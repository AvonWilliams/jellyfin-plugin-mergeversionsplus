using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MergeVersions.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MergeVersions
{
    public class MergeVersionsManager : IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly Timer _timer;
        private readonly ILogger<MergeVersionsManager> _logger; // TODO logging
        private readonly SessionInfo _session;
        private readonly IFileSystem _fileSystem;

        public MergeVersionsManager(
            ILibraryManager libraryManager,
            ILogger<MergeVersionsManager> logger,
            IFileSystem fileSystem
        )
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _fileSystem = fileSystem;
            _timer = new Timer(_ => OnTimerElapsed(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public async Task MergeMovies(IProgress<double> progress)
        {
            _logger.LogInformation("Scanning for repeated movies");

            var duplicateMovies = GetMoviesFromLibrary()
                .GroupBy(x => x.ProviderIds["Tmdb"])
                .Where(group => group.Count() > 1)
                .ToList();

            var current = 0;
            foreach (var m in duplicateMovies)
            {
                current++;
                var percent = current / (double)duplicateMovies.Count * 100;
                progress?.Report(percent);
                _logger.LogInformation(
                    $"Merging {m.ElementAt(0).Name} ({m.ElementAt(0).ProductionYear})"
                );
                await MergeVersions(m.Select(e => e.Id).ToList());
            }
            progress?.Report(100);
        }

        public void SplitMovies(IProgress<double> progress)
        {
            var movies = GetMoviesFromLibrary();
            var current = 0;
            Parallel.ForEach(
                movies,
                async m =>
                {
                    current++;
                    var percent = current / (double)movies.Count * 100;
                    progress?.Report((int)percent);

                    _logger.LogInformation($"Spliting {m.Name} ({m.ProductionYear})");
                    await DeleteAlternateSources(m.Id);
                }
            );
            progress?.Report(100);
        }

        public async Task MergeEpisodesAsync(IProgress<double> progress)
        {
            _logger.LogInformation("Scanning for repeated episodes");

            var duplicateEpisodes = GetEpisodesFromLibrary()
                .GroupBy(x => new
                {
                    x.SeriesName,
                    x.SeasonName,
                    x.Name,
                    x.IndexNumber,
                    x.ProductionYear
                })
                .Where(x => x.Count() > 1)
                .ToList();

            var current = 0;
            foreach (var e in duplicateEpisodes)
            {
                current++;
                var percent = current / (double)duplicateEpisodes.Count * 100;
                progress?.Report((int)percent);
                _logger.LogInformation(
                    $"Merging {e.ElementAt(0).Name} ({e.ElementAt(0).ProductionYear})"
                );
                await MergeVersions(e.Select(e => e.Id).ToList());
            }
            progress?.Report(100);
        }

        public async Task SplitEpisodesAsync(IProgress<double> progress)
        {
            var episodes = GetEpisodesFromLibrary();
            var current = 0;

            foreach (var e in episodes)
            {
                current++;
                var percent = current / (double)episodes.Count * 100;
                progress?.Report((int)percent);

                _logger.LogInformation($"Spliting {e.IndexNumber} ({e.SeriesName})");
                await DeleteAlternateSources(e.Id);
            }
            progress?.Report(100);
        }

        private List<Movie> GetMoviesFromLibrary()
        {
            return _libraryManager
                    .GetItemList(
                        new InternalItemsQuery
                        {
                            IncludeItemTypes = [BaseItemKind.Movie],
                            IsVirtualItem = false,
                            Recursive = true,
                        }
                )
                .Select(m => m as Movie)
                .Where(m => m.ProviderIds.ContainsKey("Tmdb"))
                .Where(IsEligible)
                .ToList();
        }

        private List<Episode> GetEpisodesFromLibrary()
        {
            return _libraryManager
                .GetItemList(
                    new InternalItemsQuery
                    {
                        IncludeItemTypes = [BaseItemKind.Episode],
                        IsVirtualItem = false,
                        Recursive = true,
                    }
                )
                .Select(m => m as Episode)
                .Where(IsEligible)
                .ToList();
        }

        private async Task MergeVersions(List<Guid> ids)
        {
            var items = ids.Select(i => _libraryManager.GetItemById<BaseItem>(i, null))
                .OfType<Video>()
                .OrderBy(i => i.Id)
                .ToList();

            if (items.Count < 2)
            {
                return;
            }

            var primaryVersion = SelectPrimaryVersion(items);

            // Already merged with this exact item as primary under the current
            // strategy - nothing to do. This lets re-running Merge (manually or
            // on schedule) pick up a strategy change without split+re-merge.
            var currentPrimary = items.FirstOrDefault(i => i.LinkedAlternateVersions.Length > 0);
            if (currentPrimary is not null &&
                currentPrimary.Id.Equals(primaryVersion.Id) &&
                items.Where(i => !i.Id.Equals(primaryVersion.Id))
                    .All(i => string.Equals(
                        i.PrimaryVersionId,
                        primaryVersion.Id.ToString("N", CultureInfo.InvariantCulture),
                        StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var alternateVersionsOfPrimary = primaryVersion
                .LinkedAlternateVersions
                .Where(l => items.Any(i => i.Path == l.Path) && l.ItemId != primaryVersion.Id)
                .ToList();

            var alternateVersionsChanged = false;
            foreach (var item in items.Where(i =>
                !i.Id.Equals(primaryVersion.Id) &&
                !alternateVersionsOfPrimary.Any(l => l.ItemId == i.Id)))
            {
                item.SetPrimaryVersionId(
                    primaryVersion.Id.ToString("N", CultureInfo.InvariantCulture)
                );

                await item.UpdateToRepositoryAsync(
                        ItemUpdateType.MetadataEdit,
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);

                // TODO: due to check in foreach it can't be an alternate version yet?
                AddToAlternateVersionsIfNotPresent(alternateVersionsOfPrimary,
                                                new LinkedChild { Path = item.Path,
                                                                  ItemId = item.Id });

                // Exclude the new primary itself: if item was the old primary,
                // its LinkedAlternateVersions includes what's now becoming its
                // own primary and must not be copied back as a self-reference.
                foreach (var linkedItem in item.LinkedAlternateVersions
                    .Where(l => l.ItemId != primaryVersion.Id))
                {
                    AddToAlternateVersionsIfNotPresent(alternateVersionsOfPrimary,
                                                    linkedItem);
                }

                if (item.LinkedAlternateVersions.Length > 0)
                {
                    item.LinkedAlternateVersions = [];
                    await item.UpdateToRepositoryAsync(
                            ItemUpdateType.MetadataEdit,
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                }
                alternateVersionsChanged = true;
            }

            if (alternateVersionsChanged)
            {
                // The new primary may itself have previously been an alternate
                // (pointing at the old primary) - clear that before it takes
                // over as primary.
                if (!string.IsNullOrEmpty(primaryVersion.PrimaryVersionId))
                {
                    primaryVersion.SetPrimaryVersionId(null);
                }

                primaryVersion.LinkedAlternateVersions = alternateVersionsOfPrimary.ToArray();
                await primaryVersion
                    .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Chooses which item becomes the "primary" version according to the
        /// user's configured <see cref="PrimaryVersionStrategy"/>.
        /// </summary>
        private Video SelectPrimaryVersion(List<Video> items)
        {
            if (!Enum.TryParse<PrimaryVersionStrategy>(
                Plugin.Instance.PluginConfiguration.PrimaryVersionStrategy,
                ignoreCase: true,
                out var strategy))
            {
                strategy = PrimaryVersionStrategy.Default;
            }

            switch (strategy)
            {
                case PrimaryVersionStrategy.FileSize:
                    return items
                        .OrderByDescending(i => GetFileSizeSafe(i.Path))
                        .First();

                case PrimaryVersionStrategy.Bitrate:
                    return items
                        .OrderByDescending(i => i.GetDefaultVideoStream()?.BitRate ?? 0)
                        .First();

                case PrimaryVersionStrategy.Resolution:
                    return items
                        .OrderBy(i => IsNonStandardVideo(i) ? 1 : 0)
                        .ThenByDescending(i => i.GetDefaultVideoStream()?.Width ?? 0)
                        .First();

                case PrimaryVersionStrategy.Default:
                default:
                    // Original plugin behaviour: reuse an existing primary
                    // version if the library already flagged one.
                    var existingPrimary = items.FirstOrDefault(i =>
                        i.MediaSourceCount > 1 && string.IsNullOrEmpty(i.PrimaryVersionId)
                    );
                    if (existingPrimary is not null)
                    {
                        return existingPrimary;
                    }

                    return items
                        .OrderBy(i => IsNonStandardVideo(i) ? 1 : 0)
                        .ThenByDescending(i => i.GetDefaultVideoStream()?.Width ?? 0)
                        .First();
            }
        }

        private static bool IsNonStandardVideo(Video item)
        {
            return item.Video3DFormat.HasValue || item.VideoType != VideoType.VideoFile;
        }

        private long GetFileSizeSafe(string path)
        {
            try
            {
                return _fileSystem.GetFileInfo(path)?.Length ?? 0;
            }
            catch
            {
                _logger.LogWarning("Could not read file size for {Path}", path);
                return 0;
            }
        }

        private async Task DeleteAlternateSources(Guid itemId)
        {
            var item = _libraryManager.GetItemById<Video>(itemId);
            if (item is null)
            {
                return;
            }

            if (item.LinkedAlternateVersions.Length == 0 && item.PrimaryVersionId != null)
            {
                item = _libraryManager.GetItemById<Video>(Guid.Parse(item.PrimaryVersionId));
            }

            if (item is null)
            {
                return;
            }

            foreach (var link in item.GetLinkedAlternateVersions())
            {
                link.SetPrimaryVersionId(null);
                link.LinkedAlternateVersions = [];

                await link.UpdateToRepositoryAsync(
                        ItemUpdateType.MetadataEdit,
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            }

            item.LinkedAlternateVersions = [];
            item.SetPrimaryVersionId(null);
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
                .ConfigureAwait(false);
        }

        private bool IsEligible(BaseItem item)
        {
            if (IsInInactiveLibrary(item) || IsInExcludedLibrary(item))
            {
                return false;
            }
            return true;
        }

        private bool IsInExcludedLibrary(BaseItem item)
        {
           return Plugin.Instance.PluginConfiguration.LocationsExcluded != null
                  && Plugin.Instance.PluginConfiguration.LocationsExcluded
                    .Any(s => _fileSystem.ContainsSubPath(s, item.Path));
        }

        private bool IsInInactiveLibrary(BaseItem item)
        {
            if (item is not Movie)
            {
                return false;
            }

            var parentPath = item.DisplayParent?.Path;
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return false;
            }

            var virtualFolders = _libraryManager.GetVirtualFolders();

            return !virtualFolders
                .SelectMany(vf => vf.Locations ?? Array.Empty<string>())
                .Any(libPath => string.Equals(libPath, parentPath, StringComparison.OrdinalIgnoreCase) ||
                                _fileSystem.ContainsSubPath(libPath, parentPath));
        }
        private void AddToAlternateVersionsIfNotPresent(List<LinkedChild> alternateVersions,
                                                        LinkedChild newVersion)
        {
            if (!alternateVersions.Any(
                i => string.Equals(i.Path,
                                newVersion.Path,
                                StringComparison.OrdinalIgnoreCase
                            )))
            {
                alternateVersions.Add(newVersion);
            }
        }

        private void OnTimerElapsed() { }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Dispose();
                _session?.DisposeAsync();
            }
        }
    }
}
