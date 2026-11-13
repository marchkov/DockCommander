param(
    [string]$Source,
    [string]$Destination = $(Join-Path $env:LOCALAPPDATA "DockCommander")
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
if (-not $Source) {
    if (Test-Path -LiteralPath (Join-Path $here "DC.exe")) { $Source = $here }
    else {
        $published = Join-Path $here "..\..\artifacts\win-x64\DC.exe"
        if (Test-Path -LiteralPath $published) { $Source = Split-Path -Parent (Resolve-Path -LiteralPath $published) }
    }
}
if (-not $Source -or -not (Test-Path -LiteralPath (Join-Path $Source "DC.exe"))) {
    throw "DC.exe was not found. Pass -Source with the published win-x64 folder."
}

$check = Join-Path $here "check-dotnet.ps1"
if (-not (Test-Path -LiteralPath $check)) { $check = Join-Path $here "..\check-dotnet.ps1" }
& powershell.exe -NoProfile -File $check
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Copy-Item -Path (Join-Path $Source "*") -Destination $Destination -Recurse -Force
$programs = [Environment]::GetFolderPath("Programs")
$shortcut = Join-Path $programs "DockCommander.lnk"
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = Join-Path $Destination "DC.exe"
$link.WorkingDirectory = $Destination
$link.Description = "DockCommander"
$link.Save()
Write-Host "Installed to $Destination"
Write-Host "Start menu shortcut: $shortcut"
