<#
.SYNOPSIS
  Builds Easy Delivery Co mods: all or only those you want.

.DESCRIPTION
  Mod layout (D:\github\EasyDeliveryMods\mods):
    LanCoop     - clean GitHub base (EasyDeliveryCoLanCoop.dll), untouched
    Shared      - EasyDeliveryCo.Shared.dll, driver/seat logic reused by LAN + Splitscreen
    Splitscreen - EasyDeliveryCoSplitscreen.dll (local P2, cameras, driver input)
    CustomSkins - EasyDeliveryCoCustomSkins.dll (StreamingAssets\CustomPlayer\*.png skins, F7 panel)

  The script prepares build prerequisites without touching the LAN repo sources:
    - copies BepInEx DLLs into mods\LanCoop\lib if missing
    - links mods\EasyDeliveryCo_Data to <GameDir>\EasyDeliveryCo_Data (junction,
      so the `..\EasyDeliveryCo_Data\Managed` HintPaths in every csproj resolve)

.EXAMPLE
  .\Build.ps1
  .\Build.ps1 -Mods Splitscreen, CustomSkins
  .\Build.ps1 -Mods All -No Splitscreen
  .\Build.ps1 -Mods LanCoop -Deploy
  .\Build.ps1 -Mods All -Configuration Release -Deploy -GameDir "C:\GOG Games\Easy Delivery Co"
#>
[CmdletBinding()]
param(
  [string[]]$Mods = @("All"),

  # Mods to skip, e.g. -Mods All -No Splitscreen
  [string[]]$No = @(),

  [string]$Configuration = "Release",

  [string]$GameDir = "C:\GOG Games\Easy Delivery Co",

  # Second game copy (local test client). Deployed plugin folders are mirrored
  # there automatically. Defaults to "<GameDir> (Client)"; skipped if missing.
  [string]$ClientDir = "",

  # Folder that already contains BepInEx.dll / 0Harmony.dll / BepInEx.Harmony.dll
  [string]$LibSource = "C:\GOG Games\Easy Delivery Co\EasyDeliveryCoLanCoop-src\lib",

  # Copy built DLLs into <GameDir>\BepInEx\plugins\<Mod>\
  [switch]$Deploy,

  [string]$DotnetExe = ""
)

$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ModsDir = Join-Path $RootDir "mods"

$Projects = @{
  Shared      = "Shared\EasyDeliveryCo.Shared.csproj"
  LanCoop     = "LanCoop\EasyDeliveryCoLanCoop.csproj"
  Splitscreen = "Splitscreen\EasyDeliveryCoSplitscreen.csproj"
  CustomSkins = "CustomSkins\EasyDeliveryCoCustomSkins.csproj"
}

# Plugin folder name + extra DLLs that must be deployed next to the plugin DLL.
$DeployMap = @{
  LanCoop     = @{ Folder = "EasyDeliveryCoLanCoop";     Dll = "EasyDeliveryCoLanCoop.dll";   Extra = @("EasyDeliveryCo.Shared.dll") }
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
  $localSdk = "C:\GOG Games\Easy Delivery Co\EasyDeliveryCoLanCoop-src\.dotnet\dotnet.exe"
  if (Test-Path -LiteralPath $localSdk) { return $localSdk }
  throw "No .NET SDK found. Install one or pass -DotnetExe <path to dotnet>."
}

