# End-to-end smoke test. Drives the real mouse and keyboard, so don't touch them while it runs.
# Usage: powershell -ExecutionPolicy Bypass -File tests\e2e\E2E.ps1 -Exe <path to Snip.exe> -OutDir <folder>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $Hotkey = 'WinShiftS'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Input.ps1')
function Cleanup { if ($script:proc -and -not $script:proc.HasExited) { $script:proc.Kill() } }

New-Item -ItemType Directory -Force $OutDir | Out-Null
Get-Process Snip -ErrorAction SilentlyContinue | Stop-Process -Force
$script:proc = Start-Process $Exe -PassThru
Start-Sleep -Milliseconds 1500
if ($proc.HasExited) { Fail 'Snip exited on startup' }
$proc.Refresh()
Write-Host ("Idle after start: working set {0:N1} MB, private {1:N1} MB" -f ($proc.WorkingSet64 / 1MB), ($proc.PrivateMemorySize64 / 1MB))
$cpu0 = $proc.TotalProcessorTime
Start-Sleep -Seconds 3
$proc.Refresh()
Write-Host ("CPU used while idle for 3s: {0} ms" -f [int]($proc.TotalProcessorTime - $cpu0).TotalMilliseconds)

[System.Windows.Forms.Clipboard]::Clear()
$sw = [Diagnostics.Stopwatch]::StartNew()
if ($Hotkey -eq 'PrtSc') { [In]::Tap(0x2C) }
else { [In]::Key(0x5B, $false); [In]::Key(0x10, $false); [In]::Key(0x53, $false); [In]::Key(0x53, $true); [In]::Key(0x10, $true); [In]::Key(0x5B, $true) }
while ((Overlay) -eq [IntPtr]::Zero -and $sw.ElapsedMilliseconds -lt 3000) { Start-Sleep -Milliseconds 5 }
$h = Overlay
if ($h -eq [IntPtr]::Zero) { Fail "overlay did not appear after $Hotkey" }
Write-Host "Overlay visible after $($sw.ElapsedMilliseconds) ms"
Start-Sleep -Milliseconds 400

$fg = [In]::GetForegroundWindow()
$sb = New-Object System.Text.StringBuilder 256; [void][In]::GetClassName($fg, $sb, 256)
if ($fg -ne $h) { Fail "overlay is not the foreground window (foreground class: $($sb.ToString()))" }
Get-Process ScreenClippingHost, ScreenSketch -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "WARN: Windows snipping process running: $($_.Name)" }

$p = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$x0 = $p.X + 600; $y0 = $p.Y + 300
[In]::Drag($x0, $y0, $x0 + 640, $y0 + 400)
Shot '1-selected.png'
[In]::Tap(0x52); [In]::Drag($x0 + 40, $y0 + 40, $x0 + 240, $y0 + 160)      # R: rectangle
[In]::Tap(0x41); [In]::Drag($x0 + 400, $y0 + 300, $x0 + 260, $y0 + 170)    # A: arrow
[In]::Tap(0x45); [In]::Drag($x0 + 300, $y0 + 40, $x0 + 420, $y0 + 120)     # E: ellipse
[In]::Tap(0x48); [In]::Drag($x0 + 40, $y0 + 230, $x0 + 300, $y0 + 230)     # H: highlighter
[In]::Tap(0x4E); [In]::Click($x0 + 520, $y0 + 60); [In]::Click($x0 + 560, $y0 + 100)   # N: numbers 1, 2
[In]::Tap(0x42); [In]::Drag($x0 + 450, $y0 + 300, $x0 + 600, $y0 + 340)    # B: redact
[In]::Tap(0x50); [In]::Drag($x0 + 60, $y0 + 330, $x0 + 200, $y0 + 370)     # P: pen
[In]::Tap(0x54); [In]::Click($x0 + 60, $y0 + 280)                          # T: text
foreach ($c in [char[]]'SNIP TEST') { if ($c -eq ' ') { [In]::Tap(0x20) } else { [In]::Key(0x10,$false); [In]::Tap([byte][char]$c); [In]::Key(0x10,$true) } }
[In]::Tap(0x0D)
[In]::Tap(0x4C); [In]::Drag($x0 + 20, $y0 + 390, $x0 + 300, $y0 + 390)     # L: line
[In]::Key(0x11,$false); [In]::Tap(0x5A); [In]::Key(0x11,$true)             # Ctrl+Z removes the line
Start-Sleep -Milliseconds 200
Shot '2-annotated.png'
[In]::Key(0x11,$false); [In]::Tap(0x43); [In]::Key(0x11,$true)             # Ctrl+C
Start-Sleep -Milliseconds 500

if ((Overlay) -ne [IntPtr]::Zero) { Fail 'overlay still open after Ctrl+C' }
$img = [System.Windows.Forms.Clipboard]::GetImage()
if (-not $img) { Fail 'no image on clipboard' }
Write-Host "Clipboard image: $($img.Width) x $($img.Height)"
$img.Save((Join-Path $OutDir '3-clipboard.png'))
if ($img.Width -ne 641 -or $img.Height -ne 401) { Fail "unexpected clipboard size $($img.Width)x$($img.Height)" }
$fmts = [System.Windows.Forms.Clipboard]::GetDataObject().GetFormats() -join ', '
Write-Host "Clipboard formats: $fmts"

Start-Sleep -Milliseconds 800
$fg = [In]::GetForegroundWindow(); $sb.Clear() | Out-Null; [void][In]::GetClassName($fg, $sb, 256)
Write-Host "Foreground after close: $($sb.ToString())"
Shot '4-after.png'
$proc.Refresh()
Write-Host ("After capture: working set {0:N1} MB, private {1:N1} MB" -f ($proc.WorkingSet64 / 1MB), ($proc.PrivateMemorySize64 / 1MB))
Cleanup
Write-Host 'PASS'
