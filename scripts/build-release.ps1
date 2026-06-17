param(
    [string]$Version = "0.1.0"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$releaseDir = Join-Path $root "dist\release"
$bridgeDir = Join-Path $releaseDir "TrainDeckBridge-win-x64"
$packagesDir = Join-Path $releaseDir "packages"
$bridgeProject = Join-Path $root "windows\TrainDeck.BridgeApp\TrainDeck.BridgeApp.csproj"
$androidDir = Join-Path $root "android"
$installerScript = Join-Path $root "installer\TrainDeckBridge.iss"

New-Item -ItemType Directory -Force -Path $releaseDir, $packagesDir | Out-Null
if (Test-Path $bridgeDir) {
    Remove-Item -LiteralPath $bridgeDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $bridgeDir | Out-Null
Get-ChildItem -LiteralPath $packagesDir -File | Remove-Item -Force

dotnet publish $bridgeProject -c Release -r win-x64 --self-contained false -o $bridgeDir

foreach ($name in @("README.md", "LICENSE", "CONTRIBUTING.md")) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $bridgeDir $name) -Force
}

Push-Location $androidDir
try {
    gradle :app:assembleDebug
}
finally {
    Pop-Location
}

$apkSource = Join-Path $androidDir "app\build\outputs\apk\debug\app-debug.apk"
$apkOut = Join-Path $packagesDir "TrainDeck-android-$Version-debug.apk"
Copy-Item -LiteralPath $apkSource -Destination $apkOut -Force

$zipPath = Join-Path $packagesDir "TrainDeckBridge-$Version-win-x64.zip"
if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $bridgeDir "*") -DestinationPath $zipPath -Force

$isccCandidates = @()
foreach ($candidate in @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
)) {
    if (Test-Path -LiteralPath $candidate) {
        $isccCandidates += $candidate
    }
}

if ($isccCandidates.Count -gt 0) {
    $iscc = $isccCandidates[0]
    & $iscc "/DAppVersion=$Version" "/DSourceDir=$bridgeDir" "/DOutputDir=$packagesDir" $installerScript
}
else {
    Write-Warning "Inno Setup 6 compiler not found; skipped setup exe."
}

$hashFile = Join-Path $packagesDir "SHA256SUMS.txt"
Get-ChildItem -LiteralPath $packagesDir -File |
    Where-Object { $_.Name -ne "SHA256SUMS.txt" } |
    Sort-Object Name |
    ForEach-Object {
        $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName
        "{0}  {1}" -f $hash.Hash.ToLowerInvariant(), $_.Name
    } |
    Set-Content -LiteralPath $hashFile -Encoding ASCII

Write-Host "Release artifacts:"
Get-ChildItem -LiteralPath $packagesDir -File | Sort-Object Name | ForEach-Object {
    Write-Host (" - {0} ({1:n0} bytes)" -f $_.Name, $_.Length)
}