function Ensure-BepInExLibs {
  $dest = Join-Path $ModsDir "LanCoop\lib"
  $needed = @("BepInEx.dll", "0Harmony.dll", "BepInEx.Harmony.dll")
  $missing = $needed | Where-Object { -not (Test-Path -LiteralPath (Join-Path $dest $_)) }
  if ($missing.Count -eq 0) {
    Write-Host "[deps] BepInEx libs present in mods\LanCoop\lib." -ForegroundColor DarkGray
    return
  }
  foreach ($f in $missing) {
    $src = Join-Path $LibSource $f
    if (-not (Test-Path -LiteralPath $src)) {
      throw "Missing $f (not in $dest nor in -LibSource $LibSource). Put BepInEx 5 DLLs there first."
    }
    Copy-Item -LiteralPath $src -Destination (Join-Path $dest $f) -Force
    Write-Host "[deps] Copied $f -> mods\LanCoop\lib\" -ForegroundColor DarkGray
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
  # Standard link used by the new mods (..\EasyDeliveryCo_Data from any mods\<X>\ project).
  $created = Ensure-JunctionLink (Join-Path $ModsDir "EasyDeliveryCo_Data") $target
  if (-not $created) {
    Write-Host "[deps] mods\EasyDeliveryCo_Data already exists, reusing." -ForegroundColor DarkGray
  }
  # The untouched LAN clone may reference a different relative path
  # (e.g. ..\EasyDeliveryCo steam version\EasyDeliveryCo_Data). Mirror whatever
  # its csproj HintPaths expect, without modifying its sources.
  $lancoopProj = Join-Path $ModsDir "LanCoop\EasyDeliveryCoLanCoop.csproj"
  if (Test-Path -LiteralPath $lancoopProj) {
    $text = [System.IO.File]::ReadAllText($lancoopProj)
    $rx = [regex]'<HintPath>([^<>]*?EasyDeliveryCo_Data)[\\/][^<>]*</HintPath>'
    foreach ($m in $rx.Matches($text)) {
      $relDir = $m.Groups[1].Value -replace '/', '\'
      $absDir = [System.IO.Path]::GetFullPath((Join-Path (Join-Path $ModsDir "LanCoop") $relDir))
      if ($absDir -ieq [System.IO.Path]::GetFullPath((Join-Path $ModsDir "EasyDeliveryCo_Data"))) { continue }
      Ensure-JunctionLink $absDir $target | Out-Null
    }
  }
}

function Normalize-ModList($list) {
  # Accept @("All") | "LanCoop,Splitscreen" | @("LanCoop", "Splitscreen")
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
  $key = (Get-Culture).TextInfo.ToTitleCase($m.ToLower())
  if ($m -ieq "CustomSkins") { $key = "CustomSkins" }
  if ($m -ieq "LanCoop") { $key = "LanCoop" }
  return $key
}

function Get-SelectedMods {
  $want = @()
  $all = $false
  foreach ($m in Normalize-ModList $Mods) {
    if ($m -ieq "All") { $all = $true; break }
    $key = To-ModKey $m
    if (-not $Projects.ContainsKey($key)) { throw "Unknown mod '$m'. Valid: All, LanCoop, Splitscreen, CustomSkins, Shared." }
    if ($want -notcontains $key) { $want += $key }
  }
  if ($all) { $want = @("Shared", "LanCoop", "Splitscreen", "CustomSkins") }
  # Splitscreen needs Shared built (ProjectReference handles it, but build it explicitly first for clarity).
  if ($want -contains "Splitscreen" -and $want -notcontains "Shared") { $want = @("Shared") + $want }
  foreach ($m in Normalize-ModList $No) {
    $key = To-ModKey $m
    if (-not $Projects.ContainsKey($key)) { throw "Unknown mod '$m' in -No. Valid: LanCoop, Splitscreen, CustomSkins, Shared." }
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
  if ($ClientDir -eq "") { $ClientDir = "$GameDir (Client)" }

  # Deploying over running games leaves locked half-updated DLLs: refuse early.
  foreach ($d in @($GameDir, $ClientDir)) {
    if (-not (Test-Path -LiteralPath $d)) { continue }
    $full = [System.IO.Path]::GetFullPath($d)
    $running = Get-Process -Name "EasyDeliveryCo" -ErrorAction SilentlyContinue | Where-Object {
      try { $_.MainModule.FileName.StartsWith($full, [System.StringComparison]::OrdinalIgnoreCase) }
      catch { $false }
    }
    if ($running) { throw "Close the game running from $d before deploying (DLLs are locked)." }
  }

  $pluginsDir = Join-Path $GameDir "BepInEx\plugins"
  $deployedFolders = @()
  foreach ($m in $selected) {
    if (-not $DeployMap.ContainsKey($m)) { continue }  # Shared deploys alongside Splitscreen
    $info = $DeployMap[$m]
    $outDir = Join-Path $ModsDir "$m\bin\$Configuration\netstandard2.1"
    if ($m -eq "LanCoop") { $outDir = Join-Path $ModsDir "LanCoop\bin\$Configuration\netstandard2.1" }
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
    if ($deployedFolders -notcontains $info.Folder) { $deployedFolders += $info.Folder }
  }
  Write-Host "Deployed to $pluginsDir" -ForegroundColor Green

  # Mirror the same plugin folders into the client copy, like the manual step.
  if (Test-Path -LiteralPath $ClientDir) {
    $clientPlugins = Join-Path $ClientDir "BepInEx\plugins"
    New-Item -ItemType Directory -Path $clientPlugins -Force | Out-Null
    foreach ($folder in $deployedFolders) {
      $srcDir = Join-Path $pluginsDir $folder
      $dstDir = Join-Path $clientPlugins $folder
      New-Item -ItemType Directory -Path $dstDir -Force | Out-Null
      Copy-Item -LiteralPath (Join-Path $srcDir "*") -Destination $dstDir -Force -Recurse
      Write-Host "[deploy] Mirrored $folder -> client copy." -ForegroundColor DarkGray
    }
  } else {
    Write-Host "[deploy] No client copy at $ClientDir, skipping mirror." -ForegroundColor DarkGray
  }
}
