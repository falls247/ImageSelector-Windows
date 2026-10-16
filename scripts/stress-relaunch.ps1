# On a real Windows desktop: measure a close/relaunch cycle without leaving runaway processes.
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [ValidateRange(1, 200)][int]$Iterations = 20,
    [ValidateRange(1, 1000)][int]$ImageCount = 120,
    [ValidateRange(128, 8192)][int]$MaxPrivateMB = 1024
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path $ExePath).Path
$dir = Join-Path $env:TEMP ('ImageSelector-relaunch-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir -Force | Out-Null
$results = [System.Collections.Generic.List[object]]::new()
$active = $null
try {
    Add-Type -AssemblyName System.Drawing
    $bitmap = [System.Drawing.Bitmap]::new(800, 600)
    try {
        for ($n = 0; $n -lt $ImageCount; $n++) {
            $bitmap.Save((Join-Path $dir ('image-{0:D4}.jpg' -f $n)),
                [System.Drawing.Imaging.ImageFormat]::Jpeg)
        }
    } finally { $bitmap.Dispose() }
    # A tall image exercises decode-height instead of decode-width.
    $tall = [System.Drawing.Bitmap]::new(10, 12000)
    try {
        $tall.Save((Join-Path $dir 'tall.jpg'), [System.Drawing.Imaging.ImageFormat]::Jpeg)
    } finally { $tall.Dispose() }

    for ($i = 1; $i -le $Iterations; $i++) {
        $active = Start-Process -FilePath $exe -ArgumentList ('"{0}"' -f $dir) -PassThru
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        do {
            Start-Sleep -Milliseconds 200
            $active.Refresh()
            if ($active.HasExited) { throw "Iteration $i app exited before opening window." }
        } while ($active.MainWindowHandle -eq [intptr]::Zero -and $watch.Elapsed.TotalSeconds -lt 10)
        if ($active.MainWindowHandle -eq [intptr]::Zero) { throw "Iteration $i window did not appear." }
        Start-Sleep -Milliseconds 400
        $active.Refresh()
        $private = [math]::Round($active.PrivateMemorySize64 / 1MB, 1)
        $working = [math]::Round($active.WorkingSet64 / 1MB, 1)
        $results.Add([pscustomobject]@{
            Cycle = $i
            StartupMs = $watch.ElapsedMilliseconds
            PrivateMB = $private
            WorkingMB = $working
            Handles = $active.HandleCount
            Threads = $active.Threads.Count
        })
        Write-Host "Cycle $i / $Iterations : private $private MB, working $working MB"
        if ($private -gt $MaxPrivateMB) { throw "Private memory $private MB exceeds limit $MaxPrivateMB MB" }

        # A second EXE must exit without creating another UI/decoder.
        $duplicate = Start-Process -FilePath $exe -ArgumentList ('"{0}"' -f $dir) -PassThru
        if (-not $duplicate.WaitForExit(6000)) {
            $duplicate.Kill()
            throw "Concurrent launch failed to exit in cycle $i."
        }
        $active.Refresh()
        if ($active.HasExited) { throw "The existing instance exited unexpectedly after duplicate launch." }

        if (-not $active.CloseMainWindow()) { throw "CloseMainWindow() refused cycle $i." }
        if (-not $active.WaitForExit(10000)) { throw "App did not exit in cycle $i within 10 seconds." }
        $active = $null
    }
    Write-Host "All $Iterations cycles passed."
}
finally {
    if ($active -and -not $active.HasExited) {
        Write-Warning "Test process remains active; terminating only this test process."
        $active.Kill()
        $active.WaitForExit(5000) | Out-Null
    }
    $csv = Join-Path (Get-Location) 'relaunch-metrics.csv'
    $results | Export-Csv -Path $csv -NoTypeInformation -Encoding utf8
    Write-Host "Metrics: $csv"
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
