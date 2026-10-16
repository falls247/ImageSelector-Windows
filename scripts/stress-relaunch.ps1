# Desktop-only stress harness for close -> immediate relaunch process overlap.
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [ValidateSet('Overlap', 'Sequential')][string]$Mode = 'Overlap',
    [ValidateRange(1, 200)][int]$Iterations = 20,
    [ValidateRange(1, 1000)][int]$ImageCount = 120,
    [ValidateRange(128, 8192)][int]$MaxPrivateMB = 1536,
    [switch]$HeavyFixtures
)

$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $ExePath).Path
$folder = Join-Path $env:TEMP ('ImageSelector-relaunch-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$samples = [System.Collections.Generic.List[object]]::new()
$owned = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

function Start-App {
    $p = Start-Process -FilePath $exe -ArgumentList ('"{0}"' -f $folder) -PassThru
    $script:owned.Add($p)
    return $p
}

function Observe-Window([System.Diagnostics.Process]$p) {
    $start = [datetime]::UtcNow
    $noResponseSince = $null
    $shownAt = $null
    while (([datetime]::UtcNow - $start).TotalSeconds -lt 10) {
        Start-Sleep -Milliseconds 200
        $p.Refresh()
        if ($p.HasExited) { throw "Process $($p.Id) exited before test completed." }
        if ($p.MainWindowHandle -eq [intptr]::Zero) { continue }
        if (-not $shownAt) { $shownAt = [datetime]::UtcNow }
        if (-not $p.Responding) {
            if (-not $noResponseSince) { $noResponseSince = [datetime]::UtcNow }
            if (([datetime]::UtcNow - $noResponseSince).TotalSeconds -ge 3) {
                throw "Unresponsive for >=3 seconds: PID $($p.Id)"
            }
        }
        else { $noResponseSince = $null }
    }
    if (-not $shownAt) { throw "Window was not visible within 10 seconds: PID $($p.Id)" }
    return $shownAt
}

function Read-MemoryCounters {
    try {
        $data = Get-Counter @('\Memory\Committed Bytes', '\Memory\Available MBytes') -ErrorAction Stop
        $values = @{}
        foreach ($sample in $data.CounterSamples) {
            if ($sample.Path -match 'committed bytes') { $values.CommittedMB = [math]::Round($sample.CookedValue / 1MB, 2) }
            if ($sample.Path -match 'available mbytes') { $values.AvailableMB = [math]::Round($sample.CookedValue, 2) }
        }
        return $values
    }
    catch { return @{ CommittedMB = -1; AvailableMB = -1 } }
}

function Make-Fixtures {
    Add-Type -AssemblyName System.Drawing
    $bitmap = [System.Drawing.Bitmap]::new(800, 600)
    try {
        for ($i = 0; $i -lt $ImageCount; $i++) {
            $bitmap.Save((Join-Path $folder ('image-{0:D4}.jpg' -f $i)),
                [System.Drawing.Imaging.ImageFormat]::Jpeg)
        }
    }
    finally { $bitmap.Dispose() }
    $vertical = [System.Drawing.Bitmap]::new(10, 12000)
    try { $vertical.Save((Join-Path $folder 'vertical.jpg'), [System.Drawing.Imaging.ImageFormat]::Jpeg) }
    finally { $vertical.Dispose() }
    if (-not $HeavyFixtures) { return }

    # Draw non-uniform bytes through LockBits so JPEG files cannot trivially compress.
    for ($n = 0; $n -lt 20; $n++) {
        $large = [System.Drawing.Bitmap]::new(6000, 4000,
            [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        try {
            $rect = [System.Drawing.Rectangle]::new(0, 0, 6000, 4000)
            $locked = $large.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly,
                [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
            try {
                $rng = [System.Random]::new($n + 1234)
                $row = [byte[]]::new([math]::Abs($locked.Stride))
                for ($y = 0; $y -lt 4000; $y++) {
                    $rng.NextBytes($row)
                    [System.Runtime.InteropServices.Marshal]::Copy(
                        $row, 0, [intptr]::Add($locked.Scan0, $y * $locked.Stride), $row.Length)
                }
            }
            finally { $large.UnlockBits($locked) }
            $large.Save((Join-Path $folder ('heavy-{0:D2}.jpg' -f $n)),
                [System.Drawing.Imaging.ImageFormat]::Jpeg)
        }
        finally { $large.Dispose() }
    }
}

try {
    Make-Fixtures
    $active = Start-App
    for ($cycle = 1; $cycle -le $Iterations; $cycle++) {
        $shown = Observe-Window $active
        $active.Refresh()
        $oldPid = $active.Id
        $previousPeak = [math]::Round($active.PeakWorkingSet64 / 1MB, 2)
        $pagedPeak = [math]::Round($active.PeakPagedMemorySize64 / 1MB, 2)
        $private = [math]::Round($active.PrivateMemorySize64 / 1MB, 2)
        $handles = $active.HandleCount
        $threadCount = $active.Threads.Count
        $memory = Read-MemoryCounters
        if ($private -gt $MaxPrivateMB) { throw "Private memory limit exceeded: $private MB" }

        if (-not $active.CloseMainWindow()) { throw "CloseMainWindow failed: PID $oldPid" }
        $closeUtc = [datetime]::UtcNow

        # Critical regression scenario: launch immediately without waiting for old PID.
        $next = $null
        $newStartUtc = $null
        if ($cycle -lt $Iterations -and $Mode -eq 'Overlap') {
            $next = Start-App
            $newStartUtc = [datetime]::UtcNow
        }

        if (-not $active.WaitForExit(10000)) {
            throw "Previous process failed to exit within 10 seconds: PID $oldPid"
        }
        $oldExitUtc = $active.ExitTime.ToUniversalTime()
        if ($cycle -lt $Iterations -and $Mode -eq 'Sequential') {
            $next = Start-App
            $newStartUtc = [datetime]::UtcNow
        }
        $samples.Add([pscustomobject]@{
            Cycle = $cycle
            Mode = $Mode
            PreviousPid = $oldPid
            NextPid = if ($next) { $next.Id } else { 0 }
            WindowShownUtc = $shown.ToString('O')
            CloseRequestUtc = $closeUtc.ToString('O')
            PreviousExitUtc = $oldExitUtc.ToString('O')
            NextLaunchRecordedUtc = if ($newStartUtc) { $newStartUtc.ToString('O') } else { '' }
            PrivateMB = $private
            PeakWorkingMB = $previousPeak
            PeakPagedMB = $pagedPeak
            HandleCount = $handles
            ThreadCount = $threadCount
            CommittedMB = $memory.CommittedMB
            AvailableMB = $memory.AvailableMB
        })
        Write-Host ("Cycle {0}/{1}: PID {2} exited, private {3} MB, peak {4} MB" -f
            $cycle, $Iterations, $oldPid, $private, $previousPeak)
        $active = $next
    }
    # Check actual process exit time versus the next instance's first LoadFolder log.
    $logPath = Join-Path $env:LOCALAPPDATA 'ImageSelector\logs\error.log'
    if (Test-Path -LiteralPath $logPath) {
        $logLines = Get-Content -LiteralPath $logPath
        foreach ($entry in $samples) {
            if (-not $entry.NextPid) { continue }
            $pattern = 'PID=' + $entry.NextPid + ' TID=.+ LoadFolder started:'
            $first = $logLines | Where-Object { $_ -match $pattern } | Select-Object -Last 1
            if (-not $first) { throw "Missing next PID LoadFolder log: $($entry.NextPid)" }
            if ($first -notmatch '^\[([^\]]+)\]') { throw "Invalid log timestamp: $first" }
            $loadedUtc = [datetimeoffset]::Parse($Matches[1]).UtcDateTime
            if ($loadedUtc -le [datetime]::Parse($entry.PreviousExitUtc)) {
                throw "New PID $($entry.NextPid) started loading before old PID $($entry.PreviousPid) exited"
            }
        }
    }
    else { throw "Missing ImageSelector PID log: $logPath" }

    Start-Sleep -Milliseconds 300
    $remaining = @(Get-Process ImageSelector -ErrorAction SilentlyContinue)
    if ($remaining.Count -gt 0) {
        throw ("Residual ImageSelector PIDs: " + (($remaining | Select-Object -ExpandProperty Id) -join ','))
    }
    Write-Host "PASS: all $Iterations relaunch cycles completed; no residual processes."
}
finally {
    $csv = Join-Path (Get-Location) 'relaunch-metrics.csv'
    $samples | Export-Csv -Path $csv -NoTypeInformation -Encoding utf8
    Write-Host "Metrics: $csv"
    foreach ($p in $owned) {
        try {
            if (-not $p.HasExited) {
                Write-Warning "Cleaning up stress-test-owned PID $($p.Id)"
                $p.Kill()
                $p.WaitForExit(5000) | Out-Null
            }
        }
        catch { Write-Warning $_ }
        finally { $p.Dispose() }
    }
    Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
}
