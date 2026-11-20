param(
    [string]$Source,
    [string]$Destination = $(Join-Path $env:LOCALAPPDATA "DockCommander"),
    [switch]$SelfTest
)

$ErrorActionPreference = "Stop"
$InstallManifestName = "DockCommander.install.json"

function Assert-InstallDestination {
    param([string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $root = [System.IO.Path]::GetPathRoot($full).TrimEnd('\')
    if ($full -eq $root) { throw "Refusing to use a drive root: $full" }
    $blocked = @($env:USERPROFILE, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:WINDIR) |
        Where-Object { $_ } |
        ForEach-Object { [System.IO.Path]::GetFullPath($_).TrimEnd('\') }
    if ($blocked -contains $full) { throw "Refusing to use a system or profile directory: $full" }
    return $full
}

function Get-RelativeFiles {
    param([string]$Root)
    $full = [System.IO.Path]::GetFullPath($Root)
    @(Get-ChildItem -LiteralPath $full -Recurse -File -Force | ForEach-Object {
        $_.FullName.Substring($full.Length).TrimStart('\', '/')
    })
}

function ConvertTo-InstallJson {
    param([string[]]$Files)
    $items = @($Files | ForEach-Object { '"' + ($_ -replace '\\', '\\' -replace '"', '\"') + '"' })
    return '{ "product": "DockCommander", "files": [' + ($items -join ", ") + "] }"
}

function Install-DockCommander {
    param([string]$From, [string]$To, [switch]$SkipRuntimeCheck)
    $source = [System.IO.Path]::GetFullPath($From).TrimEnd('\')
    $destination = Assert-InstallDestination $To
    if ($source -eq $destination) { throw "The source and destination must be different folders." }
    if (-not (Test-Path -LiteralPath (Join-Path $source "DC.exe") -PathType Leaf)) {
        throw "DC.exe was not found in $source"
    }
    if (-not $SkipRuntimeCheck) {
        $check = Join-Path $PSScriptRoot "check-dotnet.ps1"
        if (-not (Test-Path -LiteralPath $check)) { $check = Join-Path $PSScriptRoot "..\check-dotnet.ps1" }
        & powershell.exe -NoProfile -File $check
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    $previous = @()
    $manifestPath = Join-Path $destination $InstallManifestName
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        $previous = @((Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json).files)
    }
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Copy-Item -Path (Join-Path $source "*") -Destination $destination -Recurse -Force
    $recorded = @(Get-RelativeFiles $source | Where-Object { $_ -ne $InstallManifestName })
    $recorded += $InstallManifestName
    foreach ($relative in $previous) {
        if ($recorded -notcontains $relative) {
            $stale = [System.IO.Path]::GetFullPath((Join-Path $destination $relative))
            $prefix = $destination + "\"
            if ($stale.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $stale -PathType Leaf)) {
                Remove-Item -LiteralPath $stale -Force
            }
        }
    }
    Set-Content -LiteralPath $manifestPath -Value (ConvertTo-InstallJson $recorded) -Encoding ascii
    $programs = [Environment]::GetFolderPath("Programs")
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut((Join-Path $programs "DockCommander.lnk"))
    $link.TargetPath = Join-Path $destination "DC.exe"
    $link.WorkingDirectory = $destination
    $link.Description = "DockCommander"
    $link.Save()
    Write-Host "Installed to $destination"
}

if ($SelfTest) {
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("dc-install-" + [guid]::NewGuid().ToString("n"))
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    $shortcut = Join-Path ([Environment]::GetFolderPath("Programs")) "DockCommander.lnk"
    $savedShortcut = $null
    if (Test-Path -LiteralPath $shortcut) { $savedShortcut = [System.IO.File]::ReadAllBytes($shortcut) }
    try {
        $source = Join-Path $temp "source"
        $next = Join-Path $temp "next"
        $destination = Join-Path $temp "DockCommander"
        New-Item -ItemType Directory -Path (Join-Path $source "lib") -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $source "DC.exe") -Value "first"
        Set-Content -LiteralPath (Join-Path $source "lib\old.dll") -Value "old"
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $destination "keep.txt") -Value "keep"
        Install-DockCommander -From $source -To $destination -SkipRuntimeCheck
        if ((Get-Content -LiteralPath (Join-Path $destination "keep.txt") -Raw) -notmatch "keep") { throw "An existing file was overwritten." }
        if ((Get-Content -LiteralPath (Join-Path $destination "DC.exe") -Raw) -notmatch "first") { throw "DC.exe was not installed." }
        New-Item -ItemType Directory -Path $next -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $next "DC.exe") -Value "second"
        Install-DockCommander -From $next -To $destination -SkipRuntimeCheck
        if ((Get-Content -LiteralPath (Join-Path $destination "DC.exe") -Raw) -notmatch "second") { throw "The update did not replace DC.exe." }
        if (Test-Path -LiteralPath (Join-Path $destination "lib\old.dll")) { throw "A file from the previous install was left behind." }
        if ((Get-Content -LiteralPath (Join-Path $destination "keep.txt") -Raw) -notmatch "keep") { throw "The update removed a file it did not install." }
        & (Join-Path $PSScriptRoot "uninstall.ps1") -Destination $destination
        if (-not (Test-Path -LiteralPath (Join-Path $destination "keep.txt"))) { throw "The uninstaller removed a file outside the manifest." }
        if (Test-Path -LiteralPath (Join-Path $destination "DC.exe")) { throw "The uninstaller left DC.exe behind." }
        $blocked = $false
        try { Install-DockCommander -From $source -To $env:USERPROFILE -SkipRuntimeCheck } catch { $blocked = $true }
        if (-not $blocked) { throw "The installer accepted the user profile as a destination." }
    }
    catch {
        Write-Host $_.Exception.Message
        exit 1
    }
    finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
        if ($savedShortcut) { [System.IO.File]::WriteAllBytes($shortcut, $savedShortcut) }
        elseif (Test-Path -LiteralPath $shortcut) {
            $shell = New-Object -ComObject WScript.Shell
            $target = $shell.CreateShortcut($shortcut).TargetPath
            if ($target -and $target.StartsWith($temp, [System.StringComparison]::OrdinalIgnoreCase)) {
                Remove-Item -LiteralPath $shortcut -Force
            }
        }
    }
    exit 0
}

$here = $PSScriptRoot
if (-not $Source) {
    if (Test-Path -LiteralPath (Join-Path $here "DC.exe")) { $Source = $here }
    else {
        $published = Join-Path $here "..\..\artifacts\win-x64\DC.exe"
        if (Test-Path -LiteralPath $published) { $Source = Split-Path -Parent (Resolve-Path -LiteralPath $published) }
    }
}
if (-not $Source) { throw "DC.exe was not found. Pass -Source with the published win-x64 folder." }
Install-DockCommander -From $Source -To $Destination
