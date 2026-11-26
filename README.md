# Arcfin Sync

[![Version](https://arcfin.psmattas.com/badges/plugin.svg)](https://git.psmattas.com/ArcFin/Arcfin-Sync/releases)
![License](https://img.shields.io/badge/license-AGPL--3.0-blue)

A Jellyfin server plugin for [Arcfin](https://git.psmattas.com/ArcFin/ArcFin). Installs on your Jellyfin server, keeps each user's Arcfin settings in step across their devices, and lets the server's administrators set defaults and locks for those settings.

> **Where Arcfin Sync lives:** the code, pull requests and releases are on [git.psmattas.com/ArcFin/Arcfin-Sync](https://git.psmattas.com/ArcFin/Arcfin-Sync), and its issues go in the [ArcFin issue tracker](https://git.psmattas.com/ArcFin/ArcFin/issues). The copy on GitHub is a read-only mirror, so please open issues and pull requests on git.psmattas.com.

## What it does

- **Settings catalogue.** Carries the same list of Arcfin settings as the app (key, type, title, default, choices, which platforms show it) and serves it to signed-in users. Only settings in the catalogue are stored.
- **Settings sync.** Keeps a user's synced settings in step across their iPhone, iPad, Mac and Apple TV, and remembers which device changed them last, so Arcfin can sync automatically, ask first, or not sync on a device. A device can send its settings to the user's other devices.
- **Devices.** Keeps a record of each device a user signs in to with Arcfin (name, platform, app version, sync mode, last seen, its settings). Users can rename or forget their devices; administrators can for anyone.
- **Defaults and locks.** Administrators give any setting a default (used until someone picks their own) or lock it (everyone gets that value and can't change it), for everyone or for one user. A per-user override can also unlock a setting for that person.
- **Pick Together.** People on the server swipe through titles on iPhone and iPad until they agree on one, then watch it on one device, on another, or together over SyncPlay. They join with a code or a QR code in Arcfin, and each person only gets titles everyone in the session is allowed to see. The device that starts a session hosts it: it deals new titles, removes people and locks the session. Administrators turn it on or off for everyone or for chosen users. Needs Arcfin 0.5.0 (build 3) or later.
- **Watch parties.** Arcfin hosts a SyncPlay group and guests join with a six-character code or QR code. Parties can be planned up to 30 days ahead, and the host sees what time it'll be for each guest. People in other SyncPlay apps can join the group; the host needs Arcfin.
- **Seerr and TMDB through the server.** The API keys are kept on the server, encrypted with AES-256-GCM, and never reach a device. Every Seerr call is made as the person using it, on an allowlist of routes. Requests made while Seerr is down wait in a queue and are sent once it's back. Seerr can have a local address for the server and a public one for the apps.
- **Signed commands.** A P-256 key, rotated on a schedule if you like, signs commands Arcfin carries out: clear a PIN, lock or unlock a profile (and optionally disable the Jellyfin account), wipe a lost device, and PIN rules. Devices trust the key over HTTPS by themselves, and over HTTP once someone checks its fingerprint. Rules for devices that fall behind, older Arcfin versions, trusted devices (a new device waits until a trusted one approves it) and requiring HTTPS away from home.
- **Watch time and bedtime.** Daily minutes and a bedtime per person, in their own time zone, counted across every app, with warnings 10 and 5 minutes before.
- **Household.** Download limits (titles and GB for videos; albums, songs and GB for music, counted once across a person's devices), shared accounts that ask who's watching and copy progress to each person, announcements, and planned maintenance.
- **Notifications.** Arcfin tells people when a Seerr request of theirs is approved, declined or available; tells the people who manage requests about requests waiting for approval, approved automatically or failed; tells whoever reported an issue and the people who manage issues about new issues, comments, and issues resolved or reopened; and tells everyone about announcements and, a day ahead, planned maintenance. The server watches Seerr, within seconds when Seerr's webhook is set up and otherwise every ten minutes, and keeps each person's news for two weeks. Arcfin asks for it when it opens and when the system lets it run in the background, so it isn't instant push (see [#757](https://git.psmattas.com/ArcFin/ArcFin-Apple/issues/757)). Needs Arcfin with notification support.
- **Hidden titles and recommendations.** Titles hidden from Continue Watching and Next Up are kept per person on the server. Top Picks, Because You Watched and Popular on This Server rows for Home.
- **Live sessions and history.** Administrators can end, lock, remove people from or reschedule any watch party or Pick Together session, and keep their history for a set number of days or for ever.
- **Opt-in.** Every feature above is off until an administrator turns it on; anything that was already in use when the plugin updates stays on. Arcfin works the same with or without the plugin.
- **Configuration page.** One page in the Jellyfin dashboard with Overview, Settings, Users & Devices, Features, Seerr & TMDB, Security, Watch Time, Household and Downloads tabs.
- **Audio tracks for downloads.** Copies one audio track of an item out as AAC in an .m4a with the server's ffmpeg, so a converted download can keep more than one audio track.
- **Encoder check.** Reports which video encoders (H.264, HEVC, AV1) really work with the server's hardware acceleration, from a one-frame test encode per codec.

## Status

v0.7.1, for Arcfin 0.5.0 (build 5 or later for the visualizer settings; build 4 or later for notifications and issues; build 3 or later for the other 0.6 features). Install it from the repository manifest below.

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
