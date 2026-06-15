param(
    [int]$Port = 47331,
    [switch]$Keyboard
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "windows\TrainDeck.BridgeApp\TrainDeck.BridgeApp.csproj"
$args = @("run", "--project", $project, "--")
$args += @("--port", $Port)
if ($Keyboard) {
    $args += "--keyboard"
}

& dotnet @args
