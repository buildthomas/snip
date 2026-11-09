# Esc closes at once with nothing drawn; with annotations it needs a second Esc within 1.5 s.
# Drives the real mouse and keyboard. Usage: powershell -STA -File tests\e2e\EscGuard.ps1 -Exe <Snip.exe> -OutDir <dir>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $OutDir
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Input.ps1')
function Cleanup { if ($script:proc -and -not $script:proc.HasExited) { $script:proc.Kill() } }

function Open-Overlay {
    Start-Process $Exe -ArgumentList '--capture' | Out-Null
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ((Overlay) -eq [IntPtr]::Zero -and $sw.ElapsedMilliseconds -lt 3000) { Start-Sleep -Milliseconds 20 }
    if ((Overlay) -eq [IntPtr]::Zero) { Fail 'overlay did not appear' }
    Start-Sleep -Milliseconds 400
    $p = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $script:x0 = $p.X + 600; $script:y0 = $p.Y + 300
    [In]::Drag($x0, $y0, $x0 + 640, $y0 + 400)
}
function Esc { [In]::Tap(0x1B); Start-Sleep -Milliseconds 250 }
function Expect-Open([string] $what) { if ((Overlay) -eq [IntPtr]::Zero) { Fail "overlay closed: $what" }; Write-Host "ok: open - $what" }
function Expect-Closed([string] $what) { if ((Overlay) -ne [IntPtr]::Zero) { Fail "overlay still open: $what" }; Write-Host "ok: closed - $what" }

New-Item -ItemType Directory -Force $OutDir | Out-Null
Get-Process Snip -ErrorAction SilentlyContinue | Stop-Process -Force
$script:proc = Start-Process $Exe -PassThru
Start-Sleep -Milliseconds 1500

Open-Overlay
Esc
Expect-Closed 'single Esc with nothing drawn'

Open-Overlay
[In]::Tap(0x52); [In]::Drag($x0 + 40, $y0 + 40, $x0 + 240, $y0 + 160)
Esc
Expect-Open 'first Esc with a rectangle drawn'
Shot 'esc-hint.png'
Start-Sleep -Milliseconds 1800
Esc
Expect-Open 'second Esc after the 1.5 s window'
Esc
Expect-Closed 'quick second Esc'

Open-Overlay
[In]::Tap(0x52); [In]::Drag($x0 + 40, $y0 + 40, $x0 + 240, $y0 + 160)
[In]::Key(0x11,$false); [In]::Tap(0x5A); [In]::Key(0x11,$true)
Esc
Expect-Closed 'single Esc after undoing everything'

Open-Overlay
[In]::Tap(0x54); [In]::Click($x0 + 60, $y0 + 280)
[In]::Tap(0x41); [In]::Tap(0x42)
Esc
Expect-Open 'Esc while typing commits the text'
Esc
Expect-Open 'first Esc after committed text'
Esc
Expect-Closed 'second Esc'

Cleanup
Write-Host 'PASS'
