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
- **Arcs.** A vertical feed of short scenes (15 to 60 seconds) from each person's own films and episodes, for Arcfin on iPhone and iPad. See [Arcs](#arcs) below.
- **Live sessions and history.** Administrators can end, lock, remove people from or reschedule any watch party or Pick Together session, and keep their history for a set number of days or for ever.
- **Opt-in.** Every feature above is off until an administrator turns it on; anything that was already in use when the plugin updates stays on. Arcfin works the same with or without the plugin.
- **Configuration page.** One page in the Jellyfin dashboard with Overview, Settings, Users & Devices, Features, Seerr & TMDB, Security, Watch Time, Household, Downloads and Arcs tabs.
- **Audio tracks for downloads.** Copies one audio track of an item out as AAC in an .m4a with the server's ffmpeg, so a converted download can keep more than one audio track.
- **Encoder check.** Reports which video encoders (H.264, HEVC, AV1) really work with the server's hardware acceleration, from a one-frame test encode per codec.

## Arcs

Arcs cuts short clips out of the films and episodes each person can see and picks them for that person. Clips only come from titles their parental rating and library access allow, from the libraries the administrator chose, and never from titles the administrator banned.

- **Where clips start.** At each chapter, two seconds in, as long as the chapter or the chosen length, whichever is shorter. Intros, recaps, credits, previews and adverts are skipped when Jellyfin has media segments for the title; without them the first 6 % and last 8 % are. Titles without chapters use the "Arcs scene index" scheduled task, which looks for scene changes in each file's keyframes with Jellyfin's ffmpeg, decoding on the GPU when Jellyfin transcodes with NVIDIA NVENC and on the CPU otherwise. It scans one title at a time by default; the administrator can run up to eight at once, which finishes sooner but uses much more of the GPU or CPU while it runs. Only new and changed files are scanned again. Until it has run, clips start 20, 40 and 60 % in. A clip reported by two people is never used again.
- **Spoilers.** In spoiler-safe mode (the default, and always when the administrator says so) an episode is only used once the person has watched it or a later one, a show they haven't started only gives its first three episodes, and a film they haven't seen never gives a clip from its last quarter.
- **How clips are picked.** Each title is scored on how well it fits the person's taste (genres, people, tags and studios from what they've watched, liked and favourited in Jellyfin, plus what they did in Arcs), on what people with similar taste on the server responded to, on how they responded to it themselves, on being new to the library, and on being popular here. Going on to watch the full title counts most, then likes, shares, rewatches and watching a clip to the end; skipping within a few seconds and dislikes count against. The administrator can change how much each of these counts.
- **Variety.** Never the same film or show twice in a row, at most two clips from one title in ten, a mix of genres, and about one clip in six from outside the person's usual taste. Each clip says why it was picked.
- **People's own choices.** "Not now" holds a title back for a week, "fewer like this" halves it for a month, and hiding it keeps it out for good. Everyone can see and undo these, or reset their Arcs history.
- **Feeds.** For You, Continue (scenes from what they're in the middle of, only from what they've already seen), Discover (titles they've never played), one show or film shuffled, and their liked clips. A clip of the day can show on Home.
- **Moods and reactions.** People can react to a clip (funny, wow or sad) and pick a mood for the feed (funny, action, feel-good, scary, drama or sci-fi), which keeps to that mood's genres and lifts titles people reacted to in its way. A reaction counts as liking the title a little.
- **Dialogue search.** People can find scenes by a line of dialogue. The "Arcs dialogue index" scheduled task reads text subtitles (SRT, ASS, SSA and WebVTT files beside a title, and text streams inside it) with Jellyfin's ffmpeg, in the subtitle languages people here chose, at most three per title, and only reads new or changed titles again. Each title's lines are kept once, in a small file in the plugin's data folder (roughly 60 to 100 KB per language for a film, 20 to 40 KB for an episode), plus a word filter of a few kilobytes per language in a shared list. A search matches every word in one line or in two lines in a row, ignoring case and accents, puts lines with the words in order first, and follows the same spoiler, library and skip rules as the feed.
- **Best scenes.** A film's or show's best scenes: the ones the most people liked, went on to watch from or shared (only where at least two did), then its cold opens and named chapters.
- **Friends and sending scenes.** Everyone has a friend code (eight letters and digits, like K7Q2-MX9P). Entering someone's code makes the two of you friends, both ways, so nobody sees who else is on the server, hidden accounts included. People can send a scene, with a short note, to a friend who can see that title; it comes first in their For You and, with notifications on, they're told. Up to 30 sends a day each, 50 waiting in an inbox, 100 friends, and 10 wrong codes an hour. A new code can be made at any time; friends stay. Resetting Arcs history keeps friends and the code. The administrator can turn sending off.
- **Requests.** With Seerr set up on the Seerr & TMDB tab, about one clip in eight can be the trailer of a title that isn't on the server, found from what the person liked or watched most, to request through Seerr. Titles already here or already requested are left out, as are adult titles and, where the certification is known, titles above the person's parental rating. Shown only to people who may request in Seerr, and the administrator can turn it off. Each person can turn the YouTube trailers off in Arcfin, so these show the title's poster instead; the administrator can turn them off for everyone.
- **My stats.** Each person can see their own totals: scenes watched, minutes, likes, shares, the titles they watched most scenes of, their most rewatched scene and their usual reaction.
- **Privacy.** What other people did only counts where at least two of them did it, and like and reaction counts only show from two people. Administrators see totals, never one person's activity. Each person's last 2000 Arcs events are kept on the server.
- **Video.** Clips are made with Jellyfin's ffmpeg as H.264 and AAC in 720p, or 480p to save data, kept in the plugin's data folder and removed oldest-used first to stay under the administrator's cache size. The feed streams each clip as HLS, so it starts playing as soon as its first two seconds are made; shared clips are whole MP4s. When Jellyfin transcodes with NVIDIA NVENC, clips are decoded, scaled, tone mapped and encoded on the card, and fall back to the CPU if that fails; other hardware acceleration types use the CPU for clips. The first clips of each page are made ahead. Sharing a clip can be turned off.

Arcs is off until an administrator turns it on, on the Arcs tab.

## Status

v0.8.0, for Arcfin 0.5.0 (build 6 or later for Arcs; build 5 or later for the visualizer settings; build 4 or later for notifications and issues; build 3 or later for the other 0.6 features). Install it from the repository manifest below.

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
