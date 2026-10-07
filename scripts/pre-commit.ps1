# Fast checks on what is being committed; the full gate runs on push (scripts/check.ps1).
# Checks the staged copy of each C# file, not the working copy, so what is committed is what is checked.
$ErrorActionPreference = 'Stop'
Set-Location (git rev-parse --show-toplevel)
# Source files are UTF-8; the console code page would mangle non-ASCII text piped through the formatter.
$utf8 = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8

$staged = @((git diff --cached --name-only --diff-filter=ACMR -z -- '*.cs') -split "`0" | Where-Object { $_ })
if ($staged.Count -eq 0) {
    & (Join-Path $PSScriptRoot 'check-core-untouched.ps1') -Staged
    exit $LASTEXITCODE
}

# Files with no unstaged edits can be checked on disk in one formatter run. Partially staged files
# are checked from their staged copy, one at a time.
$partial = (git diff --name-only -z -- $staged) -split "`0" | Where-Object { $_ }
$whole = $staged | Where-Object { $_ -notin $partial }

$unformatted = @()
if ($whole) {
    dotnet csharpier check $whole *> $null
    if ($LASTEXITCODE -ne 0) {
        $unformatted += $whole | Where-Object { dotnet csharpier check $_ *> $null; $LASTEXITCODE -ne 0 }
    }
}

foreach ($file in $partial) {
    $blob = git show ":$file" | Out-String
    $formatted = $blob | dotnet csharpier format --write-stdout | Out-String
    if ($LASTEXITCODE -ne 0 -or $formatted.TrimEnd() -ne $blob.TrimEnd()) { $unformatted += $file }
}

if ($unformatted) {
    Write-Host 'These staged files are not formatted:' -ForegroundColor Red
    $unformatted | ForEach-Object { Write-Host "  $_" }
    $quoted = ($unformatted | ForEach-Object { "'$_'" }) -join ' '
    Write-Host "Fix and re-stage: pwsh scripts/format.ps1 $quoted; git add $quoted"
    exit 1
}

& (Join-Path $PSScriptRoot 'check-core-untouched.ps1') -Staged
exit $LASTEXITCODE
