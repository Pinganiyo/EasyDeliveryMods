<#
.SYNOPSIS
  Builds Easy Delivery Co mods: all or only those you want.

.DESCRIPTION
  Mod layout (D:\github\EasyDeliveryMods\mods):
    Shared      - EasyDeliveryCo.Shared.dll, shared driver/seat logic
    Splitscreen - EasyDeliveryCoSplitscreen.dll (local P2, cameras, driver input)
    CustomSkins - EasyDeliveryCoCustomSkins.dll (StreamingAssets\CustomPlayer\*.png skins, F7 panel)

  The script prepares build prerequisites:
    - copies BepInEx DLLs into mods\lib if missing (from -LibSource)
    - links mods\EasyDeliveryCo_Data to <GameDir>\EasyDeliveryCo_Data (junction,
      so the ..\EasyDeliveryCo_Data\Managed HintPaths in every csproj resolve)

.EXAMPLE
  .\Build.ps1
  .\Build.ps1 -Mods Splitscreen, CustomSkins
  .\Build.ps1 -Mods All -No CustomSkins
  .\Build.ps1 -Mods All -Configuration Release -Deploy -GameDir "C:\GOG Games\Easy Delivery Co"
#>
[CmdletBinding()]
param(
  [string[]]$Mods = @("All"),

  # Mods to skip, e.g. -Mods All -No CustomSkins
  [string[]]$No = @(),

  [string]$Configuration = "Release",

  [string]$GameDir = "C:\GOG Games\Easy Delivery Co",

  # Folder that already contains BepInEx.dll / 0Harmony.dll / BepInEx.Harmony.dll
  # (the BepInEx 5 install's core folder).
  [string]$LibSource = "C:\GOG Games\Easy Delivery Co\BepInEx\core",

  # Copy built DLLs into <GameDir>\BepInEx\plugins\<Mod>\
  [switch]$Deploy,

  [string]$DotnetExe = ""
)

$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ModsDir = Join-Path $RootDir "mods"
$LibDir  = Join-Path $ModsDir "lib"

$Projects = @{
  Shared      = "Shared\EasyDeliveryCo.Shared.csproj"
  Splitscreen = "Splitscreen\EasyDeliveryCoSplitscreen.csproj"
  CustomSkins = "CustomSkins\EasyDeliveryCoCustomSkins.csproj"
}

# Plugin folder name + extra DLLs that must be deployed next to the plugin DLL.
$DeployMap = @{
  Splitscreen = @{ Folder = "EasyDeliveryCoSplitscreen"; Dll = "EasyDeliveryCoSplitscreen.dll"; Extra = @("EasyDeliveryCo.Shared.dll") }
  CustomSkins = @{ Folder = "EasyDeliveryCoCustomSkins"; Dll = "EasyDeliveryCoCustomSkins.dll"; Extra = @() }
}

function Resolve-Dotnet {
  if ($DotnetExe -ne "") { return $DotnetExe }
  try {
    $cmd = Get-Command dotnet -ErrorAction Stop
    $v = & $cmd.Source --version 2>$null
    if ($LASTEXITCODE -eq 0 -and "$v" -match '^\d+\.\d+') { return $cmd.Source }
  } catch { }
  throw "No .NET SDK found. Install one or pass -DotnetExe <path to dotnet>."
}

function Ensure-BepInExLibs {
  $needed = @("BepInEx.dll", "0Harmony.dll", "BepInEx.Harmony.dll")
  New-Item -ItemType Directory -Path $LibDir -Force | Out-Null
  $missing = $needed | Where-Object { -not (Test-Path -LiteralPath (Join-Path $LibDir $_)) }
  if ($missing.Count -eq 0) {
    Write-Host "[deps] BepInEx libs present in mods\lib." -ForegroundColor DarkGray
    return
  }
  foreach ($f in $missing) {
    $src = Join-Path $LibSource $f
    if (-not (Test-Path -LiteralPath $src)) {
      throw "Missing $f (not in $LibDir nor in -LibSource $LibSource). Put BepInEx 5 DLLs there first."
    }
    Copy-Item -LiteralPath $src -Destination (Join-Path $LibDir $f) -Force
    Write-Host "[deps] Copied $f -> mods\lib\" -ForegroundColor DarkGray
  }
}

function Ensure-JunctionLink($LinkPath, $TargetPath) {
  if (Test-Path -LiteralPath $LinkPath) { return $false }
  $parent = Split-Path -Parent $LinkPath
  if (-not (Test-Path -LiteralPath $parent)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
  }
  try {
    New-Item -ItemType Junction -Path $LinkPath -Target $TargetPath | Out-Null
    Write-Host "[deps] Created junction $LinkPath -> $TargetPath" -ForegroundColor DarkGray
  } catch {
    Write-Warning "Junction failed for $LinkPath ($($_.Exception.Message)), copying required Managed DLLs instead."
    $managed = Join-Path $LinkPath "Managed"
    New-Item -ItemType Directory -Path $managed -Force | Out-Null
    $dlls = @(
      "UnityEngine.dll", "UnityEngine.CoreModule.dll", "UnityEngine.SharedInternalsModule.dll",
      "UnityEngine.PhysicsModule.dll", "UnityEngine.IMGUIModule.dll",
      "UnityEngine.InputLegacyModule.dll", "UnityEngine.TextRenderingModule.dll",
      "UnityEngine.AudioModule.dll", "UnityEngine.AssetBundleModule.dll",
      "UnityEngine.AnimationModule.dll", "UnityEngine.ImageConversionModule.dll",
      "Unity.InputSystem.dll", "Assembly-CSharp.dll"
    )
    $srcManaged = Join-Path $TargetPath "Managed"
    foreach ($d in $dlls) {
      Copy-Item -LiteralPath (Join-Path $srcManaged $d) -Destination (Join-Path $managed $d) -Force
    }
  }
  return $true
}

