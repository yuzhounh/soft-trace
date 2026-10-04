$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$dotnetCommand = (Get-Command dotnet -ErrorAction Stop).Source
$solution = Join-Path $projectRoot 'SoftTrace.sln'
$appProject = Join-Path $projectRoot 'src\SoftTrace.App\SoftTrace.App.csproj'
$version = '0.3.2'
$runtime = 'win-x64'
$distDirectory = Join-Path $projectRoot 'dist'
$publishDirectory = Join-Path $distDirectory "SoftTrace-v$version-$runtime"
$portableExecutable = Join-Path $distDirectory "SoftTrace-v$version-$runtime-Portable.exe"
$installerScript = Join-Path $projectRoot 'installer\SoftTrace.iss'

& $dotnetCommand restore $solution
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }
& $dotnetCommand build $solution --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }
& $dotnetCommand test $solution --configuration Release --no-build
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }
& $dotnetCommand publish $appProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

Copy-Item -LiteralPath (Join-Path $publishDirectory 'SoftTrace.exe') `
    -Destination $portableExecutable `
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
Write-Output "Soft Trace v$version portable build: $portableExecutable"
Write-Output "Soft Trace v$version installer: $setupExecutable"
