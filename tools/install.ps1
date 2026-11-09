# Builds Snip and installs it for the current user (no admin): %LOCALAPPDATA%\Programs\Snip\Snip.exe
# Snip registers itself to start with Windows on first launch; toggle it from the tray menu.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $env:LOCALAPPDATA 'Programs\Snip'

Get-Process Snip -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish (Join-Path $root 'src\Snip') -c Release -o (Join-Path $root 'artifacts\publish') -nologo
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $root 'artifacts\publish\Snip.exe') $target -Force
Start-Process (Join-Path $target 'Snip.exe')
Write-Host "Installed to $target and started. Press Win+Shift+S or PrtSc to capture."
