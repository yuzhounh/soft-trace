$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$dotnetCommand = (Get-Command dotnet -ErrorAction Stop).Source
$solution = Join-Path $projectRoot 'SoftTrace.sln'
$appProject = Join-Path $projectRoot 'src\SoftTrace.App\SoftTrace.App.csproj'
$version = '0.3.3'
$runtime = 'win-x64'
$distDirectory = Join-Path $projectRoot 'dist'
$publishDirectory = Join-Path $distDirectory "SoftTrace-v$version-$runtime"
$portableArchive = Join-Path $distDirectory "SoftTrace-v$version-$runtime-Portable.zip"
$installerScript = Join-Path $projectRoot 'installer\SoftTrace.iss'

# Embed the public desktop client configuration, never a user's login tokens.
$oauthBuildFile = Join-Path $projectRoot '.tools\firebase-oauth-secret.txt'
if (-not (Test-Path -LiteralPath $oauthBuildFile)) {
    $legacyOAuthFile = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SoftTrace\firebase-oauth-secret.txt'
    if (-not (Test-Path -LiteralPath $legacyOAuthFile)) {
        throw 'Desktop OAuth configuration is required. Supply .tools\firebase-oauth-secret.txt before building.'
    }
    $desktopClientSecret = [System.IO.File]::ReadAllText($legacyOAuthFile).Trim()
    if ($desktopClientSecret.StartsWith('dpapi:', [System.StringComparison]::Ordinal)) {
        Add-Type -AssemblyName System.Security
        $protectedClientBytes = [Convert]::FromBase64String($desktopClientSecret.Substring(6))
        $desktopClientSecret = [System.Text.Encoding]::UTF8.GetString(
            [System.Security.Cryptography.ProtectedData]::Unprotect(
                $protectedClientBytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser))
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $oauthBuildFile) -Force | Out-Null
    [System.IO.File]::WriteAllText($oauthBuildFile, $desktopClientSecret, [System.Text.UTF8Encoding]::new($false))
}
$desktopClientSecret = [System.IO.File]::ReadAllText($oauthBuildFile).Trim()
if ([string]::IsNullOrWhiteSpace($desktopClientSecret) -or $desktopClientSecret.StartsWith('dpapi:')) {
    throw 'Desktop OAuth build configuration must contain the unencrypted desktop client value.'
}
$desktopClientSecret = $null

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
Write-Output "Soft Trace v$version portable archive: $portableArchive"
Write-Output "Soft Trace v$version installer: $setupExecutable"
