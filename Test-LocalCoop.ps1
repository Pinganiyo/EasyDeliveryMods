<#
.SYNOPSIS
  Test helper: builds the coop2 mod (mods\coop2), deploys it, and launches the game once.

.DESCRIPTION
  Local 2-player splitscreen runs in a single game instance: P2 is spawned inside
  the same game and uses a second controller (or keyboard fallback).
  This script:
    1. Builds the mods and deploys them into the game folder (unless -NoBuild).
    2. Launches EasyDeliveryCo.exe on the chosen monitor.

  Once the game is running, load a world and press F8 if splitscreen does not
  start automatically.

.EXAMPLE
  .\Test-LocalCoop.ps1
  .\Test-LocalCoop.ps1 -Monitor 2 -Borderless
  .\Test-LocalCoop.ps1 -Borderless -ScreenWidth 1920 -ScreenHeight 1080
  .\Test-LocalCoop.ps1 -NoBuild
  .\Test-LocalCoop.ps1 -NoLaunch
  .\Test-LocalCoop.ps1 -Stop
#>
[CmdletBinding()]
param(
  [string]$GameDir = "C:\GOG Games\Easy Delivery Co",

  # Which display the game opens on (Unity -monitor, 1-based).
  # Check numbering in Windows Settings > System > Display.
  [int]$Monitor = 1,

  # Borderless window on the assigned monitor instead of exclusive fullscreen.
  [switch]$Borderless,

  # Optional explicit resolution (0 = leave to the game settings).
  [int]$ScreenWidth = 0,
  [int]$ScreenHeight = 0,

  # Skip the Build.ps1 build+deploy step (mods must already be deployed).
  [switch]$NoBuild,

  # Prepare everything but do not start the game.
  [switch]$NoLaunch,

  # Kill game instances started from this folder, then exit.
  [switch]$Stop
)

$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Get-GameProcesses($dir) {
  $full = [System.IO.Path]::GetFullPath($dir)
  Get-Process -Name "EasyDeliveryCo" -ErrorAction SilentlyContinue | Where-Object {
    try { $_.MainModule.FileName.StartsWith($full, [System.StringComparison]::OrdinalIgnoreCase) }
    catch { $false }
  }
}

if ($Stop) {
  $killed = 0
  foreach ($p in Get-GameProcesses $GameDir) {
    try { Stop-Process -InputObject $p -Force; $killed++ } catch { }
  }
  Write-Host "Stopped $killed game instance(s)."
  exit 0
}

$gameExe = Join-Path $GameDir "EasyDeliveryCo.exe"
if (-not (Test-Path -LiteralPath $gameExe)) { throw "Game exe not found: $gameExe" }

# 1. Build mods and deploy them to the game folder.
if (-not $NoBuild) {
  $buildPs = Join-Path $RootDir "Build.ps1"
  powershell -NoProfile -ExecutionPolicy Bypass -File $buildPs -Mods Coop2 -Deploy -GameDir $GameDir
  if ($LASTEXITCODE -ne 0) { throw "Build.ps1 failed." }
}

# 2. Launch the game.
$gameArgs = @("-monitor", "$Monitor")
if ($ScreenWidth -gt 0) { $gameArgs += @("-screen-width", "$ScreenWidth") }
if ($ScreenHeight -gt 0) { $gameArgs += @("-screen-height", "$ScreenHeight") }
if ($Borderless) { $gameArgs += "-popupwindow" } else { $gameArgs += @("-screen-fullscreen", "1") }

if ($NoLaunch) {
  Write-Host "Prepared (no launch). Start manually:"
  Write-Host "  `"$gameExe`" $($gameArgs -join ' ')"
  exit 0
}

Write-Host "Starting game on monitor $Monitor`: $gameExe $($gameArgs -join ' ')" -ForegroundColor Cyan
Start-Process -FilePath $gameExe -ArgumentList $gameArgs -WorkingDirectory $GameDir | Out-Null

Write-Host ""
Write-Host "Next steps:" -ForegroundColor Green
Write-Host "  1. Load into a world."
Write-Host "  2. Splitscreen should start automatically; press F8 to toggle it."
Write-Host "  3. Check BepInEx/LogOutput.log in the game folder if P2 does not appear."
Write-Host "To stop the game later: .\Test-LocalCoop.ps1 -Stop"
