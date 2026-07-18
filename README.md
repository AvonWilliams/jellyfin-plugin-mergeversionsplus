<h1 align="center">Jellyfin Merge Versions Plus</h1>
<h3 align="center">A fork of <a href="https://github.com/danieladov/jellyfin-plugin-mergeversions">danieladov/jellyfin-plugin-mergeversions</a></h3>

<p align="center">
Groups repeated movies and episodes into a single library entry with selectable versions —
same as the original plugin, but with a configurable strategy for which version becomes
the default/primary one when merged.
</p>

## What's different from upstream

The original plugin picks the "primary" version using internal, non-configurable logic
(reuse an existing flagged primary if one exists, otherwise highest resolution). This can
occasionally pick the wrong file — e.g. a small placeholder/sample file — with no way to
override it.

This fork adds a **"Nominate primary version by"** dropdown on the plugin's configuration
page with four strategies:

- **Default** — original plugin behaviour (unchanged, safe upgrade path)
- **Largest file size**
- **Highest resolution**
- **Highest bitrate**

## Install Process

This fork isn't published to a plugin repository/manifest — install manually:

1. Build the plugin (see Build Process below), or download a pre-built `.dll` from the
   [Releases](https://github.com/AvonWilliams/jellyfin-plugin-mergeversionsplus/releases) page if available.
2. Place `Jellyfin.Plugin.MergeVersions.dll` in a folder called `plugins/Merge Versions Plus`
   under Jellyfin's program data directory (e.g. `/var/lib/jellyfin/plugins/Merge Versions Plus/`
   on a typical Linux/LXC install).
3. Restart Jellyfin.
4. Go to Dashboard -> Plugins -> Merge Versions Plus to configure the primary version
   strategy and excluded paths.

> **Note:** if you have the original `danieladov` Merge Versions plugin installed, remove
> it first (or use a different plugins subfolder) to avoid both plugins scanning/merging
> the same library. This fork uses its own plugin GUID, so Jellyfin will treat it as a
> separate plugin rather than an upgrade of the original.

## Build Process

1. Clone this repository
2. Ensure the .NET SDK matching the `TargetFramework` in
   `Jellyfin.Plugin.MergeVersions.csproj` is installed
3. Build:
```sh
dotnet publish --configuration Release --output bin
```
4. Copy the resulting `Jellyfin.Plugin.MergeVersions.dll` from `bin/` into
   `plugins/Merge Versions Plus/` under Jellyfin's program data directory
5. Restart Jellyfin

## User Guide

1. Set your preferred primary version strategy on the plugin's configuration page, then
   save.
2. Merge your movies or episodes from the Scheduled Task or directly from the plugin
   configuration page.
3. Splitting is only available through the configuration page.
4. Changing the strategy does **not** retroactively re-evaluate already-merged items —
   split and re-merge affected items for the new strategy to take effect on them.

## Attribution

Based on [jellyfin-plugin-mergeversions](https://github.com/danieladov/jellyfin-plugin-mergeversions)
by [danieladov](https://github.com/danieladov), MIT licensed. See `LICENSE`.
