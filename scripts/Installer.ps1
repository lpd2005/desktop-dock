param([switch]$SkipPackage, [string]$Proxy = '')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $taskRoot
if (-not $SkipPackage) { & (Join-Path $PSScriptRoot 'Package.ps1') }
$taskVersion = ([xml](Get-Content -LiteralPath Directory.Build.props -Raw)).Project.PropertyGroup.Version
$taskPayload = Join-Path $taskRoot "artifacts/DesktopDock-$taskVersion-win-x64"
if (-not (Test-Path -LiteralPath (Join-Path $taskPayload 'DesktopDock.exe'))) { throw 'Verified application payload is missing.' }
$taskCompiler = & (Join-Path $PSScriptRoot 'Prepare-InstallerCompiler.ps1') -Proxy $Proxy
$taskOutput = Join-Path $taskRoot 'artifacts'
& $taskCompiler /Q "/DAppVersion=$taskVersion" "/DPayload=$taskPayload" "/DOutput=$taskOutput" (Join-Path $taskRoot 'packaging/DesktopDock.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$taskAssets = @("DesktopDock-$taskVersion-Setup-x64.exe", "DesktopDock-$taskVersion-win-x64.zip")
$taskLines = foreach ($taskAsset in $taskAssets) { (Get-FileHash -LiteralPath (Join-Path $taskOutput $taskAsset) -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $taskAsset }
[IO.File]::WriteAllLines((Join-Path $taskOutput 'SHA256SUMS.txt'), $taskLines, [Text.UTF8Encoding]::new($false))
Get-Item -LiteralPath (Join-Path $taskOutput $taskAssets[0]) | Select-Object Name,Length
