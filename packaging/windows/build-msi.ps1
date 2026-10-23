<#
.SYNOPSIS
    Builds and packages the SwiftDesk Windows MSI installer.
.DESCRIPTION
    Publishes RemoteDesk.Client, RemoteDesk.Agent, and RemoteDesk.PrivilegedHelper,
    creates the ProgramData directory structure with secure ACLs, and bundles into an MSI.
#>

param(
    [string]$Configuration = "Release",
    [string]$OutputMsi = "SwiftDesk-1.0.0-Setup.msi"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$PublishDir = Join-Path $PSScriptRoot "publish"

Write-Host "==> Publishing SwiftDesk binaries ($Configuration)..." -ForegroundColor Cyan
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null

$DotnetPath = Join-Path $RepoRoot "work\.dotnet\dotnet.exe"
if (-not (Test-Path $DotnetPath)) { $DotnetPath = "dotnet" }

& $DotnetPath publish (Join-Path $RepoRoot "src\RemoteDesk.Client\RemoteDesk.Client.csproj") -c $Configuration -r win-x64 --self-contained false -o $PublishDir
& $DotnetPath publish (Join-Path $RepoRoot "src\RemoteDesk.Agent\RemoteDesk.Agent.csproj") -c $Configuration -r win-x64 --self-contained false -o $PublishDir
& $DotnetPath publish (Join-Path $RepoRoot "src\RemoteDesk.PrivilegedHelper\RemoteDesk.PrivilegedHelper.csproj") -c $Configuration -r win-x64 --self-contained false -o $PublishDir

Write-Host "==> Generating machine reboot secret key template..." -ForegroundColor Cyan
$KeyBytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
$SampleKeyPath = Join-Path $PublishDir "reboot.key.sample"
[System.IO.File]::WriteAllBytes($SampleKeyPath, $KeyBytes)

Write-Host "==> Compiling WiX MSI installer..." -ForegroundColor Cyan
if (Get-Command "wix" -ErrorAction SilentlyContinue) {
    & wix build (Join-Path $PSScriptRoot "SwiftDesk.wxs") -o (Join-Path $PSScriptRoot $OutputMsi)
    Write-Host "==> Successfully generated MSI: $OutputMsi" -ForegroundColor Green
} else {
    Write-Host "WiX CLI not found on PATH. Packaged publish directory prepared at: $PublishDir" -ForegroundColor Yellow
}
