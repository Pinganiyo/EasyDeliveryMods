<#
.SYNOPSIS
  Test helper: runs two game instances on this PC, one as host and one as client.

.DESCRIPTION
  Same-PC LAN testing needs two separate game folders (separate BepInEx configs,
  so host and client get different nicknames and the client points at 127.0.0.1).
  This script:
    1. Creates the client folder as a copy of the game folder (unless it exists).
    2. Builds all mods and deploys them into BOTH folders.
    3. Writes Nickname (Host/Client) and HostAddress (127.0.0.1) into each cfg.
    4. Starts the host with --lancoop-server, waits, then starts the client
       with --lancoop-client.

  You still load the world by hand on the host; the client then connects
  (auto-discovery usually loops back, otherwise use the in-game console:
  connect 127.0.0.1:27777).

.EXAMPLE
  .\Test-LocalCoop.ps1
  .\Test-LocalCoop.ps1 -HostMonitor 1 -ClientMonitor 2
  .\Test-LocalCoop.ps1 -Borderless -ScreenWidth 1920 -ScreenHeight 1080
  .\Test-LocalCoop.ps1 -DelaySeconds 30 -HostNickname P1 -ClientNickname P2
  .\Test-LocalCoop.ps1 -NoLaunch
  .\Test-LocalCoop.ps1 -Stop
#>
[CmdletBinding()]
param(
  [string]$GameDir = "C:\GOG Games\Easy Delivery Co",

  # Second game copy used as the client. Defaults to "<GameDir> (Client)".
  [string]$ClientDir = "",

  [string]$HostNickname = "Host",
  [string]$ClientNickname = "Client",

  # Which display each instance opens on (Unity -monitor, 1-based).
  # Check numbering in Windows Settings > System > Display.
  [int]$HostMonitor = 1,
  [int]$ClientMonitor = 2,

  # Borderless window on the assigned monitor instead of exclusive fullscreen.
  [switch]$Borderless,

  # Optional explicit resolution (0 = leave to the game settings).
  [int]$ScreenWidth = 0,
  [int]$ScreenHeight = 0,

  # Seconds to wait between starting host and client (time to load the world).
  [int]$DelaySeconds = 20,

  # Fail if the client folder is missing instead of copying the game.
  [switch]$NoCopy,

  # Skip the Build.ps1 build+deploy step (mods must already be deployed).
  [switch]$NoBuild,

  # Prepare everything but do not start the game instances.
  [switch]$NoLaunch,

  # Kill test instances started from these folders, then exit.
  [switch]$Stop
)

$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ($ClientDir -eq "") { $ClientDir = "$GameDir (Client)" }

function Get-TestProcesses($dir) {
  $full = [System.IO.Path]::GetFullPath($dir)
  Get-Process -Name "EasyDeliveryCo" -ErrorAction SilentlyContinue | Where-Object {
    try { $_.MainModule.FileName.StartsWith($full, [System.StringComparison]::OrdinalIgnoreCase) }
    catch { $false }
  }
}

if ($Stop) {
  $killed = 0
  foreach ($d in @($GameDir, $ClientDir)) {
    if (-not (Test-Path -LiteralPath $d)) { continue }
    foreach ($p in Get-TestProcesses $d) {
      try { Stop-Process -InputObject $p -Force; $killed++ } catch { }
    }
  }
  Write-Host "Stopped $killed test instance(s)."
  exit 0
}

$hostExe = Join-Path $GameDir "EasyDeliveryCo.exe"
if (-not (Test-Path -LiteralPath $hostExe)) { throw "Host exe not found: $hostExe" }

# 1. Client folder: copy of the game (separate BepInEx config + saves scope).
if (-not (Test-Path -LiteralPath $ClientDir)) {
  if ($NoCopy) { throw "Client folder missing: $ClientDir (pass without -NoCopy to create it by copying the game)." }
  Write-Host "Copying game to client folder (one-time, may take a few minutes)..." -ForegroundColor Cyan
  Write-Host "  From: $GameDir"
  Write-Host "  To:   $ClientDir"
  & robocopy $GameDir $ClientDir /MIR /XD "BepInEx\cache" /XF "*.log" /NFL /NDL /NJH /NJS | Out-Null
  if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE." }
  Write-Host "Copy done." -ForegroundColor Green
}
else {
  Write-Host "Reusing client folder: $ClientDir" -ForegroundColor DarkGray
}
$clientExe = Join-Path $ClientDir "EasyDeliveryCo.exe"
if (-not (Test-Path -LiteralPath $clientExe)) { throw "Client exe not found: $clientExe" }

