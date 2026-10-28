param([string]$Proxy = '')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskVersion = '6.7.3'
$taskCompiler = Join-Path $taskRoot ".tools/InnoSetup-$taskVersion/ISCC.exe"
function Prepare-Language {
    $taskLanguage = Join-Path (Split-Path -Parent $taskCompiler) 'Languages/ChineseSimplified.isl'
    $taskLanguageHash = '7d544b9bb1d142cfa11f2e5d3cc8abe2e55f8e066c5124e3772675aa236e1278'
    if (-not (Test-Path -LiteralPath $taskLanguage)) {
        $taskLanguageRequest = @{ Uri = 'https://raw.githubusercontent.com/jrsoftware/issrc/is-6_7_3/Files/Languages/Unofficial/ChineseSimplified.isl'; OutFile = $taskLanguage; TimeoutSec = 60 }
        if ($Proxy) { $taskLanguageRequest.Proxy = $Proxy }
        Invoke-WebRequest @taskLanguageRequest
    }
    if ((Get-FileHash -LiteralPath $taskLanguage -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskLanguageHash) { throw 'Installer translation SHA-256 mismatch.' }
}
if (Test-Path -LiteralPath $taskCompiler) { Prepare-Language; return $taskCompiler }
$taskDownload = Join-Path $taskRoot ".tools/build-downloads/innosetup-$taskVersion.exe"
New-Item -ItemType Directory -Path (Split-Path -Parent $taskDownload) -Force | Out-Null
$taskExpected = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'
if (-not (Test-Path -LiteralPath $taskDownload)) {
    $taskRequest = @{ Uri = "https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-$taskVersion.exe"; OutFile = $taskDownload; TimeoutSec = 180 }
    if ($Proxy) { $taskRequest.Proxy = $Proxy }
    Invoke-WebRequest @taskRequest
}
if ((Get-FileHash -LiteralPath $taskDownload -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskExpected) { throw 'Installer compiler SHA-256 mismatch.' }
$taskDirectory = Split-Path -Parent $taskCompiler
$taskProcess = Start-Process -FilePath $taskDownload -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS','/CURRENTUSER',('/DIR="' + $taskDirectory + '"')) -WindowStyle Hidden -PassThru -Wait
if ($taskProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $taskCompiler)) { throw 'Installer compiler preparation failed.' }
Prepare-Language
return $taskCompiler
