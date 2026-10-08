# build.ps1 — build Te1000Daemon.exe (Release, x86, net472) with the in-box
# .NET Framework MSBuild. No .NET SDK / NuGet / internet required.
#
# Usage:  powershell -ExecutionPolicy Bypass -File daemon\build.ps1 [-Debug]
param([switch]$Debug)

$ErrorActionPreference = 'Stop'
$config = if ($Debug) { 'Debug' } else { 'Release' }

# 32-bit MSBuild so framework references resolve from Framework (x86), not Framework64.
$msbuild = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) {
    throw "MSBuild not found at $msbuild. A .NET Framework 4.x install is required."
}

# Resolve the TwinCAT System Manager interop the csproj references. If TwinCAT
# lives elsewhere, edit the HintPath in Te1000Daemon.csproj.
$tcatCandidates = @(
    'C:\Program Files (x86)\Beckhoff\TwinCAT\Functions\TE2000-HMI-Engineering\VisualStudio\2022\TCatSysManagerLib.dll',
    'C:\Program Files (x86)\Beckhoff\TwinCAT\Functions\TE2000-HMI-Engineering\VisualStudio\2026\TCatSysManagerLib.dll',
    'C:\Program Files (x86)\Beckhoff\TwinCAT\Functions\TE2000-HMI-Engineering\VisualStudio\TcXaeShell\TCatSysManagerLib.dll',
    # TwinCAT 4024 without TE2000: the Drive Manager 2 ships the same interop.
    'C:\TwinCAT\Functions\TE5950-Drive-Manager-2\TCatSysManagerLib.dll'
)
$found = $tcatCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$extra = @()
if (-not $found) {
    Write-Warning "TCatSysManagerLib.dll not found at the known paths; the build may fail. Edit the HintPath in Te1000Daemon.csproj."
} else {
    Write-Host "Using TCatSysManagerLib: $found"
    $extra += "-p:TCatSysManagerLibPath=$found"
}

$proj = Join-Path $PSScriptRoot 'Te1000Daemon.csproj'
Write-Host "Building $proj ($config|x86)..."
& $msbuild $proj -nologo "-p:Configuration=$config" -p:Platform=x86 -v:minimal @extra
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

$exe = Join-Path $PSScriptRoot "bin\$config\Te1000Daemon.exe"
Write-Host "OK: $exe"
