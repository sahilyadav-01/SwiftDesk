<#
.SYNOPSIS
    Builds and packages the SwiftDesk Windows MSI installer.
.DESCRIPTION
    Publishes RemoteDesk.Client, RemoteDesk.Agent, and RemoteDesk.PrivilegedHelper,
    creates the ProgramData directory structure with secure ACLs, and bundles into an MSI.
#>

param(
    [string]$Configuration = "Release",
    [string]$OutputMsi = "SwiftDesk-1.2.1-Setup.msi",
    [string]$ServerUrl = "https://academy-sending-treo-apparent.trycloudflare.com",
    [string]$BootstrapToken = "Jwm6reOSdWLx5Q0Y2hzA6dcurwYUbcHyTKOdqMosdEM=",
    [string]$OperatorKey = "bdVrcJpCp2ih6UCl6f1rwoynECaWOte1FY2Y3WPtyjo="
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$PublishDir = Join-Path $PSScriptRoot "publish"

Write-Host "==> Publishing SwiftDesk binaries ($Configuration)..." -ForegroundColor Cyan
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null

$DotnetPath = Join-Path $RepoRoot "work\.dotnet\dotnet.exe"
if (-not (Test-Path $DotnetPath)) { $DotnetPath = "dotnet" }

function Invoke-SwiftDeskPublish([string]$ProjectPath) {
    & $DotnetPath publish $ProjectPath -c $Configuration -r win-x64 --self-contained true -o $PublishDir
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing '$ProjectPath' failed with exit code $LASTEXITCODE"
    }
}

Invoke-SwiftDeskPublish (Join-Path $RepoRoot "src\RemoteDesk.Client\RemoteDesk.Client.csproj")
Invoke-SwiftDeskPublish (Join-Path $RepoRoot "src\RemoteDesk.Agent\RemoteDesk.Agent.csproj")
Invoke-SwiftDeskPublish (Join-Path $RepoRoot "src\RemoteDesk.PrivilegedHelper\RemoteDesk.PrivilegedHelper.csproj")

Write-Host "==> Creating SwiftDesk.exe launcher alias and branding assets..." -ForegroundColor Cyan
Copy-Item (Join-Path $PublishDir "RemoteDesk.Client.exe") (Join-Path $PublishDir "SwiftDesk.exe") -Force
Copy-Item (Join-Path $PublishDir "RemoteDesk.Client.dll") (Join-Path $PublishDir "SwiftDesk.dll") -Force
Copy-Item (Join-Path $PublishDir "RemoteDesk.Client.runtimeconfig.json") (Join-Path $PublishDir "SwiftDesk.runtimeconfig.json") -Force
Copy-Item (Join-Path $PublishDir "RemoteDesk.Client.deps.json") (Join-Path $PublishDir "SwiftDesk.deps.json") -Force
Copy-Item (Join-Path $PSScriptRoot "SwiftDesk.ico") (Join-Path $PublishDir "SwiftDesk.ico") -Force
$AssetsDir = Join-Path $PublishDir "Assets"
if (-not (Test-Path $AssetsDir)) { New-Item -ItemType Directory -Path $AssetsDir -Force | Out-Null }
Copy-Item (Join-Path $RepoRoot "docs\assets\logo.png") (Join-Path $AssetsDir "logo.png") -Force
Copy-Item (Join-Path $RepoRoot "src\RemoteDesk.Client\Assets\app-icon.png") (Join-Path $AssetsDir "app-icon.png") -Force
Copy-Item (Join-Path $PSScriptRoot "SwiftDesk.ico") (Join-Path $AssetsDir "SwiftDesk.ico") -Force

Write-Host "==> Generating machine reboot secret key template..." -ForegroundColor Cyan
$Rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$KeyBytes = New-Object byte[] 32
$Rng.GetBytes($KeyBytes)
$SampleKeyPath = Join-Path $PublishDir "reboot.key.sample"
[System.IO.File]::WriteAllBytes($SampleKeyPath, $KeyBytes)

Write-Host "==> Compiling WiX MSI installer..." -ForegroundColor Cyan
$WixFile = Join-Path $PSScriptRoot "SwiftDesk.wxs"
$MsiDestination = Join-Path $PSScriptRoot $OutputMsi
$WixDefines = @(
    "-d", "ServerUrl=$ServerUrl",
    "-d", "BootstrapToken=$BootstrapToken",
    "-d", "OperatorKey=$OperatorKey"
)

if (Get-Command "wix" -ErrorAction SilentlyContinue) {
    & wix build $WixFile -arch x64 -b PublishDir=$PublishDir @WixDefines -o $MsiDestination
} elseif (Test-Path $DotnetPath) {
    & $DotnetPath tool run wix build $WixFile -arch x64 -b PublishDir=$PublishDir @WixDefines -o $MsiDestination
} else {
    throw "WiX CLI not found. Packaged publish directory prepared at: $PublishDir"
}

if ($LASTEXITCODE -ne 0) {
    throw "WiX build failed with exit code $LASTEXITCODE"
}
Write-Host "==> Successfully generated MSI: $OutputMsi" -ForegroundColor Green
