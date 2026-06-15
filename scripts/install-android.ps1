param(
    [string]$ApkPath = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if (!$ApkPath) {
    $ApkPath = Join-Path $repoRoot "android\app\build\outputs\apk\debug\app-debug.apk"
}
$androidHome = $env:ANDROID_HOME
if (!$androidHome) {
    $androidHome = Join-Path $env:LOCALAPPDATA "Android\Sdk"
}
$adb = Join-Path $androidHome "platform-tools\adb.exe"

if (!(Test-Path -LiteralPath $adb)) {
    throw "adb.exe was not found at $adb"
}

if (!(Test-Path -LiteralPath $ApkPath)) {
    throw "APK was not found at $ApkPath. Build it first with: gradle :app:assembleDebug"
}

& $adb start-server | Out-Host
$devices = & $adb devices
$deviceLines = $devices | Where-Object { $_ -match "`t(device|unauthorized|offline)$" }

if (!$deviceLines) {
    Write-Host "No Android device is visible to ADB."
    Write-Host "On the tablet: unlock it, set USB mode to File transfer, and approve the USB debugging prompt."
    exit 1
}

if ($deviceLines -match "`tunauthorized$") {
    Write-Host "Device is visible but unauthorized."
    Write-Host "On the tablet: tick 'Always allow from this computer' and tap Allow, then rerun this script."
    exit 1
}

if ($deviceLines -match "`toffline$") {
    Write-Host "Device is visible but offline."
    Write-Host "On the tablet: unlock it, toggle USB debugging off/on, reconnect USB, and approve the prompt."
    exit 1
}

& $adb install -r $ApkPath
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
