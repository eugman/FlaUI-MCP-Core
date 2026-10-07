# Agent notes

- Code quality rules and the commands that enforce them are in [docs/code-quality.md](docs/code-quality.md). Run `pwsh scripts/check.ps1` before you push.
- `tests/FlaUI.Mcp.IntegrationTests` launches test apps and takes desktop focus. Run it only when the person at the machine agrees. `scripts/check.ps1` runs only the no-focus tests.
- This repository is the shared core. Keep it free of product-specific code and wording; that belongs in the product repos.
