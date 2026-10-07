# Contributing

## Setup

You need Windows 10 or 11, the .NET 9 SDK (9.0.300 or later; see `global.json`), the .NET 8 Desktop Runtime and PowerShell 7.

```powershell
pwsh scripts/setup.ps1
```

This installs CSharpier and turns on the git hooks. The pre-commit hook checks formatting; the pre-push hook runs `pwsh scripts/check.ps1`, the same gate CI runs. The rules are described in [docs/code-quality.md](docs/code-quality.md).

## Tests

| Project | Needs the desktop |
|---|---|
| `tests/FlaUI.Mcp.Tests` | No |
| `tests/FlaUI.Mcp.IntegrationTests` | Yes: launches the WinForms and WPF apps in `tests/TestApps` and takes focus |

## The core and the product repos

This repository is the generic server. Product repos, such as the Tabular Editor 3 one, contain this repository's history and add their own host project. They take core changes by merging:

```powershell
git remote add core https://github.com/eugman/FlaUI-MCP-Core.git   # once
git fetch core
git merge core/main
```

A product repo never edits core-owned files (listed in `scripts/core-paths.txt`). `scripts/check-core-untouched.ps1` enforces this in its hooks and CI. Make the change here first, then merge it.

Keep this repository free of product-specific code and wording.

## Releases

The **Release** workflow builds x64 and ARM64 ZIPs. Push a version tag such as `v0.2.0`, or run the workflow by hand with a tag name; manual runs create a draft release.
