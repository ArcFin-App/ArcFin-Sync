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
2. Install the .NET 8 SDK.
3. `dotnet build` from the repo root.
4. Copy the built DLL into a local Jellyfin server's plugin directory to test.

### Stack

- C# / .NET 8
- Jellyfin plugin API (`MediaBrowser.Controller`, `MediaBrowser.Common`)
- Embedded HTML/JS for the admin config page
- YAML for advanced config (YamlDotNet)

### Code Standards

- Follow standard C# conventions (PascalCase for public members, camelCase for locals).
- No comments unless the reasoning behind the code isn't obvious from reading it.
- Keep the settings sync logic and the notification logic in separate services. Don't couple them.

### Branching

- `main`, stable, always buildable
- `v{major}.{minor}.{patch}`, active development branch
- Feature work branches off the current version branch, not `main`

### Pull Requests

- One concern per PR.
- PR description must explain why, not just what.
- All CI checks must pass before merge.
- Maintainer reviews all PRs before merge.

### AI-Assisted Code

AI tools (Claude Code) are used for assistive coding. All AI-generated code is reviewed by the maintainer before merge.

---

## Reporting Issues

Open a Gitea issue with:
- Steps to reproduce
- Expected vs. actual behavior
- Jellyfin server version, plugin version, OS
