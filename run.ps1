$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$dotnetCommand = (Get-Command dotnet -ErrorAction Stop).Source

& $dotnetCommand run --project (Join-Path $projectRoot 'src\SoftTrace.App\SoftTrace.App.csproj')
