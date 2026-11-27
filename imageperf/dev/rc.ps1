# Runs a C# script in the test Rhino instance through RhinoCode (outside the app container) and prints its log.
# The script writes its log to dev\out\log.txt.
param([string]$Script, [int]$Rhino, [int]$TimeoutSec = 300)
$dev = 'C:\Users\User\Documents\Rhino Agent\ida-plugins\imageperf\dev'
New-Item -ItemType Directory -Force "$dev\out" | Out-Null
$log = "$dev\out\log.txt"; $done = "$dev\out\rc_done.txt"
Remove-Item $log, $done -ErrorAction SilentlyContinue
$cmd = "cmd /c `"`"C:\Program Files\Rhino 8\System\RhinoCode.exe`" --rhino rhinocode_remotepipe_$Rhino script `"$Script`" > `"$dev\out\rc_stdout.txt`" 2>&1 & echo done > `"$done`"`""
$null = Invoke-CimMethod Win32_Process -MethodName Create -Arguments @{CommandLine = $cmd}
$t = 0
# RhinoCode returns before the script finishes; the script's log file marks the end.
while (-not (Test-Path $log) -and $t -lt $TimeoutSec * 2) { [System.Threading.Thread]::Sleep(500); $t++ }
if (-not (Test-Path $log)) { "TIMEOUT" }
[System.Threading.Thread]::Sleep(300)
Get-Content "$dev\out\rc_stdout.txt" -Encoding utf8 -ErrorAction SilentlyContinue
if (Test-Path $log) { Get-Content $log -Encoding utf8 }