function Ensure-GameDataLink {
  $target = Join-Path $GameDir "EasyDeliveryCo_Data"
  if (-not (Test-Path -LiteralPath (Join-Path $target "Managed\Assembly-CSharp.dll"))) {
    throw "Game DLLs not found at $target. Pass the correct -GameDir."
  }
  # Standard link used by the mods (..\EasyDeliveryCo_Data from any mods\<X>\ project).
  $created = Ensure-JunctionLink (Join-Path $ModsDir "EasyDeliveryCo_Data") $target
  if (-not $created) {
    Write-Host "[deps] mods\EasyDeliveryCo_Data already exists, reusing." -ForegroundColor DarkGray
  }
}

function Normalize-ModList($list) {
  # Accept @("All") | "Splitscreen,CustomSkins" | @("Splitscreen", "CustomSkins")
  $flat = @()
  foreach ($m in $list) {
    foreach ($p in ("$m" -split ',')) {
      $t = $p.Trim()
      if ($t -ne "") { $flat += $t }
    }
  }
  return $flat
}

function To-ModKey($m) {
  if ($m -ieq "CustomSkins") { return "CustomSkins" }
  return (Get-Culture).TextInfo.ToTitleCase($m.ToLower())
}

function Get-SelectedMods {
  $want = @()
  $all = $false
  foreach ($m in Normalize-ModList $Mods) {
    if ($m -ieq "All") { $all = $true; break }
    $key = To-ModKey $m
    if (-not $Projects.ContainsKey($key)) { throw "Unknown mod '$m'. Valid: All, Splitscreen, CustomSkins, Shared." }
    if ($want -notcontains $key) { $want += $key }
  }
  if ($all) { $want = @("Shared", "Splitscreen", "CustomSkins") }
  # Splitscreen needs Shared built (ProjectReference handles it, but build it explicitly first for clarity).
  if ($want -contains "Splitscreen" -and $want -notcontains "Shared") { $want = @("Shared") + $want }
  foreach ($m in Normalize-ModList $No) {
    $key = To-ModKey $m
    if (-not $Projects.ContainsKey($key)) { throw "Unknown mod '$m' in -No. Valid: Splitscreen, CustomSkins, Shared." }
    $want = @($want | Where-Object { $_ -ine $key })
  }
  if ($want.Count -eq 0) { throw "Nothing to build: -No excluded every selected mod." }
  return $want
}

# -- Main ----------------------------------------------------------------------

$dotnet = Resolve-Dotnet
Write-Host "Using dotnet: $dotnet" -ForegroundColor DarkGray

if (-not (Test-Path -LiteralPath $GameDir)) {
  throw "GameDir not found: $GameDir"
}

Ensure-BepInExLibs
Ensure-GameDataLink

$selected = Get-SelectedMods
Write-Host "Building ($Configuration): $($selected -join ', ')" -ForegroundColor Cyan

$failed = @()
foreach ($m in $selected) {
  $proj = Join-Path $ModsDir $Projects[$m]
  Write-Host "-- $m --------------------------------" -ForegroundColor Cyan
  & $dotnet build $proj -c $Configuration --nologo -v minimal
  if ($LASTEXITCODE -ne 0) { $failed += $m }
}

if ($failed.Count -gt 0) { throw "Build FAILED for: $($failed -join ', ')" }
Write-Host "Build OK: $($selected -join ', ')" -ForegroundColor Green

if ($Deploy) {
  # Deploying over a running game leaves locked half-updated DLLs: refuse early.
  $full = [System.IO.Path]::GetFullPath($GameDir)
  $running = Get-Process -Name "EasyDeliveryCo" -ErrorAction SilentlyContinue | Where-Object {
    try { $_.MainModule.FileName.StartsWith($full, [System.StringComparison]::OrdinalIgnoreCase) }
    catch { $false }
  }
  if ($running) { throw "Close the game running from $GameDir before deploying (DLLs are locked)." }

  $pluginsDir = Join-Path $GameDir "BepInEx\plugins"
  foreach ($m in $selected) {
    if (-not $DeployMap.ContainsKey($m)) { continue }  # Shared deploys alongside Splitscreen
    $info = $DeployMap[$m]
    $outDir = Join-Path $ModsDir "$m\bin\$Configuration\netstandard2.1"
    $destDir = Join-Path $pluginsDir $info.Folder
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    $files = @($info.Dll) + $info.Extra
    foreach ($f in $files) {
      $src = Join-Path $outDir $f
      if (-not (Test-Path -LiteralPath $src)) {
        # Shared.dll lands in the referencing project's output, not its own deploy step.
        if ($m -eq "Splitscreen") { $src = Join-Path $ModsDir "Splitscreen\bin\$Configuration\netstandard2.1\$f" }
      }
      if (-not (Test-Path -LiteralPath $src)) { throw "Deploy: expected output not found: $src" }
      Copy-Item -LiteralPath $src -Destination (Join-Path $destDir $f) -Force
      Write-Host "[deploy] $f -> BepInEx\plugins\$($info.Folder)\" -ForegroundColor DarkGray
    }
  }
  Write-Host "Deployed to $pluginsDir" -ForegroundColor Green
}
