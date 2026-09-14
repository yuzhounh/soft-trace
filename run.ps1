$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) {
    $localDotnet
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

& $dotnetCommand run --project (Join-Path $projectRoot 'src\SoftTrace.App\SoftTrace.App.csproj')
