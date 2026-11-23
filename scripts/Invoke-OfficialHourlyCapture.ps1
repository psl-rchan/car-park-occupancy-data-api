# Official-hourly capture helper for the Windows Scheduled Task OccupancyOfficialHourlyCapture.
# Host schedule stays at :05; this script does not register or move that task.
# Up to two retries in the same mutex-held run when POST is non-200 or the body still
# reports forecastFailed / skippedMissingOfficial (late :00 snapshots often land shortly after :05).
param(
    [int]$RetryDelaySeconds = 60
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $OutputEncoding

$logDir = 'C:\Users\Administrator\apps\official-hourly-capture-logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Get-PoolEnv([string]$Name) {
    Import-Module WebAdministration
    $filter = "system.applicationHost/applicationPools/add[@name='CarParkOccupancyApi']/environmentVariables/add[@name='$Name']"
    $value = Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter -Name 'value'
    $text = if ($null -ne $value -and $value.PSObject.Properties['Value'] -and $value.Value) { [string]$value.Value } else { [string]$value }
    if ([string]::IsNullOrWhiteSpace($text)) { throw "App pool env $Name not found" }
    return $text
}

function Test-OfficialHourlyComplete([int]$Status, [string]$Body) {
    if ($Status -ne 200) { return $false }
    if ([string]::IsNullOrWhiteSpace($Body)) { return $false }
    try {
        $json = $Body | ConvertFrom-Json
        $forecastFailed = 0
        $skippedMissingOfficial = 0
        if ($null -ne $json.forecastFailed) { $forecastFailed = [int]$json.forecastFailed }
        if ($null -ne $json.skippedMissingOfficial) { $skippedMissingOfficial = [int]$json.skippedMissingOfficial }
        return ($forecastFailed -eq 0 -and $skippedMissingOfficial -eq 0)
    } catch {
        return $false
    }
}

$now = [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTimeOffset]::UtcNow, 'China Standard Time')
$expectedHour = $now.ToString('yyyy-MM-ddTHH:00:00+08:00')
$stamp = $now.ToString('yyyyMMdd-HH')
$logPath = Join-Path $logDir ("official-hourly-" + $stamp + ".log")

function Write-Log([string]$line) {
    $ts = [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTimeOffset]::UtcNow, 'China Standard Time').ToString('yyyy-MM-dd HH:mm:ss zzz')
    Add-Content -LiteralPath $logPath -Value ($ts + ' ' + $line) -Encoding utf8
}

$mutex = New-Object System.Threading.Mutex($false, 'Global\OccupancyOfficialHourlyCapture')
if (-not $mutex.WaitOne(0)) {
    Write-Log 'already_running=1'
    Write-Output 'already_running=1'
    Write-Output ('expectedHour=' + $expectedHour)
    Write-Output ('log=' + $logPath)
    exit 3
}

try {
    $key = Get-PoolEnv 'ApiKey'
    $headers = @{ 'X-Api-Key' = $key }
    # Do not print or log the key or headers.
    $status = 0
    $body = ''
    $err = ''
    $maxAttempts = 3
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $status = 0
        $body = ''
        $err = ''
        try {
            $r = Invoke-WebRequest -Uri 'http://127.0.0.1:8080/api/occupancy/official-hourly' -Method POST -Headers $headers -UseBasicParsing -TimeoutSec 1200
            $status = [int]$r.StatusCode
            $body = $r.Content.Trim()
        } catch {
            $resp = $_.Exception.Response
            $status = if ($resp) { [int]$resp.StatusCode } else { 0 }
            $body = ''
            if ($resp) {
                try {
                    $stream = $resp.GetResponseStream()
                    $reader = New-Object System.IO.StreamReader($stream)
                    $body = $reader.ReadToEnd().Trim()
                    $reader.Close()
                } catch { }
            }
            $err = $_.Exception.Message.Split([char]10)[0]
        }
        $sw.Stop()
        Write-Log ("attempt=" + $attempt + " status=" + $status + " duration_s=" + [int]$sw.Elapsed.TotalSeconds + " expectedHour=" + $expectedHour + " body=" + $body + $(if ($err) { " err=" + $err } else { "" }))
        Write-Output ("attempt=" + $attempt)
        Write-Output ("status=" + $status)
        Write-Output ("duration_s=" + [int]$sw.Elapsed.TotalSeconds)
        Write-Output ("expectedHour=" + $expectedHour)
        Write-Output ("body=" + $body)

        if (Test-OfficialHourlyComplete $status $body) {
            Write-Output ("log=" + $logPath)
            exit 0
        }
        if ($attempt -lt $maxAttempts) {
            Write-Log ("retry_delay_s=" + $RetryDelaySeconds)
            Start-Sleep -Seconds $RetryDelaySeconds
        }
    }
    Write-Output ("log=" + $logPath)
    if ($status -ne 200) { exit 2 }
    exit 0
} catch {
    Write-Log ("FAILED " + $_.Exception.Message.Split([char]10)[0])
    Write-Output ("FAILED")
    exit 1
} finally {
    try { $mutex.ReleaseMutex() | Out-Null } catch { }
    $mutex.Dispose()
}
