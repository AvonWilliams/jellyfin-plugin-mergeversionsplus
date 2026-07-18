using MediaBrowser.Model.Plugins;
using System;

namespace Jellyfin.Plugin.MergeVersions.Configuration
{
    /// <summary>
    /// Determines how the "primary" version is chosen when multiple
    /// versions of a movie/episode are merged together.
    /// </summary>
    public enum PrimaryVersionStrategy
    {
        /// <summary>
        /// Original plugin behaviour: reuses an existing primary version if one is
        /// already flagged, otherwise falls back to highest resolution (width).
        /// </summary>
        Default,

        /// <summary>Largest file on disk becomes the primary version.</summary>
        FileSize,

        /// <summary>Highest video resolution (width) becomes the primary version.</summary>
        Resolution,

        /// <summary>Highest video bitrate becomes the primary version.</summary>
        Bitrate
    }

    public class PluginConfiguration : BasePluginConfiguration
    {

        public String[] LocationsExcluded { get; set; }

        /// <summary>
        /// Strategy used to nominate which version becomes primary/default
        /// when merging. Stored as a string (matching enum names in
        /// <see cref="PrimaryVersionStrategy"/>) rather than the enum itself,
        /// to avoid ambiguity between numeric and string enum JSON
        /// serialization across Jellyfin's config API and the config page's
        /// plain HTML &lt;select&gt;. Parsed explicitly wherever it's used.
        /// </summary>
        public String PrimaryVersionStrategy { get; set; }

        public PluginConfiguration()
        {
            LocationsExcluded = Array.Empty<String>();
            PrimaryVersionStrategy = Configuration.PrimaryVersionStrategy.Default.ToString();
        }
    }
}
