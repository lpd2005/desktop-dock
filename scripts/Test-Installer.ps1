param()
$ErrorActionPreference = 'Stop'
$taskRoot = (Split-Path -Parent $PSScriptRoot)
Set-Location -LiteralPath $taskRoot
$taskVersion = ([xml](Get-Content -LiteralPath Directory.Build.props -Raw)).Project.PropertyGroup.Version
$taskInstaller = Join-Path $taskRoot "artifacts/DesktopDock-$taskVersion-Setup-x64.exe"
$taskCase = Join-Path $taskRoot ('artifacts/installer-check-' + [guid]::NewGuid().ToString('N'))
$taskInstall = Join-Path $taskCase 'application'
$taskExe = Join-Path $taskInstall 'DesktopDock.exe'
$taskUninstallKey = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/{1F94CBEF-8E1A-4A28-B1A1-9FC24B81D401}_is1'
if (Test-Path $taskUninstallKey) { throw 'Run installer checks on a clean profile; an installed copy already exists.' }
New-Item -ItemType Directory -Path $taskCase -Force | Out-Null
$taskChecks = [ordered]@{}
function Require([bool]$Condition, [string]$Name) {
    $taskChecks[$Name] = $Condition
    if (-not $Condition) { throw "Installer check failed: $Name" }
}
function Install-Application {
    $taskProcess = Start-Process -FilePath $taskInstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS',('/DIR="' + $taskInstall + '"')) -WindowStyle Hidden -PassThru -Wait
    Require ($taskProcess.ExitCode -eq 0) 'InstallerReturnsSuccess'
}
$taskRunKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
$taskOldRun = $taskRunKey.GetValue('DesktopDock', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
$taskOldKind = if ($null -ne $taskOldRun) { $taskRunKey.GetValueKind('DesktopDock') } else { $null }
$taskConfig = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'DesktopDock/settings.json'
$taskConfigBefore = if (Test-Path -LiteralPath $taskConfig) { (Get-FileHash -LiteralPath $taskConfig -Algorithm SHA256).Hash } else { $null }
try {
    Install-Application
    Require ((Test-Path -LiteralPath $taskExe) -and (Test-Path $taskUninstallKey)) 'InstalledProgramAndUninstallerAreRegistered'
    Require (Test-Path -LiteralPath (Join-Path $taskInstall '使用说明.txt')) 'UserGuideIsInstalled'
    $taskProbe = Start-Process -FilePath $taskExe -ArgumentList ('--quit --data-dir "' + (Join-Path $taskCase 'runtime-data') + '"') -WindowStyle Hidden -PassThru -Wait
    Require ($taskProbe.ExitCode -eq 0) 'SelfContainedApplicationStartsWithoutExternalRuntime'
    $taskSentinel = Join-Path $taskInstall 'unrelated-test-file.txt'
    [IO.File]::WriteAllText($taskSentinel, 'Synthetic file not owned by the installer.')
    $taskRunCommand = '"' + $taskExe + '" --startup'
    $taskRunKey.SetValue('DesktopDock', $taskRunCommand, [Microsoft.Win32.RegistryValueKind]::String)
    Install-Application
    Require ((Test-Path $taskSentinel) -and $taskRunKey.GetValue('DesktopDock') -eq $taskRunCommand) 'UpgradePreservesStartupChoiceAndUnrelatedFile'
    $taskUninstaller = Join-Path $taskInstall 'unins000.exe'
    $taskProcess = Start-Process -FilePath $taskUninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -PassThru -Wait
    Require ($taskProcess.ExitCode -eq 0 -and -not (Test-Path $taskExe) -and -not (Test-Path $taskUninstallKey)) 'UninstallRemovesApplicationAndRegistration'
    Require ($null -eq $taskRunKey.GetValue('DesktopDock')) 'UninstallRemovesItsOwnStartupEntry'
    Require (Test-Path $taskSentinel) 'UninstallPreservesUnrelatedFiles'
    $taskConfigAfter = if (Test-Path -LiteralPath $taskConfig) { (Get-FileHash -LiteralPath $taskConfig -Algorithm SHA256).Hash } else { $null }
    Require ($taskConfigBefore -eq $taskConfigAfter) 'InstallUpgradeAndUninstallPreserveUserConfiguration'
    [IO.File]::WriteAllText((Join-Path $taskCase 'installer-report.json'), ($taskChecks | ConvertTo-Json))
    $taskChecks | ConvertTo-Json
} finally {
    if (Test-Path -LiteralPath (Join-Path $taskInstall 'unins000.exe')) {
        $taskCleanup = Start-Process -FilePath (Join-Path $taskInstall 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -PassThru -Wait
    }
    if ($null -eq $taskOldRun) { $taskRunKey.DeleteValue('DesktopDock', $false) }
    else { $taskRunKey.SetValue('DesktopDock', $taskOldRun, $taskOldKind) }
    $taskRunKey.Dispose()
}
