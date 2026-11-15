# Arcfin Sync

![Version](https://img.shields.io/badge/version-0.2.0-blue)
![License](https://img.shields.io/badge/license-AGPL--3.0-blue)

A Jellyfin server plugin for [Arcfin](https://git.psmattas.com/ArcFin/Arcfin-iOS). Installs on your Jellyfin server and syncs Arcfin app settings across a user's devices, gives the admin control over which settings can be changed, and adds notification support (new media, admin broadcasts, Seerr request availability) that Jellyfin doesn't have on its own.

## What it does

- Syncs Arcfin app settings between a user's devices. Cross-platform settings (playback defaults, subtitle preferences, and similar) sync everywhere. Platform-specific settings (macOS window behavior, tvOS focus and UI preferences, and similar) stay on that platform only and never sync across.
- Gives the admin a settings page in the Jellyfin dashboard to lock, unlock, or prefill any setting, for one user or all users.
- Advanced YAML override section for admins who want to set config outside the UI.
- Sends notifications to Arcfin clients: new media added, admin-authored announcements, and Seerr request-available alerts via webhook.

## Status

v0.2.0 in development. Installable via the repository manifest (manifest.json) once the release is published on Gitea.

## Requirements

- Jellyfin server 12.1+
- .NET 10 SDK to build from source

## Installing

Add this repository to Jellyfin: Dashboard > Plugins > Repositories > Add, then paste:

```
https://git.psmattas.com/ArcFin/Arcfin-Sync/media/branch/main/manifest.json
```

Arcfin Sync will show up in Dashboard > Plugins > Catalog once a release is published.

## Building from source

```
dotnet build
```

Output goes to `bin/Debug/net10.0/Jellyfin.Plugin.ArcfinSync.dll`. Copy it into your Jellyfin server's plugin directory and restart the server to load it.

## Related

- [Arcfin](https://git.psmattas.com/ArcFin/Arcfin-iOS): the client app this plugin serves.

## License

AGPL-3.0-or-later. See [LICENSE](LICENSE).
