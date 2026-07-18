# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Jellyfin plugin (fork of [danieladov/jellyfin-plugin-mergeversions](https://github.com/danieladov/jellyfin-plugin-mergeversions)) that groups repeated movies/episodes into a single library entry with selectable versions. The fork's distinguishing feature: a configurable strategy for which version becomes the "primary" one (upstream's logic for this is fixed and can pick the wrong file, e.g. a small placeholder/sample).

## Build

```sh
dotnet publish --configuration Release --output bin
```

Requires the .NET SDK version matching `TargetFramework` in `Jellyfin.Plugin.MergeVersions/Jellyfin.Plugin.MergeVersions.csproj` (currently `net9.0`). No test suite exists in this repo.

To deploy locally: copy `bin/Jellyfin.Plugin.MergeVersions.dll` into `plugins/Merge Versions Plus/` under Jellyfin's program data directory, then restart Jellyfin.

## Git workflow

Before every `git push`, bump the version — increment `AssemblyVersion`/`FileVersion` in `Jellyfin.Plugin.MergeVersions.csproj`, and `version` in `build.yaml` and the relevant entry in `manifest.json`, keeping them in sync.

## Architecture

Single-project plugin (`Jellyfin.Plugin.MergeVersions/`), standard Jellyfin plugin shape:

- **`Plugin.cs`** — plugin entry point, exposes `Plugin.Instance` (singleton) and `PluginConfiguration`. Registers the config page (`Configuration/configurationpage.html`) as an embedded resource.
- **`Configuration/PluginConfiguration.cs`** — persisted settings: `LocationsExcluded` (paths to skip) and `PrimaryVersionStrategy` (stored as a *string*, not the enum, to avoid ambiguity between numeric/string enum JSON serialization across Jellyfin's config API and the plain HTML `<select>` on the config page — always parsed explicitly at the point of use, see `MergeVersionsManager.SelectPrimaryVersion`).
- **`Configuration/configurationpage.html`** — the plugin's settings UI (vanilla JS against `ApiClient`/`Dashboard` globals, no build step). Owns the `PrimaryVersionStrategy` `<select>` and the excluded-locations checkbox list; posts to the `MergeVersions` API routes and reads/writes plugin config directly via `ApiClient.getPluginConfiguration`/`updatePluginConfiguration`.
- **`MergeVersionsManager.cs`** — all core logic:
  - `MergeMovies`/`MergeEpisodesAsync` find duplicates (movies grouped by TMDb provider ID; episodes grouped by series/season/name/index/year) and merge them into one primary + linked alternate versions.
  - `SelectPrimaryVersion` implements the configurable strategy (`Default`, `FileSize`, `Resolution`, `Bitrate`) — this is the fork's core addition over upstream. `Default` preserves original plugin behavior (reuse existing flagged primary, else highest resolution) as a safe upgrade path.
  - `SplitMovies`/`SplitEpisodesAsync` undo merges.
  - `IsEligible`/`IsInExcludedLibrary`/`IsInInactiveLibrary` filter items against `LocationsExcluded` and inactive virtual folders before they're considered for merging.
  - Changing the strategy is retroactive: `MergeMovies`/`MergeEpisodesAsync` re-evaluate already-merged groups on every run (not just newly-duplicated ones) and re-primary them if the strategy picks a different version. `MergeVersions` short-circuits (no writes) when the selected primary already matches, and clears self-reference/stale `PrimaryVersionId` state when swapping an existing alternate into the primary slot.
- **`Api/MergeVersionsController.cs`** — REST endpoints (`MergeVersions/MergeMovies`, `SplitMovies`, `MergeEpisodes`, `SplitEpisodes`), each backed by a fresh `MergeVersionsManager` instance. Called both by the config page buttons and by the scheduled tasks.
- **`ScheduledTasks/RefreshLibraryTask.cs`** — defines `MergeMoviesTask` and `MergeEpisodesTask`, each running every 24h by default, both just delegating to `MergeVersionsManager`.

Plugin GUID (`2e02b900-aaf6-4415-b304-44f0c6f8c162`) is unique to this fork — deliberately different from upstream's so both can coexist as far as Jellyfin is concerned, though running both against the same library is not recommended (README warns to remove upstream first).

Version/release metadata lives in three places and must be kept consistent: `Jellyfin.Plugin.MergeVersions.csproj` (`AssemblyVersion`/`FileVersion`), `build.yaml` (`version`), and `manifest.json` (per-version `version`/`changelog`/`sourceUrl`/`checksum`).
