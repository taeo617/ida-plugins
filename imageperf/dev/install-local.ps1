# Copies the freshly built ImagePerf into the real Rhino package folder.
# Run OUTSIDE the Claude app container (Invoke-CimMethod Win32_Process Create), with no Rhino that has ImagePerf loaded.
$root = 'C:\Users\User\Documents\Rhino Agent\ida-plugins\imageperf'
$ver = ([xml](Get-Content "$root\ImagePerf.csproj")).Project.PropertyGroup.Version
$pkg = Join-Path $env:APPDATA "McNeel\Rhinoceros\packages\8.0\imageperf\$ver"
$log = "$root\dev\install-local.log"
try {
  New-Item -ItemType Directory -Force "$pkg\net7.0", "$pkg\net48" | Out-Null
  Copy-Item "$root\pkg\net7.0\*" "$pkg\net7.0\" -Force -ErrorAction Stop
  Copy-Item "$root\pkg\net48\*" "$pkg\net48\" -Force -ErrorAction Stop
  Copy-Item "$root\pkg\manifest.yml" "$pkg\manifest.yml" -Force -ErrorAction Stop
  # Rhino loads the version named in <package>\manifest.txt (Yak writes it on install).
  Set-Content (Join-Path (Split-Path $pkg -Parent) 'manifest.txt') $ver -NoNewline -Encoding ascii
  "OK $(Get-Date -Format s)" | Out-File $log -Encoding utf8
  Get-ChildItem $pkg -Recurse -File | ForEach-Object { "$($_.FullName) $($_.Length) $($_.LastWriteTime)" } | Out-File $log -Append -Encoding utf8
} catch { "FAIL $_" | Out-File $log -Encoding utf8 }
