# The full local gate, also run by the pre-push hook and CI: formatting, analyzers, no-focus tests.
# Integration tests are excluded because they launch apps and take desktop focus.
$ErrorActionPreference = 'Stop'
Set-Location (git rev-parse --show-toplevel)

function Step($name, [scriptblock]$command) {
    Write-Host "== $name" -ForegroundColor Cyan
    & $command
    if ($LASTEXITCODE -ne 0) { Write-Host "FAILED: $name" -ForegroundColor Red; exit $LASTEXITCODE }
}

# Every way to exempt code from the analyzers, counted at a revision: rules downgraded in any
# .editorconfig or .globalconfig, pragmas, SuppressMessage attributes and NoWarn. The total may only shrink.
function DebtCount($revision) {
    $patterns = @(
        @('^\s*dotnet_diagnostic\.\S+\.severity\s*=\s*(none|silent|suggestion)', '*.editorconfig', '*.globalconfig'),
        @('#pragma warning disable', '*.cs'),
        @('SuppressMessage\(', '*.cs'),
        @('<NoWarn>', '*.csproj', '*.props', '*.targets')
    )
    $count = 0
    foreach ($pattern in $patterns) {
        $hits = git grep -c -E $pattern[0] $revision -- $pattern[1..($pattern.Count - 1)]
        $count += ($hits | ForEach-Object { [int]($_ -split ':')[-1] } | Measure-Object -Sum).Sum
    }

    return $count
}

# The revision to compare with: CI passes DEBT_BASE (the previous push, or a pull request's base).
# Locally it is the upstream branch, or for a new branch the point where it left the default branch.
function DebtBase {
    $candidates = @($env:DEBT_BASE, '@{upstream}', 'origin/HEAD')
    foreach ($candidate in $candidates | Where-Object { $_ -and $_ -notmatch '^0+$' }) {
        $resolved = git rev-parse -q --verify "$candidate^{commit}" 2>$null
        if ($resolved) {
            return git merge-base HEAD $resolved
        }
    }

    return $null
}

function CheckDebt {
    $base = DebtBase
    $global:LASTEXITCODE = 0
    if (-not $base) {
        Write-Host 'Nothing to compare with; skipped.'
        return
    }

    $before = DebtCount $base
    $after = DebtCount 'HEAD'
    # git grep exits 1 when nothing matches, so set the result explicitly.
    $global:LASTEXITCODE = 0
    if ($after -gt $before) {
        Write-Host "Known debt grew from $before to $after exemptions since $base." -ForegroundColor Red
        Write-Host 'Fix the code instead of exempting it.'
        $global:LASTEXITCODE = 1
    }
}

Step 'Restore tools' { dotnet tool restore | Out-Null }
Step 'Formatting (fix with: pwsh scripts/format.ps1)' { dotnet csharpier check . }
Step 'Shared core untouched' { & ./scripts/check-core-untouched.ps1 }
Step 'Known debt does not grow' { CheckDebt }
# Capped so a check doesn't saturate a shared desktop, and leaves no idle build nodes behind.
Step 'Build with analyzers' { dotnet build FlaUI.Mcp.slnx -nologo -v q -m:4 -nodeReuse:false }

$unitTests = Get-ChildItem tests -Filter *.csproj -Recurse |
    Where-Object { $_.Name -like '*Tests.csproj' -and $_.Name -notlike '*IntegrationTests*' }
foreach ($project in $unitTests) {
    Step "Test $($project.BaseName)" { dotnet test $project.FullName --no-build -nologo -v q }
}
