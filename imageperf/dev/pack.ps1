# Builds ImagePerf and packs it as a Yak package into pkg\ (and copies it to ..\dist).
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
dotnet build -c Release | Out-Host
$ver = ([xml](Get-Content ImagePerf.csproj)).Project.PropertyGroup.Version
Remove-Item pkg -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory pkg\net7.0, pkg\net48 | Out-Null
Copy-Item bin\Release\net7.0\ImagePerf.rhp, bin\Release\net7.0\ImagePerf.deps.json pkg\net7.0\ -ErrorAction SilentlyContinue
Copy-Item bin\Release\net48\ImagePerf.rhp pkg\net48\
Copy-Item manifest.yml pkg\
Push-Location pkg
& "C:\Program Files\Rhino 8\System\Yak.exe" build --platform win | Out-Host
Pop-Location
$yak = Get-ChildItem pkg -Filter "imageperf-$ver-*.yak" | Select-Object -First 1
Copy-Item $yak.FullName (Join-Path $root '..\dist') -Force
"packed $($yak.Name)"

