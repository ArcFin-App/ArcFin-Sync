# SPDX-License-Identifier: AGPL-3.0-or-later
"""Checks that commit subjects follow Conventional Commits: <type>(scope): message.

    check-commits.py <base>..<head>   every commit in the range (CI, on a pull request)
    check-commits.py --file <path>    one message, as git's commit-msg hook passes it

Merge commits and git's own "Revert ..." commits are skipped, and so are fixup!/squash!
commits in the hook, since they're squashed away before they're pushed.
"""

import re
import subprocess
import sys

TYPES = ("feat", "fix", "docs", "style", "refactor", "perf", "test", "build", "ci", "chore")
SUBJECT = re.compile(rf"^(?:{'|'.join(TYPES)})(?:\([a-z0-9][a-z0-9-]*\))?!?: \S")
MAX_LENGTH = 100
SKIPPED = re.compile(r'^(Merge (branch|pull request|remote-tracking branch) |Revert ")')
HELP = f"""Commit subjects are <type>(scope): message, e.g.
  feat(library): remember the sort order per library
  fix(tv): keep the hero focused after playback
type is one of: {', '.join(TYPES)}. The scope is optional, lowercase, and names the area
or platform. Keep the subject under {MAX_LENGTH} characters. See CONTRIBUTING.md."""


def problem(subject: str) -> str | None:
    if not SUBJECT.match(subject):
        return "isn't <type>(scope): message"
    if len(subject) > MAX_LENGTH:
        return f"is {len(subject)} characters, over {MAX_LENGTH}"
    return None


def check_file(path: str) -> int:
    with open(path, encoding="utf-8") as f:
        lines = [line for line in f.read().splitlines() if not line.startswith("#")]
    subject = next((line for line in lines if line.strip()), "")
    if SKIPPED.match(subject) or subject.startswith(("fixup! ", "squash! ", "amend! ")):
        return 0
    issue = problem(subject)
    if issue:
        print(f'Commit subject "{subject}" {issue}.\n\n{HELP}', file=sys.stderr)
        return 1
    return 0


def check_range(commits: str) -> int:
    log = subprocess.run(["git", "log", "--format=%h%x09%P%x09%s", commits],
                         capture_output=True, text=True, check=True).stdout
    failed = 0
    for line in log.splitlines():
        sha, parents, subject = line.split("\t", 2)
        if len(parents.split()) > 1 or SKIPPED.match(subject):
            continue
        issue = problem(subject)
        if issue:
            print(f"::error::{sha} \"{subject}\" {issue}")
            failed += 1
    if failed:
        print(f"\n{HELP}\n\nReword them with git rebase -i, then push again.")
    else:
        print("Every commit subject follows <type>(scope): message.")
    return 1 if failed else 0


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--file":
        sys.exit(check_file(sys.argv[2]))
    if len(sys.argv) == 2:
        sys.exit(check_range(sys.argv[1]))
    sys.exit(__doc__)
