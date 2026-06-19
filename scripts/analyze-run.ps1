param(
    [string]$Path,
    [string]$OutPath,
    [switch]$Json
)

$ErrorActionPreference = "Stop"

function Get-Number {
    param([object]$Value)

    if ($null -eq $Value) {
        return $null
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    $number = 0.0
    if ([double]::TryParse($text, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$number)) {
        return $number
    }

    return $null
}

function Format-Number {
    param(
        [object]$Value,
        [string]$Format = "0.0"
    )

    if ($null -eq $Value) {
        return "n/a"
    }

    return ([double]$Value).ToString($Format, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-LatestRunPath {
    $runDir = Join-Path $env:APPDATA "TrainDeck\runs"
    if (!(Test-Path -LiteralPath $runDir)) {
        throw "No TrainDeck run folder found at $runDir."
    }

    $latest = Get-ChildItem -LiteralPath $runDir -Filter "*.csv" -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $latest) {
        throw "No TrainDeck run CSV files found in $runDir."
    }

    return $latest.FullName
}

function Get-ColumnStats {
    param(
        [array]$Rows,
        [string]$Name
    )

    $values = @()
    foreach ($row in $Rows) {
        $value = Get-Number $row.$Name
        if ($null -ne $value) {
            $values += [double]$value
        }
    }

    if ($values.Count -eq 0) {
        return [pscustomobject]@{
            Name = $Name
            Count = 0
            Min = $null
            Max = $null
            Average = $null
            Changes = 0
        }
    }

    $changes = 0
    $previous = $null
    foreach ($value in $values) {
        if ($null -ne $previous -and [Math]::Abs($value - $previous) -gt 0.001) {
            $changes++
        }

        $previous = $value
    }

    return [pscustomobject]@{
        Name = $Name
        Count = $values.Count
        Min = ($values | Measure-Object -Minimum).Minimum
        Max = ($values | Measure-Object -Maximum).Maximum
        Average = ($values | Measure-Object -Average).Average
        Changes = $changes
    }
}

if ([string]::IsNullOrWhiteSpace($Path)) {
    $Path = Get-LatestRunPath
}

$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
$rows = @(Import-Csv -LiteralPath $resolvedPath)
if ($rows.Count -eq 0) {
    throw "Run file has no samples: $resolvedPath"
}

$orderedRows = @($rows | Sort-Object { Get-Number $_.elapsedSeconds })
$durationSeconds = 0.0
$distanceKm = 0.0
$previousElapsed = $null
$previousSpeed = $null
$overspeedSamples = 0
$overspeedPeakKmh = 0.0
$limitSamples = 0
$hardBrakeSamples = 0
$movingSamples = 0
$stopCount = 0
$wasMoving = $false

foreach ($row in $orderedRows) {
    $elapsed = Get-Number $row.elapsedSeconds
    $speed = Get-Number $row.speedKmh
    $limit = Get-Number $row.speedLimitKmh
    $accel = Get-Number $row.accelerationMps2

    if ($null -ne $elapsed -and $elapsed -gt $durationSeconds) {
        $durationSeconds = $elapsed
    }

    if ($null -ne $speed -and $speed -gt 2) {
        $movingSamples++
    }

    if ($null -ne $speed -and $null -ne $previousElapsed -and $null -ne $previousSpeed -and $null -ne $elapsed -and $elapsed -gt $previousElapsed) {
        $deltaHours = ($elapsed - $previousElapsed) / 3600.0
        $distanceKm += (($speed + $previousSpeed) / 2.0) * $deltaHours
    }

    if ($null -ne $limit -and $limit -gt 0 -and $null -ne $speed) {
        $limitSamples++
        $overBy = $speed - $limit
        if ($overBy -gt 1.5) {
            $overspeedSamples++
            if ($overBy -gt $overspeedPeakKmh) {
                $overspeedPeakKmh = $overBy
            }
        }
    }

    if ($null -ne $accel -and $accel -lt -0.8) {
        $hardBrakeSamples++
    }

    if ($null -ne $speed) {
        if ($wasMoving -and $speed -lt 1.0) {
            $stopCount++
            $wasMoving = $false
        }
        elseif ($speed -gt 5.0) {
            $wasMoving = $true
        }
    }

    if ($null -ne $elapsed) {
        $previousElapsed = $elapsed
    }

    if ($null -ne $speed) {
        $previousSpeed = $speed
    }
}

$speedStats = Get-ColumnStats $orderedRows "speedKmh"
$accelStats = Get-ColumnStats $orderedRows "accelerationMps2"
$cabColumns = @(
    "powerHandleInput",
    "powerHandleNormalized",
    "reverser",
    "emergencyBrake",
    "dra",
    "brakeHold",
    "doorReleaseLeft",
    "doorReleaseRight",
    "tpwsBrakeDemand",
    "tdTargetKmh",
    "tdOutput"
)
$cabStats = @($cabColumns | ForEach-Object { Get-ColumnStats $orderedRows $_ })
$usefulCabStats = @($cabStats | Where-Object { $_.Count -gt 0 })

$overspeedPercent = if ($limitSamples -gt 0) { 100.0 * $overspeedSamples / $limitSamples } else { 0.0 }
$movingPercent = 100.0 * $movingSamples / $orderedRows.Count
$hardBrakePercent = 100.0 * $hardBrakeSamples / $orderedRows.Count

$summary = [pscustomobject]@{
    source = $resolvedPath
    samples = $orderedRows.Count
    durationSeconds = [Math]::Round($durationSeconds, 1)
    distanceKm = [Math]::Round($distanceKm, 2)
    maxSpeedKmh = if ($null -ne $speedStats.Max) { [Math]::Round($speedStats.Max, 1) } else { $null }
    averageSpeedKmh = if ($null -ne $speedStats.Average) { [Math]::Round($speedStats.Average, 1) } else { $null }
    movingPercent = [Math]::Round($movingPercent, 1)
    overspeedSamplePercent = [Math]::Round($overspeedPercent, 1)
    peakOverspeedKmh = [Math]::Round($overspeedPeakKmh, 1)
    hardBrakeSamplePercent = [Math]::Round($hardBrakePercent, 1)
    minAccelerationMps2 = if ($null -ne $accelStats.Min) { [Math]::Round($accelStats.Min, 2) } else { $null }
    maxAccelerationMps2 = if ($null -ne $accelStats.Max) { [Math]::Round($accelStats.Max, 2) } else { $null }
    stopCount = $stopCount
    cabSignals = $usefulCabStats
}

if ($Json) {
    $body = $summary | ConvertTo-Json -Depth 6
}
else {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# TrainDeck Run Analysis")
    $lines.Add("")
    $lines.Add("- Source: ``$resolvedPath``")
    $lines.Add("- Samples: $($summary.samples)")
    $lines.Add("- Duration: $(Format-Number $summary.durationSeconds) seconds")
    $lines.Add("- Estimated distance: $(Format-Number $summary.distanceKm "0.00") km")
    $lines.Add("- Speed: avg $(Format-Number $summary.averageSpeedKmh) km/h, max $(Format-Number $summary.maxSpeedKmh) km/h")
    $lines.Add("- Moving samples: $(Format-Number $summary.movingPercent)%")
    $lines.Add("- Overspeed samples: $(Format-Number $summary.overspeedSamplePercent)% above limit + 1.5 km/h")
    $lines.Add("- Peak overspeed: $(Format-Number $summary.peakOverspeedKmh) km/h")
    $lines.Add("- Hard brake samples: $(Format-Number $summary.hardBrakeSamplePercent)% below -0.8 m/s2")
    $lines.Add("- Stops after motion: $($summary.stopCount)")
    $lines.Add("")
    $lines.Add("## Mapping Signals")
    $lines.Add("")
    if ($usefulCabStats.Count -eq 0) {
        $lines.Add("No cab trace columns were populated. Confirm the TSW HTTP API cab actor is available before recording.")
    }
    else {
        $lines.Add("| Signal | Coverage | Min | Max | Avg | Changes |")
        $lines.Add("| --- | ---: | ---: | ---: | ---: | ---: |")
        foreach ($stat in $usefulCabStats) {
            $coverage = 100.0 * $stat.Count / $orderedRows.Count
            $lines.Add("| $($stat.Name) | $(Format-Number $coverage)% | $(Format-Number $stat.Min "0.###") | $(Format-Number $stat.Max "0.###") | $(Format-Number $stat.Average "0.###") | $($stat.Changes) |")
        }
    }

    $lines.Add("")
    $lines.Add("## Training Notes")
    $lines.Add("")
    if ($summary.overspeedSamplePercent -gt 2) {
        $lines.Add("- Review braking and throttle timing around speed limit changes; overspeed was present in $(Format-Number $summary.overspeedSamplePercent)% of limit-aware samples.")
    }
    else {
        $lines.Add("- Speed-limit discipline looks usable for a baseline trace.")
    }

    if ($summary.hardBrakeSamplePercent -gt 8) {
        $lines.Add("- Braking is abrupt in this trace. For assist training, treat this run as assertive rather than comfort-biased.")
    }
    elseif ($summary.hardBrakeSamplePercent -gt 0) {
        $lines.Add("- Some hard braking exists; inspect these segments before using the trace as a smooth-driving baseline.")
    }
    else {
        $lines.Add("- No hard-brake samples were detected by the default threshold.")
    }

    $activeSignals = @($usefulCabStats | Where-Object { $_.Changes -gt 0 } | Select-Object -ExpandProperty Name)
    if ($activeSignals.Count -gt 0) {
        $lines.Add("- Active cab signals for mapping: $($activeSignals -join ', ').")
    }
    else {
        $lines.Add("- No changing cab signals were detected; record while actively driving before using the trace for mapping.")
    }

    $body = $lines -join [Environment]::NewLine
}

if ([string]::IsNullOrWhiteSpace($OutPath)) {
    $suffix = if ($Json) { ".json" } else { ".md" }
    $OutPath = [System.IO.Path]::ChangeExtension($resolvedPath, ".analysis$suffix")
}

$outDir = Split-Path -Parent $OutPath
if (![string]::IsNullOrWhiteSpace($outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}

Set-Content -LiteralPath $OutPath -Value $body -Encoding ASCII
Write-Host "Wrote $OutPath"
