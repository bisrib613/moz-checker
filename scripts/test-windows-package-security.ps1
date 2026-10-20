param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$ReportDirectory,
    [switch]$RemoveRunnerExclusions
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$ReportDirectory = (Resolve-Path $ReportDirectory).Path
$PackageDirectory = (Resolve-Path $PackageDirectory).Path
$report = [ordered]@{ status = 'not_scanned'; appExecuted = $false; startedAtUtc = (Get-Date).ToUniversalTime().ToString('o') }
$auditRoot = Join-Path $env:SystemDrive ('MozChecker-SecurityAudit-' + [guid]::NewGuid().ToString('N'))
$ruleName = 'MozChecker-SecurityAudit-' + [guid]::NewGuid().ToString('N')
$process = $null
$failed = $false
try {
    # Keep the test outside runner build/cache exclusions; do not rename, patch, or repack the EXE.
    New-Item -ItemType Directory -Path $auditRoot | Out-Null
    Copy-Item -Path $PackageDirectory -Destination (Join-Path $auditRoot 'MozChecker') -Recurse
    $exe = Join-Path $auditRoot 'MozChecker/MozChecker.exe'
    $report.exeSha256 = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
    if ($report.exeSha256 -ne (Get-FileHash (Join-Path $PackageDirectory 'MozChecker.exe') -Algorithm SHA256).Hash.ToLower()) { throw 'Copied executable hash mismatch' }
    $report.authenticode = [string](Get-AuthenticodeSignature $exe).Status
    $status = Get-MpComputerStatus
    if (-not $status.AMServiceEnabled) { Start-Service WinDefend }
    # Enable protection, never add exclusions or disable antivirus to make the app launch.
    Set-MpPreference -DisableRealtimeMonitoring $false
    Set-MpPreference -DisableBehaviorMonitoring $false
    Update-MpSignature
    $status = Get-MpComputerStatus
    $report.defender = $status | Select-Object AMServiceEnabled, AntivirusEnabled, RealTimeProtectionEnabled, BehaviorMonitorEnabled, AMEngineVersion, AntivirusSignatureVersion, AntivirusSignatureLastUpdated
    $preferences = Get-MpPreference
    $report.preferences = $preferences | Select-Object DisableRealtimeMonitoring, DisableBehaviorMonitoring, MAPSReporting, SubmitSamplesConsent
    if (-not $status.AMServiceEnabled -or -not $status.AntivirusEnabled -or -not $status.RealTimeProtectionEnabled -or -not $status.BehaviorMonitorEnabled) { throw 'Real-time and behavior protection could not be enabled; no startup test performed' }
    $applicablePaths = @($preferences.ExclusionPath | Where-Object { $exe -like $_ -or $exe -like ($_.TrimEnd('\') + '\*') })
    $applicableExtensions = @($preferences.ExclusionExtension | Where-Object { $_ -in @('*', 'exe', '.exe') })
    $applicableProcesses = @($preferences.ExclusionProcess | Where-Object { 'MozChecker.exe' -like $_ -or $exe -like $_ })
    $report.applicableExclusions = @{ paths = $applicablePaths; extensions = $applicableExtensions; processes = $applicableProcesses }
    if ($applicablePaths.Count -or $applicableExtensions.Count -or $applicableProcesses.Count) {
        if (-not $RemoveRunnerExclusions -or $env:GITHUB_ACTIONS -ne 'true') { throw 'The test target is excluded by policy; no startup test performed' }
        # GitHub's disposable VM excludes whole drives. Removing exclusions INCREASES coverage.
        # Never run this policy change on a user device or add an app exception.
        if ($applicablePaths.Count) { Remove-MpPreference -ExclusionPath $applicablePaths }
        if ($applicableExtensions.Count) { Remove-MpPreference -ExclusionExtension $applicableExtensions }
        if ($applicableProcesses.Count) { Remove-MpPreference -ExclusionProcess $applicableProcesses }
        $preferences = Get-MpPreference
        $remainingPaths = @($preferences.ExclusionPath | Where-Object { $exe -like $_ -or $exe -like ($_.TrimEnd('\') + '\*') })
        $remainingExtensions = @($preferences.ExclusionExtension | Where-Object { $_ -in @('*', 'exe', '.exe') })
        $remainingProcesses = @($preferences.ExclusionProcess | Where-Object { 'MozChecker.exe' -like $_ -or $exe -like $_ })
        $report.exclusionsRemovedOnDisposableRunner = $true
        if ($remainingPaths.Count -or $remainingExtensions.Count -or $remainingProcesses.Count) { throw 'Runner exclusion policy still applies; cannot validate startup' }
    }
    $scanner = Join-Path $env:ProgramFiles 'Windows Defender/MpCmdRun.exe'
    & $scanner -Scan -ScanType 3 -File (Join-Path $auditRoot 'MozChecker') 2>&1 | Tee-Object (Join-Path $ReportDirectory 'defender-scan.txt')
    $report.scanExitCode = $LASTEXITCODE
    if ($report.scanExitCode -ne 0) { throw 'Static scan failed or detected a threat; application was not started' }
    $data = Join-Path $auditRoot 'empty-profile'
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    $profiles = Get-NetFirewallProfile
    if (@($profiles | Where-Object { -not $_.Enabled }).Count) {
        if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Firewall coverage unavailable; refusing to launch test sample' }
        Set-NetFirewallProfile -All -Enabled True
    }
    $report.firewallProfiles = @(Get-NetFirewallProfile | Select-Object Name, Enabled)
    if (@($report.firewallProfiles | Where-Object { -not $_.Enabled }).Count) { throw 'Firewall containment could not be enabled' }
    New-NetFirewallRule -DisplayName $ruleName -Direction Outbound -Program $exe -Action Block | Out-Null
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $exe
    $info.Arguments = '--smoke --screenshot "' + (Join-Path $ReportDirectory 'desktop.png') + '"'
    $info.WorkingDirectory = $data
    $info.UseShellExecute = $false
    $info.Environment.Clear()
    foreach ($name in @('SystemRoot', 'WINDIR', 'COMSPEC')) { $info.Environment[$name] = [Environment]::GetEnvironmentVariable($name) }
    $info.Environment['PATH'] = (Join-Path $env:SystemRoot 'System32') + ';' + $env:SystemRoot
    foreach ($name in @('TEMP', 'TMP', 'USERPROFILE', 'LOCALAPPDATA', 'APPDATA')) { $info.Environment[$name] = $data }
    $info.Environment['QT_QPA_PLATFORM'] = 'windows'
    $info.Environment['MOZ_CHECKER_DATA_DIR'] = $data
    $report.testScope = 'Native Windows desktop --smoke startup; empty profile, no API keys, clean child environment, outbound network blocked. This is not an audit of every feature.'
    $process = [System.Diagnostics.Process]::Start($info)
    $report.appExecuted = $true
    if (-not $process.WaitForExit(60000)) { throw 'Startup smoke test timed out' }
    $report.appExitCode = $process.ExitCode
    Start-Sleep -Seconds 15
    $report.detections = @(Get-MpThreatDetection | Where-Object { ($_.Resources -join ' ') -match [regex]::Escape($auditRoot) } | Select-Object ThreatID, InitialDetectionTime, ActionSuccess, Resources)
    if ($report.appExitCode -ne 0 -or $report.detections.Count) { throw 'Startup failed or Defender detected a threat; inspect report' }
    $report.finalDefender = Get-MpComputerStatus | Select-Object RealTimeProtectionEnabled, BehaviorMonitorEnabled
    if (-not $report.finalDefender.RealTimeProtectionEnabled -or -not $report.finalDefender.BehaviorMonitorEnabled) { throw 'Protection was not active at the end of the test' }
    $report.status = 'protected_startup_completed_no_detections'
    $report.limitation = 'One runner, engine/signature and startup path. Does not invalidate a different endpoint detection or establish a false positive.'
} catch {
    $failed = $true
    $report.status = 'failed_or_not_validated'
    $report.error = $_.Exception.Message
    try {
        $report.detections = @(Get-MpThreatDetection | Where-Object { ($_.Resources -join ' ') -match [regex]::Escape($auditRoot) } | Select-Object ThreatID, InitialDetectionTime, ActionSuccess, Resources)
    } catch { $report.detectionQueryError = $_.Exception.Message }
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill() }
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    $report | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $ReportDirectory 'report.json') -Encoding utf8
    Get-Content (Join-Path $ReportDirectory 'report.json')
    # Do not restore a quarantined file or use this test copy for distribution.
    Remove-Item -LiteralPath $auditRoot -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failed) { exit 1 }
