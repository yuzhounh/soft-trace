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
$version = '0.2.1'
$runtime = 'win-x64'
$distDirectory = Join-Path $projectRoot 'dist'
$publishDirectory = Join-Path $distDirectory "SoftTrace-v$version-$runtime"
$portableArchive = Join-Path $distDirectory "SoftTrace-v$version-$runtime-Portable.zip"
$installerScript = Join-Path $projectRoot 'installer\SoftTrace.iss'

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

Compress-Archive -LiteralPath (Join-Path $publishDirectory 'SoftTrace.exe') `
    -DestinationPath $portableArchive `
    -Force

$innoCandidates = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Inno Setup 6\ISCC.exe')
    (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique
$innoCompiler = $innoCandidates | Select-Object -First 1
if (-not $innoCompiler) {
    throw 'Inno Setup 6 is required to build the installer. Install JRSoftware.InnoSetup with winget.'
}

& $innoCompiler $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$setupExecutable = Join-Path $distDirectory "SoftTrace-v$version-$runtime-Setup.exe"
Write-Output "Soft Trace v$version portable build: $portableArchive"
Write-Output "Soft Trace v$version installer: $setupExecutable"