# 2. Build mods, deploy to host + client copy (Build.ps1 mirrors automatically).
if (-not $NoBuild) {
  $buildPs = Join-Path $RootDir "Build.ps1"
  powershell -NoProfile -ExecutionPolicy Bypass -File $buildPs -No Splitscreen -Deploy -GameDir $GameDir -ClientDir $ClientDir
  if ($LASTEXITCODE -ne 0) { throw "Build.ps1 failed." }
}

# 3. Per-instance cfg: different nicknames, client points at localhost.
function Set-CfgValue($cfgPath, $key, $value) {
  $dir = Split-Path -Parent $cfgPath
  if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
  if (-not (Test-Path -LiteralPath $cfgPath)) {
    "[Profile]`nNickname = $value`n" | Set-Content -LiteralPath $cfgPath -Encoding UTF8
    return
  }
  $lines = [System.IO.File]::ReadAllLines($cfgPath)
  $done = $false
  for ($i = 0; $i -lt $lines.Length; $i++) {
    if ($lines[$i] -match ("^\s*" + [regex]::Escape($key) + "\s*=")) {
      $lines[$i] = "$key = $value"
      $done = $true
    }
  }
  if (-not $done) { $lines += "$key = $value" }
  [System.IO.File]::WriteAllLines($cfgPath, $lines)
}

Set-CfgValue (Join-Path $GameDir "BepInEx\config\EasyDeliveryCoLanCoop.cfg") "Nickname" $HostNickname
Set-CfgValue (Join-Path $ClientDir "BepInEx\config\EasyDeliveryCoLanCoop.cfg") "Nickname" $ClientNickname
Set-CfgValue (Join-Path $ClientDir "BepInEx\config\EasyDeliveryCoLanCoop.cfg") "HostAddress" "127.0.0.1"
Write-Host "[test] Host nickname=$HostNickname, client nickname=$ClientNickname, client host=127.0.0.1." -ForegroundColor DarkGray

function Get-DisplayArgs($monitor) {
  $a = @("-monitor", "$monitor")
  if ($ScreenWidth -gt 0) { $a += @("-screen-width", "$ScreenWidth") }
  if ($ScreenHeight -gt 0) { $a += @("-screen-height", "$ScreenHeight") }
  if ($Borderless) { $a += "-popupwindow" } else { $a += @("-screen-fullscreen", "1") }
  return $a
}

$hostArgs = @("--lancoop-server") + (Get-DisplayArgs $HostMonitor)
$clientArgs = @("--lancoop-client") + (Get-DisplayArgs $ClientMonitor)

if ($NoLaunch) {
  Write-Host "Prepared (no launch). Start manually:"
  Write-Host "  Host:   `"$hostExe`" $($hostArgs -join ' ')"
  Write-Host "  Client: `"$clientExe`" $($clientArgs -join ' ')"
  exit 0
}

# 4. Launch host, wait, launch client.
Write-Host "Starting HOST on monitor $HostMonitor`: $hostExe $($hostArgs -join ' ')" -ForegroundColor Cyan
Start-Process -FilePath $hostExe -ArgumentList $hostArgs -WorkingDirectory $GameDir | Out-Null
Write-Host "Load your world on the host now. Starting client in $DelaySeconds s..." -ForegroundColor Yellow
Start-Sleep -Seconds $DelaySeconds
Write-Host "Starting CLIENT on monitor $ClientMonitor`: $clientExe $($clientArgs -join ' ')" -ForegroundColor Cyan
Start-Process -FilePath $clientExe -ArgumentList $clientArgs -WorkingDirectory $ClientDir | Out-Null

Write-Host ""
Write-Host "Next steps:" -ForegroundColor Green
Write-Host "  1. Host: load into the world and leave it running."
Write-Host "  2. Client: click 'Connect to Host' in the LAN Coop panel (top-left)."
Write-Host "     If discovery misses, tick Console and type: connect 127.0.0.1:27777"
Write-Host "  3. Check BepInEx/LogOutput.log on each side on failure."
Write-Host "To stop both instances later: .\Test-LocalCoop.ps1 -Stop"
