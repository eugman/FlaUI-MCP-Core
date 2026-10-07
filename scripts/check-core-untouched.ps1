# Fails when a product repo changes files owned by the shared core (see scripts/core-paths.txt).
# Core changes belong in FlaUI-MCP-Core; product repos receive them with `git merge core/main`.
# In the core repo itself there is nothing to check.
param([switch]$Staged)

$ErrorActionPreference = 'Stop'
$root = git rev-parse --show-toplevel

# A product repo has a host project next to the core one: src/FlaUI.Mcp.<Product>.
$isProduct = [bool](Get-ChildItem (Join-Path $root 'src') -Directory | Where-Object Name -like 'FlaUI.Mcp.?*')
if (-not $isProduct) { exit 0 }

if ((git remote) -notcontains 'core') {
    Write-Host 'This product repo has no `core` remote, so core ownership cannot be checked.' -ForegroundColor Red
    Write-Host 'Run: pwsh scripts/setup.ps1'
    exit 1
}

$corePaths = Get-Content (Join-Path $root 'scripts/core-paths.txt') |
    Where-Object { $_ -and -not $_.StartsWith('#') }

if ($Staged) {
    # A merge commit is how core changes arrive. Anything extra it slips in is caught on push and in CI,
    # which compare the whole branch with core/main.
    git rev-parse -q --verify MERGE_HEAD *> $null
    if ($LASTEXITCODE -eq 0) { exit 0 }
    # --no-renames: a file moved out of a core folder must count as a change to the core.
    $changed = git diff --cached --name-only --no-renames
}
else {
    git rev-parse -q --verify core/main *> $null
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'core/main is not fetched. Run: git fetch core' -ForegroundColor Red
        exit 1
    }

    $base = git merge-base HEAD core/main
    $changed = git diff --name-only --no-renames $base HEAD
}

$touched = $changed | Where-Object { $file = $_; $corePaths | Where-Object { $file.StartsWith($_) } }
if ($touched) {
    Write-Host 'These files belong to the shared core (FlaUI-MCP-Core):' -ForegroundColor Red
    $touched | ForEach-Object { Write-Host "  $_" }
    Write-Host 'Make the change in FlaUI-MCP-Core, then run: git fetch core; git merge core/main'
    exit 1
}

exit 0
