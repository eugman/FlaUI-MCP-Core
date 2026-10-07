# Code quality

Formatting and size limits are enforced by tools, not by prose. This file is shared by the core and every product repo.

## Setup

Run `pwsh scripts/setup.ps1` once after cloning. It installs CSharpier and turns on the git hooks.

## The gates

| When | What runs |
|---|---|
| An agent edits a `.cs` file | Claude Code formats it with CSharpier (`.claude/settings.json`). Other agents should run `pwsh scripts/format.ps1 <files>`. |
| `git commit` | The staged copy of each C# file must already be formatted, and a product repo must not change files owned by the core. |
| `git push` and CI | `pwsh scripts/check.ps1`: formatting, core ownership, known debt not growing, the build with analyzers, and the no-focus unit tests. |

`pwsh scripts/format.ps1` fixes everything a tool can fix: braces, blank lines and layout.

## Limits

The build fails when new code breaks these. They are set in `.editorconfig` and `SonarLint.xml`.

| Rule | Limit |
|---|---|
| S104 | 400 lines of code per file |
| S138 | 60 lines per method |
| S3776 | cognitive complexity 15 per method |
| S134 | nesting depth 3 |
| S1067 | 3 operators per condition |
| S3358 | no nested ternaries |
| S107 | 7 parameters |
| S1200 | 30 types coupled to one class |
| IDE0011, IDE2001, IDE2003 | braces on every block, one statement per line, a blank line after each block |

Existing files that break a limit are listed under "Known debt" at the end of `.editorconfig`. A product repo keeps its own list in an `.editorconfig` inside its project folder. When you fix a file, delete its section. Never add one: `check.ps1` fails when the number of exemptions or `#pragma warning disable` lines grows compared with the upstream branch.

Magic numbers (S109) are reported as suggestions in the IDE but do not fail the build.

## How to write code here

- Make the smallest change that fits the existing code, and reuse what exists before adding a helper.
- No speculative abstractions, options or dependencies.
- One statement per line, blank lines between steps, and named constants instead of magic numbers.
- Call types directly instead of through string-keyed dispatch.

## Shared core

Files listed in `scripts/core-paths.txt` belong to [FlaUI-MCP-Core](https://github.com/eugman/FlaUI-MCP-Core). Product repos change them only by merging it:

```powershell
git remote add core https://github.com/eugman/FlaUI-MCP-Core.git   # once
git fetch core
git merge core/main
```
