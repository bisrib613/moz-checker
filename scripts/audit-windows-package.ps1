$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path security-audit -Force | Out-Null
$report = [ordered]@{
    version = $env:AUDIT_VERSION
    scannedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    status = 'not_scanned'
    appExecuted = $false
}
$failed = $false
try {
    if ($env:AUDIT_VERSION -notmatch '^v\d+\.\d+\.\d+$') { throw 'Invalid version' }
    if ($env:EXPECTED_SHA256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Expected checksum required' }
    gh release download $env:AUDIT_VERSION --repo $env:GH_REPO --pattern MozChecker-Windows-x64.zip --pattern SHA256SUMS.txt --dir security-audit/package
    if ($LASTEXITCODE -ne 0) { throw 'Release download failed' }
    $zip = Join-Path $PWD 'security-audit/package/MozChecker-Windows-x64.zip'
    $report.zipSha256 = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    if ($report.zipSha256 -ne $env:EXPECTED_SHA256.ToLower()) { throw 'Published ZIP differs from previously recorded hash' }
    $status = Get-MpComputerStatus
    $report.initialDefender = $status | Select-Object AMServiceEnabled, AntivirusEnabled, RealTimeProtectionEnabled, AMEngineVersion, AntivirusSignatureVersion, AntivirusSignatureLastUpdated
    if (-not $status.AMServiceEnabled) {
        Start-Service WinDefend
        $status = Get-MpComputerStatus
    }
    if (-not $status.AMServiceEnabled -or -not $status.AntivirusEnabled) { throw 'Microsoft Defender scanner is unavailable on this runner' }
    Update-MpSignature
    $status = Get-MpComputerStatus
    $report.defender = $status | Select-Object AMServiceEnabled, AntivirusEnabled, RealTimeProtectionEnabled, AMEngineVersion, AntivirusSignatureVersion, AntivirusSignatureLastUpdated
    Expand-Archive -Path $zip -DestinationPath security-audit/unpacked
    $exe = Join-Path $PWD 'security-audit/unpacked/MozChecker/MozChecker.exe'
    $report.exeSha256 = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
    $signature = Get-AuthenticodeSignature $exe
    $report.authenticode = [string]$signature.Status
    $scanner = Join-Path $env:ProgramFiles 'Windows Defender/MpCmdRun.exe'
    $report.status = 'scanning'
    & $scanner -Scan -ScanType 3 -File (Join-Path $PWD 'security-audit/unpacked') 2>&1 | Tee-Object security-audit/defender-scan.txt
    $report.scanExitCode = $LASTEXITCODE
    $report.detections = @(Get-MpThreatDetection | Where-Object { ($_.Resources -join ' ') -match 'security-audit' } | Select-Object ThreatID, InitialDetectionTime, LastThreatStatusChangeTime, ActionSuccess, Resources)
    if ($report.scanExitCode -ne 0 -or $report.detections.Count -gt 0) {
        $report.status = 'detected_or_scan_failed'
        throw 'Defender reported a detection or did not complete successfully; inspect the scan log'
    }
    $report.status = 'scan_completed_no_detections'
    $report.limitation = 'One engine on this runner and signature version only; does not prove safety or resolve a different endpoint detection.'
} catch {
    $failed = $true
    $report.error = $_.Exception.Message
    try {
        $report.detections = @(Get-MpThreatDetection | Where-Object { ($_.Resources -join ' ') -match 'security-audit' } | Select-Object ThreatID, InitialDetectionTime, ActionSuccess, Resources)
    } catch { $report.detectionQueryError = $_.Exception.Message }
} finally {
    $report | ConvertTo-Json -Depth 8 | Set-Content security-audit/report.json -Encoding utf8
    Get-Content security-audit/report.json
}
if ($failed) { exit 1 }
