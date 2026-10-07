# Fix everything the gates check that a tool can fix: braces and blank lines, then layout.
# Pass file paths to format only those files. (The agent post-edit hook runs only CSharpier, for speed;
# a missing brace is still caught by the build.)
param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $Paths)

$ErrorActionPreference = 'Stop'
# Resolve paths before moving to the repo root, so paths relative to a subfolder still work.
$Paths = @($Paths | Where-Object { $_ } | ForEach-Object { (Resolve-Path $_).Path })
Set-Location (git rev-parse --show-toplevel)
$rules = 'IDE0011', 'IDE0161', 'IDE2000', 'IDE2001', 'IDE2003'

function Run([scriptblock] $command) {
    & $command
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($Paths) {
    Run { dotnet format style FlaUI.Mcp.slnx --diagnostics $rules --severity warn --include $Paths }
    Run { dotnet csharpier format $Paths }
}
else {
    Run { dotnet format style FlaUI.Mcp.slnx --diagnostics $rules --severity warn }
    Run { dotnet csharpier format . }
}
