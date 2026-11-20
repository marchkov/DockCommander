param(
    [switch]$SelfTest,
    [string]$Listing,
    [string]$SharedRoot,
    [string]$DotnetPath
)

function Get-PeMachine {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $dos = New-Object byte[] 64
        if ($stream.Read($dos, 0, 64) -lt 64 -or $dos[0] -ne 0x4D -or $dos[1] -ne 0x5A) { return $null }
        $offset = [BitConverter]::ToInt32($dos, 0x3C)
        if ($offset -lt 0 -or $offset -gt 1024) { return $null }
        $stream.Position = $offset
        $pe = New-Object byte[] 6
        if ($stream.Read($pe, 0, 6) -lt 6 -or $pe[0] -ne 0x50 -or $pe[1] -ne 0x45) { return $null }
        return [BitConverter]::ToUInt16($pe, 4)
    }
    finally { $stream.Dispose() }
}

function Test-X64Pe {
    param([string]$Path)
    return (Get-PeMachine $Path) -eq 0x8664
}

function New-FakePe {
    param([string]$Path, [uint16]$Machine)
    $bytes = New-Object byte[] 96
    $bytes[0] = 0x4D; $bytes[1] = 0x5A
    [BitConverter]::GetBytes([int]64).CopyTo($bytes, 0x3C)
    $bytes[64] = 0x50; $bytes[65] = 0x45
    [BitConverter]::GetBytes($Machine).CopyTo($bytes, 68)
    [System.IO.File]::WriteAllBytes($Path, $bytes)
}

function Test-DotNet10Runtime {
    param([string]$Text, [string[]]$Roots, [string]$RuntimePath)
    $listed = $false
    foreach ($line in ($Text -split '\r?\n')) {
        if ($line -match '^Microsoft\.NETCore\.App\s+10\.') { $listed = $true }
    }
    foreach ($root in $Roots) {
        if (-not $root -or -not (Test-Path -LiteralPath $root)) { continue }
        $versions = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like "10.*" }
        foreach ($version in $versions) {
            if (Test-X64Pe (Join-Path $version.FullName "coreclr.dll")) { return $true }
        }
    }
    return [bool]($listed -and (Test-X64Pe $RuntimePath))
}

if ($SelfTest) {
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("dc-runtime-" + [guid]::NewGuid().ToString("n"))
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    try {
        $x64 = Join-Path $temp "dotnet.exe"
        $x86 = Join-Path $temp "dotnet-x86.exe"
        New-FakePe $x64 0x8664
        New-FakePe $x86 0x14C
        $arm = Join-Path $temp "dotnet-arm.exe"
        New-FakePe $arm 0xAA64
        $listing10 = "Microsoft.NETCore.App 10.0.1 [$temp]"
        $listing8 = "Microsoft.NETCore.App 8.0.11 [$temp]"
        if (Test-DotNet10Runtime -Text $listing8 -RuntimePath $x64) { throw "A .NET 8 host was accepted." }
        if (-not (Test-DotNet10Runtime -Text $listing10 -RuntimePath $x64)) { throw "An x64 .NET 10 host was rejected." }
        if (Test-DotNet10Runtime -Text $listing10 -RuntimePath $x86) { throw "An x86 .NET 10 host was accepted." }
        if (Test-DotNet10Runtime -Text $listing10 -RuntimePath $arm) { throw "An ARM64 .NET 10 host was accepted." }
        $empty = Join-Path $temp "empty\10.0.1"
        New-Item -ItemType Directory -Path $empty -Force | Out-Null
        if (Test-DotNet10Runtime -Text "" -Roots @((Split-Path $empty))) { throw "An empty runtime folder was accepted." }
        $custom = Join-Path $temp "Program Files (x86)\custom\10.0.1"
        New-Item -ItemType Directory -Path $custom -Force | Out-Null
        New-FakePe (Join-Path $custom "coreclr.dll") 0x8664
        if (-not (Test-DotNet10Runtime -Text "" -Roots @((Split-Path $custom)))) { throw "An x64 runtime outside Program Files was rejected." }
        New-FakePe (Join-Path $custom "coreclr.dll") 0x14C
        if (Test-DotNet10Runtime -Text "" -Roots @((Split-Path $custom))) { throw "An x86 coreclr.dll was accepted." }
    }
    catch {
        Write-Host $_.Exception.Message
        exit 1
    }
    finally { Remove-Item -LiteralPath $temp -Recurse -Force }
    exit 0
}

if (-not [Environment]::Is64BitOperatingSystem) {
    Write-Host "DockCommander needs 64-bit Windows and the shared framework Microsoft.NETCore.App 10 x64."
    exit 1
}

$listingText = $Listing
$runtimePath = $DotnetPath
if (-not $PSBoundParameters.ContainsKey("Listing")) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet) {
        $runtimePath = $dotnet.Source
        $listingText = (& dotnet --list-runtimes 2>$null | Out-String)
    }
}
$roots = @()
if ($SharedRoot) { $roots = @($SharedRoot) }
else {
    if ($env:ProgramFiles) { $roots += Join-Path $env:ProgramFiles "dotnet\shared\Microsoft.NETCore.App" }
    if ($env:DOTNET_ROOT) { $roots += Join-Path $env:DOTNET_ROOT "shared\Microsoft.NETCore.App" }
    if ($env:LOCALAPPDATA) { $roots += Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\shared\Microsoft.NETCore.App" }
    if ($runtimePath) { $roots += Join-Path (Split-Path -Parent $runtimePath) "shared\Microsoft.NETCore.App" }
}
if (Test-DotNet10Runtime -Text $listingText -Roots $roots -RuntimePath $runtimePath) { exit 0 }

Write-Host "DockCommander needs the shared framework Microsoft.NETCore.App 10 x64."
Write-Host "On Windows, install the .NET 10 Desktop Runtime x64. It includes that framework."
Write-Host "https://dotnet.microsoft.com/download/dotnet/10.0"
exit 1
