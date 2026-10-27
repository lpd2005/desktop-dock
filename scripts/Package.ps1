param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $taskRoot
$taskLocalDotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if (Test-Path -LiteralPath $taskLocalDotnet) {
    $taskDotnet = $taskLocalDotnet
    $env:DOTNET_ROOT = Split-Path -Parent $taskDotnet
    $env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools/cli'
    $env:NUGET_PACKAGES = Join-Path $taskRoot '.tools/nuget'
} else {
    $taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
& (Join-Path $PSScriptRoot 'Prepare-TaskbarGlass.ps1')
& $taskDotnet build DesktopDock.slnx -c Release -m:1 --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& $taskDotnet tests/DesktopDock.Checks/bin/Release/net10.0/DesktopDock.Checks.dll
if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
$taskOutput = Join-Path $taskRoot 'artifacts/DesktopDock-0.1.0-preview.17-win-x64'
& $taskDotnet publish src/DesktopDock.App/DesktopDock.App.csproj -c Release -r win-x64 --self-contained true -o $taskOutput -p:DebugType=none --disable-build-servers -m:1
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath README.md -Destination (Join-Path $taskOutput 'README.md')
Copy-Item -LiteralPath docs/DESIGN.md -Destination (Join-Path $taskOutput 'DESIGN.md')
Copy-Item -LiteralPath docs/VALIDATION.md -Destination (Join-Path $taskOutput 'VALIDATION.md')
Copy-Item -LiteralPath packaging/SafePreview.cmd -Destination (Join-Path $taskOutput '安全预览.cmd')
Copy-Item -LiteralPath packaging/RecoverDesktop.cmd -Destination (Join-Path $taskOutput '恢复桌面.cmd')
Copy-Item -LiteralPath packaging/RecoverTaskbar.cmd -Destination (Join-Path $taskOutput '恢复任务栏.cmd')
New-Item -ItemType Directory -Path (Join-Path $taskOutput 'licenses') -Force | Out-Null
Get-ChildItem -LiteralPath docs/licenses -File | Copy-Item -Destination (Join-Path $taskOutput 'licenses') -Force
foreach ($taskNotice in @('LICENSE.txt','ThirdPartyNotices.txt')) {
    $taskSourceNotice = Join-Path (Split-Path -Parent $taskDotnet) $taskNotice
    if (Test-Path -LiteralPath $taskSourceNotice) {
        Copy-Item -LiteralPath $taskSourceNotice -Destination (Join-Path $taskOutput ("licenses/Microsoft.NET." + $taskNotice))
    }
}
$taskForbidden = Get-ChildItem -LiteralPath $taskOutput -Recurse -File | Where-Object { $_.Name -in @('settings.json','desktop-state.json','taskbar-state.json','last-error.txt','smoke-report.json','feature-report.json','weather-cache.json') -or $_.Extension -in @('.log','.dmp','.etl','.pfx','.pem') }
if ($taskForbidden) { throw 'Package contains local runtime data.' }
$taskZip = "$taskOutput.zip"
Compress-Archive -Path (Join-Path $taskOutput '*') -DestinationPath $taskZip -Force
Get-Item -LiteralPath $taskZip | Select-Object Name,Length
