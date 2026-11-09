# Opens and closes the overlay a few times without driving input; Debug builds log timings to %APPDATA%\Snip\snip.log.
param([Parameter(Mandatory)] [string] $Exe, [int] $Runs = 4)
Add-Type -Name W -Namespace T -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
'@
Get-Process Snip -ErrorAction SilentlyContinue | Stop-Process -Force
$p = Start-Process $Exe -PassThru
Start-Sleep -Seconds 2
for ($i = 0; $i -lt $Runs; $i++) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $ev = [System.Threading.EventWaitHandle]::OpenExisting('Local\Snip.Capture.7f3c'); [void]$ev.Set(); $ev.Dispose()
    $h = [IntPtr]::Zero
    while ($h -eq [IntPtr]::Zero -and $sw.ElapsedMilliseconds -lt 5000) { $h = [T.W]::FindWindow([NullString]::Value, 'Snip'); Start-Sleep -Milliseconds 5 }
    Write-Host "run $i overlay after $($sw.ElapsedMilliseconds) ms "
    Start-Sleep -Milliseconds 700
    [void][T.W]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 700
}
$p.Refresh()
Write-Host ("idle working set {0:N1} MB" -f ($p.WorkingSet64 / 1MB))
$p | Stop-Process -Force
