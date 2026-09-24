# Security Policy

## Supported Versions

Only the latest release on `main` is actively supported.

| Version | Supported |
|---|---|
| Latest (`main`) | Yes |
| Older branches | No |

## Reporting a Vulnerability

**Do not open a public Gitea issue for security vulnerabilities.**

Report privately to: **security@psmattas.com**

Include in your report:
- Description of the vulnerability
- Steps to reproduce
- Potential impact
- Suggested fix, if you have one

You will receive an acknowledgement within 48 hours. If the vulnerability is confirmed, a fix will be prioritised and you will be credited, unless you prefer anonymity.

## Scope

Security issues relevant to Arcfin Sync include:

- Credential or token exposure (Jellyfin API keys, admin tokens)
- Authorization bypass on the plugin's settings API (a user changing another user's settings, or a locked setting being changed by a non-admin)
- Unvalidated webhook payloads (Seerr or other sources) being trusted or executed unsafely
- YAML config parsing vulnerabilities (unsafe deserialization)
- Privacy leaks of user settings or notification data to third parties

## Out of Scope

- Vulnerabilities in Jellyfin server itself, or in Seerr
- Issues requiring physical server access
- Theoretical attacks with no practical exploit path

## Disclosure Policy

Coordinated disclosure. Once a fix is released, the vulnerability may be publicly disclosed with credit to the reporter.
