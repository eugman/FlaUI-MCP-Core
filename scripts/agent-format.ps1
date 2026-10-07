# Agent post-edit hook: formats the C# file an agent just wrote, so dense output never piles up.
# Reads the hook payload (JSON with tool_input.file_path) from stdin. Always exits 0.
$payload = [Console]::In.ReadToEnd() | ConvertFrom-Json -ErrorAction SilentlyContinue
$path = $payload.tool_input.file_path

if ($path -and $path.EndsWith('.cs') -and (Test-Path $path)) {
    dotnet csharpier format $path | Out-Null
}

exit 0
