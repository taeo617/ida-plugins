# Closes the test Rhino (only the one whose document is in this dev folder), reinstalls the plug-in,
# and starts a fresh test Rhino with the script server on dev\test_perf.3dm. Prints the new PID.
param([switch]$NoInstall)
$dev = 'C:\Users\User\Documents\Rhino Agent\ida-plugins\imageperf\dev'
Get-Process Rhino -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like 'test_p*' } | Stop-Process -Force
[System.Threading.Thread]::Sleep(1500)
if (-not $NoInstall) {
  Remove-Item "$dev\install-local.log" -ErrorAction SilentlyContinue
  $null = Invoke-CimMethod Win32_Process -MethodName Create -Arguments @{CommandLine = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$dev\install-local.ps1`""}
  $t = 0; while (-not (Test-Path "$dev\install-local.log") -and $t -lt 40) { [System.Threading.Thread]::Sleep(500); $t++ }
  [System.Threading.Thread]::Sleep(300)
  Get-Content "$dev\install-local.log" -TotalCount 1
}
$cmd = "`"C:\Program Files\Rhino 8\System\Rhino.exe`" /nosplash /runscript=`"_StartScriptServer`" `"$dev\test_perf.3dm`""
$r = Invoke-CimMethod Win32_Process -MethodName Create -Arguments @{CommandLine = $cmd}
$out = "$dev\out\rc_list.txt"
New-Item -ItemType Directory -Force "$dev\out" | Out-Null
$t = 0
while ($t -lt 90) {
  [System.Threading.Thread]::Sleep(3000); $t += 3
  $null = Invoke-CimMethod Win32_Process -MethodName Create -Arguments @{CommandLine = "cmd /c `"`"C:\Program Files\Rhino 8\System\RhinoCode.exe`" list > `"$out`" 2>&1`""}
  [System.Threading.Thread]::Sleep(1200)
  if ((Get-Content $out -ErrorAction SilentlyContinue) -match "remotepipe_$($r.ProcessId)") { break }
}
"PID $($r.ProcessId)"
