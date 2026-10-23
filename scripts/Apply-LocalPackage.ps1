param([switch]$Launch, [switch]$Background, [int]$WaitSeconds = 0)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$pendingPath = Join-Path $projectRoot '.tools\local-upgrade\pending.json'
$receiptPath = Join-Path $projectRoot '.tools\local-upgrade\receipt.json'
function Write-Receipt($status, $details) {
    [pscustomobject]@{ Status = $status; At = (Get-Date).ToString('o'); Details = $details } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $receiptPath -Encoding UTF8
}
try {
    $pending = Get-Content -LiteralPath $pendingPath -Raw | ConvertFrom-Json
    $target = [IO.Path]::GetFullPath($pending.Executable)
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist')) + '\'
    if (-not $target.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package is outside the project dist directory.' }
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw 'Prepared program is missing.' }
    if ((Get-Item -LiteralPath $target).VersionInfo.FileVersion -ne $pending.FileVersion) { throw 'Package version does not match the prepared update.' }
    $packageRoot = [IO.Path]::GetFullPath((Split-Path -Parent $target)) + '\'
    foreach ($file in $pending.Files) {
        $packageFile = [IO.Path]::GetFullPath((Join-Path $packageRoot $file.Path))
        if (-not $packageFile.StartsWith($packageRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid package file path.' }
        if (-not (Test-Path -LiteralPath $packageFile -PathType Leaf)) { throw 'A required package file is missing.' }
    }

    $deadline = (Get-Date).AddSeconds([Math]::Min([Math]::Max($WaitSeconds, 0), 300))
    do {
        $running = @(Get-Process -Name SoftTrace -ErrorAction SilentlyContinue)
        if ($running.Count -eq 0) { break }
        if ((Get-Date) -ge $deadline) {
            Write-Receipt 'waiting_for_exit' @{ Version = $pending.Version; Running = @($running.Id); PackagePrepared = $true }
            if ($Launch) {
                # Match single-instance launch behavior without starting a second recorder.
                $event = [Threading.EventWaitHandle]::OpenExisting('SoftTrace.ShowWindowEvent')
                try { [void]$event.Set() } finally { $event.Dispose() }
            }
            exit 0
        }
        [void]$running[0].WaitForExit(1000)
    } while ($true)

    # Exclude a concurrent normal launch while activating metadata. No used binaries are replaced.
    $mutex = New-Object Threading.Mutex($false, 'SoftTrace.SingleInstance')
    $ownsMutex = $false
    try {
        try { $ownsMutex = $mutex.WaitOne(10000) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
        if (-not $ownsMutex) { Write-Receipt 'waiting_for_exit' @{ Version = $pending.Version }; exit 0 }
        if (@(Get-Process -Name SoftTrace -ErrorAction SilentlyContinue).Count -gt 0) { throw 'A recorder started during activation; retry on next launch.' }
        $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        $approvedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
        $approvedBefore = (Get-ItemProperty $approvedKey -Name SoftTrace -ErrorAction SilentlyContinue).SoftTrace
        $shellLink = New-Object -ComObject WScript.Shell
        foreach ($entry in $pending.Links) {
            if (-not (Test-Path -LiteralPath $entry.Link)) { continue }
            $link = $shellLink.CreateShortcut($entry.Link)
            if ($link.TargetPath -ne $target -and $link.TargetPath -ne $entry.OriginalTarget -and
                -not ($link.Arguments -like '*SoftTrace-NextLaunch.vbs*')) { throw 'A shortcut was changed by another task.' }
            $link.TargetPath = $target
            $link.Arguments = $entry.Arguments
            $link.WorkingDirectory = Split-Path -Parent $target
            $link.IconLocation = "$target,0"
            $link.Description = 'SoftTrace'
            $link.Save()
        }
        if ($pending.HadStartupEntry) {
            $currentStartup = (Get-ItemProperty $runKey -Name SoftTrace).SoftTrace
            $desiredStartup = '"' + $target + '"' + $pending.StartupArguments
            if ($currentStartup -ne $pending.OriginalStartup -and $currentStartup -ne $desiredStartup -and
                -not ($currentStartup -like '*SoftTrace-NextLaunch.vbs*')) { throw 'Startup was changed by another task.' }
            Set-ItemProperty $runKey -Name SoftTrace -Value $desiredStartup
        }
        $approvedAfter = (Get-ItemProperty $approvedKey -Name SoftTrace -ErrorAction SilentlyContinue).SoftTrace
        if ([Convert]::ToBase64String([byte[]]$approvedBefore) -ne [Convert]::ToBase64String([byte[]]$approvedAfter)) {
            throw 'Startup enable/disable state changed during activation.'
        }
        Write-Receipt 'activated_for_next_launch' @{ Version = $pending.Version; Executable = $target;
            SyncSettingsUntouched = $true; StartupStatePreserved = $true; RecorderStarted = $false }
    } finally { if ($ownsMutex) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
    if ($Launch) {
        if ($Background) { Start-Process -FilePath $target -ArgumentList '--background' -WorkingDirectory (Split-Path -Parent $target) -WindowStyle Hidden }
        else { Start-Process -FilePath $target -WorkingDirectory (Split-Path -Parent $target) -WindowStyle Hidden }
    }
} catch {
    Write-Receipt 'failed' @{ Message = $_.Exception.Message; PendingPackage = $true }
    exit 1
}
