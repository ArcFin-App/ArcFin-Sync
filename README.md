# Arcfin Sync

![Version](https://img.shields.io/badge/version-0.4.0-blue)
![License](https://img.shields.io/badge/license-AGPL--3.0-blue)

A Jellyfin server plugin for [Arcfin](https://git.psmattas.com/ArcFin/ArcFin). Installs on your Jellyfin server, keeps each user's Arcfin settings in step across their devices, and lets the server's administrators set defaults and locks for those settings.

## What it does

- **Settings catalogue.** Carries the same list of Arcfin settings as the app (key, type, title, default, choices, which platforms show it) and serves it to signed-in users. Only settings in the catalogue are stored.
- **Settings sync.** Keeps a user's synced settings in step across their iPhone, iPad, Mac and Apple TV, and remembers which device changed them last, so Arcfin can sync automatically, ask first, or not sync on a device. A device can send its settings to the user's other devices.
- **Devices.** Keeps a record of each device a user signs in to with Arcfin (name, platform, app version, sync mode, last seen, its settings). Users can rename or forget their devices; administrators can for anyone.
- **Defaults and locks.** Administrators give any setting a default (used until someone picks their own) or lock it (everyone gets that value and can't change it), for everyone or for one user. A per-user override can also unlock a setting for that person.
- **Configuration page.** One page in the Jellyfin dashboard with Overview, Settings and Users & Devices tabs.
- **Audio tracks for downloads.** Copies one audio track of an item out as AAC in an .m4a with the server's ffmpeg, so a converted download can keep more than one audio track.
- **Encoder check.** Reports which video encoders (H.264, HEVC, AV1) really work with the server's hardware acceleration, from a one-frame test encode per codec.

## Status

v0.4.0, for Arcfin 0.5.0. Install it from the repository manifest below.

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

Output goes to `bin/Debug/net10.0/Jellyfin.Plugin.ArcfinSync.dll`. Tests are in `tests/` (`dotnet test tests/Jellyfin.Plugin.ArcfinSync.Tests`). Copy it into your Jellyfin server's plugin directory and restart the server to load it.

## Related

- [Arcfin](https://git.psmattas.com/ArcFin/ArcFin): the client app this plugin serves.

## License

AGPL-3.0-or-later. See [LICENSE](LICENSE).
