# Builds Snip and installs it for the current user (no admin): %LOCALAPPDATA%\Programs\Snip\Snip.exe
# Adds a Start menu shortcut and an entry in Settings > Apps > Installed apps (uninstall from there or with
# tools\uninstall.ps1). Snip registers itself to start with Windows on launch; toggle it from the tray menu.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $env:LOCALAPPDATA 'Programs\Snip'
$exe = Join-Path $target 'Snip.exe'

Get-Process Snip -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish (Join-Path $root 'src\Snip') -c Release -o (Join-Path $root 'artifacts\publish') -nologo
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $root 'artifacts\publish\Snip.exe') $target -Force
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $target -Force

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'Snip.lnk'))
$link.TargetPath = $exe
$link.WorkingDirectory = $target
$link.IconLocation = "$exe,0"
$link.Description = 'Screenshot and annotate'
$link.Save()

$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Snip'
$uninstallCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$target\uninstall.ps1`""
New-Item $uninstallKey -Force | Out-Null
$values = @{
    DisplayName          = 'Snip'
    DisplayIcon          = "$exe,0"
    DisplayVersion       = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
    Publisher            = 'buildthomas'
    InstallLocation      = $target
    InstallDate          = (Get-Date -Format 'yyyyMMdd')
    URLInfoAbout         = 'https://github.com/buildthomas/snip'
    UninstallString      = $uninstallCmd
    QuietUninstallString = $uninstallCmd
}
foreach ($v in $values.GetEnumerator()) { Set-ItemProperty $uninstallKey -Name $v.Key -Value $v.Value }
$sizeKb = [int][Math]::Ceiling((Get-ChildItem $target -File | Measure-Object Length -Sum).Sum / 1KB)
foreach ($v in @{ EstimatedSize = $sizeKb; NoModify = 1; NoRepair = 1 }.GetEnumerator()) {
    Set-ItemProperty $uninstallKey -Name $v.Key -Value $v.Value -Type DWord
}

Start-Process $exe
Write-Host "Installed to $target and started. Press Win+Shift+S or PrtSc to capture."
