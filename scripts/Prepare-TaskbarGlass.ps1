param([string]$Proxy)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskTools = Join-Path $taskRoot '.tools'
New-Item -ItemType Directory -Path $taskTools -Force | Out-Null
$taskVersion = '2026.2'
$taskBinary = Join-Path $taskTools "TranslucentTB-$taskVersion-x64.zip"
$taskSource = Join-Path $taskTools "TranslucentTB-$taskVersion-source.zip"
$taskRequests = @(
    @{ Path=$taskBinary; Url="https://github.com/TranslucentTB/TranslucentTB/releases/download/$taskVersion/TranslucentTB-portable-x64.zip"; Hash='0DBE8E0255C20E131CDE536DCD0AE490D45989A7D360E26D0150DFF1922AC420' },
    @{ Path=$taskSource; Url="https://github.com/TranslucentTB/TranslucentTB/archive/refs/tags/$taskVersion.zip"; Hash='73AC414E17710DE5765F2305BBBC1AAA4D88628C49D9B47A3159795D05F2F257' }
)
foreach ($taskRequest in $taskRequests) {
    if (!(Test-Path -LiteralPath $taskRequest.Path)) {
        $taskParameters = @{ Uri=$taskRequest.Url; OutFile=$taskRequest.Path }
        if ($Proxy) { $taskParameters.Proxy = $Proxy }
        Invoke-WebRequest @taskParameters
    }
    if ((Get-FileHash -LiteralPath $taskRequest.Path -Algorithm SHA256).Hash -ne $taskRequest.Hash) { throw 'Taskbar component checksum mismatch.' }
}
$taskDestination = Join-Path $taskTools "TranslucentTB-$taskVersion"
Expand-Archive -LiteralPath $taskBinary -DestinationPath $taskDestination -Force
# The portable utility is an independent executable, with its original source and license alongside it.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskArchive = [IO.Compression.ZipFile]::OpenRead($taskSource)
try {
    $taskLicense = $taskArchive.Entries | Where-Object FullName -eq "TranslucentTB-$taskVersion/LICENSE.md"
    if (!$taskLicense) { throw 'Missing component license.' }
    $taskReader = [IO.StreamReader]::new($taskLicense.Open())
    try { [IO.File]::WriteAllText((Join-Path $taskDestination 'LICENSE.txt'), $taskReader.ReadToEnd()) } finally { $taskReader.Dispose() }
} finally { $taskArchive.Dispose() }
Copy-Item -LiteralPath $taskSource -Destination (Join-Path $taskDestination 'SOURCE.zip') -Force
$taskNotice = @"
TranslucentTB $taskVersion - unmodified official portable x64 utility
Copyright: TranslucentTB contributors. License: GNU GPL version 3.
Project: https://github.com/TranslucentTB/TranslucentTB
Release: https://github.com/TranslucentTB/TranslucentTB/releases/tag/$taskVersion
Source archive: SOURCE.zip (matching upstream tag, including build and dependency manifests).
Rebuild instructions: CONTRIBUTING.md in SOURCE.zip.
Binary SHA-256: 0dbe8e0255c20e131cde536dcd0ae490d45989a7d360e26d0150dff1922ac420
Desktop Dock starts this as a separate process only when the glass option is enabled.
No TranslucentTB source is linked into Desktop Dock. Other TranslucentTB installations are left under their owner's control.
"@
[IO.File]::WriteAllText((Join-Path $taskDestination 'NOTICE.txt'), $taskNotice)
$taskManifest = [ordered]@{}
Get-ChildItem -LiteralPath $taskDestination -File | Where-Object Name -in @('TranslucentTB.exe','ExplorerTAP.dll','ExplorerHooks.dll','ProgramLog.dll','Xaml.dll','resources.pri') | ForEach-Object {
    $taskManifest[$_.Name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}
[IO.File]::WriteAllText((Join-Path $taskDestination 'component.json'), ($taskManifest | ConvertTo-Json))
Write-Output "Prepared verified TranslucentTB $taskVersion component."
