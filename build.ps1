$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) {
    $localDotnet
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}
$solution = Join-Path $projectRoot 'SoftTrace.sln'
$testProject = Join-Path $projectRoot 'tests\SoftTrace.Core.Tests\SoftTrace.Core.Tests.csproj'
$appProject = Join-Path $projectRoot 'src\SoftTrace.App\SoftTrace.App.csproj'
$publishDirectory = Join-Path $projectRoot 'dist\SoftTrace-v0.2.0-win-x64'

& $dotnetCommand restore $solution
& $dotnetCommand build $solution --configuration Release --no-restore
& $dotnetCommand test $testProject --configuration Release --no-build
& $dotnetCommand publish $appProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false

Write-Output "SoftTrace v0.2.0 published to $publishDirectory"
