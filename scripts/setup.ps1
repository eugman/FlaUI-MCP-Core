# One-time setup after cloning: install the formatter, turn on the git hooks and, in a product repo,
# add the shared core as a fetch-only `core` remote.
$ErrorActionPreference = 'Stop'
Set-Location (git rev-parse --show-toplevel)
dotnet tool restore
git config core.hooksPath .githooks
git config blame.ignoreRevsFile .git-blame-ignore-revs

$isProduct = [bool](Get-ChildItem src -Directory | Where-Object Name -like 'FlaUI.Mcp.?*')
if ($isProduct -and (git remote) -notcontains 'core') {
    git remote add core https://github.com/eugman/FlaUI-MCP-Core.git
    # Pushing from a product repo to the core would publish product code.
    git remote set-url --push core DISABLED
    git fetch core
}

Write-Host 'Hooks enabled. pre-commit checks formatting; pre-push runs scripts/check.ps1.'
