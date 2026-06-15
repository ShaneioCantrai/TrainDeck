param(
    [int]$Port = 5555
)

$ErrorActionPreference = "Stop"
$androidHome = $env:ANDROID_HOME
if (!$androidHome) {
    $androidHome = Join-Path $env:LOCALAPPDATA "Android\Sdk"
}
$adb = Join-Path $androidHome "platform-tools\adb.exe"

if (!(Test-Path -LiteralPath $adb)) {
    throw "adb.exe was not found at $adb"
}

& $adb start-server | Out-Host
$devices = & $adb devices
$ready = $devices | Where-Object { $_ -match "`tdevice$" }
$offline = $devices | Where-Object { $_ -match "`toffline$" }
$unauthorized = $devices | Where-Object { $_ -match "`tunauthorized$" }

if ($offline) {
    Write-Host "Device is visible but offline."
    Write-Host "Unlock the tablet, reconnect USB, and approve the USB debugging prompt."
    exit 1
}

if ($unauthorized) {
    Write-Host "Device is visible but unauthorized."
    Write-Host "Tick 'Always allow from this computer' on the tablet and tap Allow."
    exit 1
}

if (!$ready) {
    Write-Host "No authorized USB debugging device is visible."
    Write-Host "Connect over USB first, then approve the tablet prompt with 'Always allow from this computer'."
    exit 1
}

$route = & $adb shell ip route
$ip = ($route | Select-String -Pattern "src\s+([0-9.]+)" | Select-Object -First 1).Matches.Groups[1].Value

if (!$ip) {
    Write-Host "Could not detect tablet Wi-Fi IP from adb shell ip route."
    Write-Host $route
    exit 1
}

& $adb tcpip $Port | Out-Host
Start-Sleep -Seconds 2
& $adb connect "$ip`:$Port"
Write-Host "Wireless ADB target: $ip`:$Port"
Write-Host "You can unplug USB after adb devices shows this target as 'device'."
