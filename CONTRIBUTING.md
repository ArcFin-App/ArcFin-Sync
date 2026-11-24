# Contributing

Contributions are welcome. Arcfin Sync is maintained day to day by one person, so response times won't always be fast, but PRs and issues from anyone are genuinely wanted.

## License and the DCO

Arcfin Sync is licensed under [AGPL-3.0-or-later](LICENSE). Every commit needs a **Developer Certificate of Origin (DCO)** sign-off, a `Signed-off-by:` trailer certifying you wrote the change or otherwise have the right to submit it under the project's license. Add it automatically with:

```
git commit -s
```

No CLA, no copyright assignment. You keep copyright on your own contributions. Signing off just licenses your contribution to the project under the same AGPL-3.0-or-later terms as the rest of the codebase. PRs without a sign-off will be asked to add one before merge.

---

## Getting Started

1. Clone the repo.
2. Install the .NET 10 SDK.
3. `dotnet build -c Release` from the repo root, and `dotnet test tests/Jellyfin.Plugin.ArcfinSync.Tests` for the tests.
4. Copy `bin/Release/net10.0/Jellyfin.Plugin.ArcfinSync.dll` into a test Jellyfin server's plugin folder and restart it.
5. Turn on the commit message hook once: `git config core.hooksPath scripts/git-hooks`.

### Stack

- C# / .NET 10, for Jellyfin 12.1 and later
- Jellyfin plugin API (`MediaBrowser.Controller`, `MediaBrowser.Common`)
- Embedded HTML/JS for the dashboard page
- xUnit tests in `tests/`

### Code Standards

- Follow standard C# conventions (PascalCase for public members, camelCase for locals).
- No comments unless the reasoning behind the code isn't obvious from reading it.
- Keep rules that don't need Jellyfin (the settings rules, Pick Together's sessions) free of Jellyfin types, with their own tests.

### Branching

- `main` is stable and always builds. Branch from an up-to-date `main` and open your PR against it.

### Commit messages

Commit subjects follow [Conventional Commits](https://www.conventionalcommits.org), as in the Arcfin app: `<type>(scope): message`, for example `feat(pick-together): let the host lock a session`. The types are `feat`, `fix`, `docs`, `style`, `refactor`, `perf`, `test`, `build`, `ci` and `chore`; the scope is optional and lowercase. Keep the subject under 100 characters, and use the body to say why. CI checks every commit in a PR (merge commits and `Revert "…"` commits are skipped), and the hook above checks them as you commit.

### Pull Requests

- One concern per PR.
- PR description must explain why, not just what.
- All CI checks must pass before merge: the build and tests, the version and manifest check, sign-off and commit messages.
- Maintainer reviews all PRs before merge.

### Releasing

A release is one PR that raises the version in `Jellyfin.Plugin.ArcfinSync.csproj` (Version, AssemblyVersion and FileVersion), `build.yaml` (version and changelog) and the README, and adds the release's entry at the top of `manifest.json`. CI checks they match and that the entry points at `releases/download/v<major.minor.patch>/arcfin-sync-<version>.zip`. Build the zip (the DLL alone), put its MD5 in the entry, and after the merge tag `v<major.minor.patch>` (signed) and publish the release with the zip attached. Publishing runs the Release zips workflow, which downloads every zip the manifest lists and checks its MD5; it also runs every Monday.

### AI-Assisted Code

AI tools (Claude Code) are used for assistive coding. All AI-generated code is reviewed by the maintainer before merge.

---

## Reporting Issues

Open a Gitea issue with:
- Steps to reproduce
- Expected vs. actual behavior
- Jellyfin server version, plugin version, OS
