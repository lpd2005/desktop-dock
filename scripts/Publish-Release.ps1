param([switch]$Publish, [switch]$CheckOnly, [switch]$VerifyPublished)
$ErrorActionPreference = 'Stop'
if ($VerifyPublished -and $Publish) { throw 'Verification cannot publish a release.' }
$taskRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $taskRoot
$taskVersion = ([xml](Get-Content -LiteralPath Directory.Build.props -Raw)).Project.PropertyGroup.Version
if ($taskVersion -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$') { throw 'Invalid release version.' }
if ((git remote get-url origin) -notin @('https://github.com/lpd2005/desktop-dock.git','https://github.com/lpd2005/desktop-dock')) { throw 'Unexpected repository.' }
if (git status --porcelain --untracked-files=normal) { throw 'Commit and review project changes before publishing.' }
$taskHead = (git rev-parse HEAD).Trim()
$taskTag = 'v' + $taskVersion
if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -ne $taskTag) { throw 'Tag and project version differ.' }
$taskOutput = Join-Path $taskRoot 'artifacts'
$taskNames = @("DesktopDock-$taskVersion-Setup-x64.exe", "DesktopDock-$taskVersion-win-x64.zip", 'SHA256SUMS.txt')
$taskAllNames = $taskNames + 'UserGuide.txt'
$taskNotes = Join-Path $taskRoot "docs/releases/$taskVersion.md"
if (-not (Test-Path -LiteralPath $taskNotes)) { throw 'Reviewed release notes are missing.' }
$taskGh = Join-Path $taskRoot '.tools/gh/bin/gh.exe'
if (-not (Test-Path -LiteralPath $taskGh)) { $taskGh = (Get-Command gh -ErrorAction Stop).Source }

function Invoke-ReleaseCli {
    param([string[]]$Arguments)
    $taskResult = & $taskGh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub release operation failed: $($Arguments[0])." }
    $taskResult
}

function Get-ReleaseRecord {
    # Listing also includes authenticated drafts, unlike the tag endpoint during draft creation.
    $taskPages = (Invoke-ReleaseCli @('api', 'repos/lpd2005/desktop-dock/releases?per_page=100', '--paginate', '--slurp')) -join "`n" | ConvertFrom-Json
    $taskRecords = @(foreach ($taskPage in $taskPages) { foreach ($taskRecord in $taskPage) { if ($taskRecord.tag_name -ceq $taskTag) { $taskRecord } } })
    if ($taskRecords.Count -gt 1) { throw 'Duplicate release records.' }
    if ($taskRecords.Count -eq 1) { $taskRecords[0] }
}

function Assert-ReleaseAssets {
    param($Release)
    if (@($Release.assets).Count -ne $taskAllNames.Count) { throw 'Unexpected release assets.' }
    foreach ($taskName in $taskAllNames) {
        $taskAsset = @($Release.assets | Where-Object { $_.name -ceq $taskName })
        $taskUrl = "https://github.com/lpd2005/desktop-dock/releases/download/$taskTag/$taskName"
        if ($taskAsset.Count -ne 1 -or $taskAsset[0].state -cne 'uploaded' -or $taskAsset[0].size -le 0 -or
            $taskAsset[0].digest -cnotmatch '^sha256:[a-f0-9]{64}$' -or $taskAsset[0].browser_download_url -cne $taskUrl) {
            throw "Invalid release asset: $taskName"
        }
    }
}

function Read-ReleaseChecksums {
    param([string]$Path)
    $taskChecksums = @{}
    foreach ($taskLine in Get-Content -LiteralPath $Path) {
        if ($taskLine -cnotmatch '^([a-f0-9]{64})  (DesktopDock-[A-Za-z0-9.-]+\.(exe|zip))$') { throw 'Invalid checksums file.' }
        $taskHash = $Matches[1]; $taskName = $Matches[2]
        if ($taskName -cnotin $taskNames[0..1] -or $taskChecksums.ContainsKey($taskName)) { throw 'Unexpected or duplicate checksum entry.' }
        $taskChecksums[$taskName] = $taskHash
    }
    if ($taskChecksums.Count -ne 2) { throw 'Incomplete checksums file.' }
    $taskChecksums
}

function Assert-PublishedChecksums {
    param($Release)
    $taskDirectory = Join-Path ([IO.Path]::GetTempPath()) ('DesktopDock-release-' + [guid]::NewGuid().ToString('N'))
    $taskChecksumPath = Join-Path $taskDirectory 'SHA256SUMS.txt'
    [IO.Directory]::CreateDirectory($taskDirectory) | Out-Null
    try {
        Invoke-ReleaseCli @('release', 'download', $taskTag, '--repo', 'lpd2005/desktop-dock', '--pattern', 'SHA256SUMS.txt', '--dir', $taskDirectory) | Out-Host
        $taskAsset = $Release.assets | Where-Object name -CEQ 'SHA256SUMS.txt'
        if ((Get-Item -LiteralPath $taskChecksumPath).Length -ne $taskAsset.size -or
            'sha256:' + (Get-FileHash -LiteralPath $taskChecksumPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskAsset.digest) {
            throw 'Published checksums file failed verification.'
        }
        $taskChecksums = Read-ReleaseChecksums $taskChecksumPath
        foreach ($taskName in $taskNames[0..1]) {
            $taskAsset = $Release.assets | Where-Object name -CEQ $taskName
            if ($taskAsset.digest -cne ('sha256:' + $taskChecksums[$taskName])) { throw 'Published asset checksum mismatch.' }
        }
    } finally {
        if (Test-Path -LiteralPath $taskChecksumPath) { [IO.File]::Delete($taskChecksumPath) }
        [IO.Directory]::Delete($taskDirectory)
    }
}

function Write-ReleaseBuildRequired {
    param([bool]$Required)
    if ($CheckOnly -and $env:GITHUB_OUTPUT) { Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value ('build_required=' + $Required.ToString().ToLowerInvariant()) }
}

$taskTemporaryToken = $false
try {
    if (-not $env:GH_TOKEN -and -not $env:GITHUB_TOKEN) {
        $env:GIT_TERMINAL_PROMPT = '0'; $env:GCM_INTERACTIVE = 'Never'
        $taskCredential = "protocol=https`nhost=github.com`npath=lpd2005/desktop-dock.git`n`n" | git -c credential.interactive=never credential fill 2>$null
        $taskPassword = @($taskCredential | Where-Object { $_.StartsWith('password=') })
        if ($taskPassword.Count -ne 1) { throw 'GitHub authentication is required.' }
        $env:GH_TOKEN = $taskPassword[0].Substring(9); $taskTemporaryToken = $true
        $taskCredential = $null; $taskPassword = $null
    }
    $taskRelease = Get-ReleaseRecord
    $taskRefs = @((Invoke-ReleaseCli @('api', "repos/lpd2005/desktop-dock/git/matching-refs/tags/$taskTag")) -join "`n" | ConvertFrom-Json | Where-Object ref -CEQ "refs/tags/$taskTag")
    $taskRemoteTag = $null
    if ($taskRefs.Count -eq 1) { $taskRemoteTag = (Invoke-ReleaseCli @('api', "repos/lpd2005/desktop-dock/commits/$taskTag", '--jq', '.sha')) -join '' }
    if ($VerifyPublished) {
        if (-not $taskRelease -or $taskRelease.draft -or -not $taskRemoteTag) { throw 'A published release with its original tag is required for verification.' }
    } elseif ($taskRemoteTag -and $taskRemoteTag -ne $taskHead) {
        throw 'This version belongs to another commit. Use a new version for new source, or VerifyPublished for a read-only check.'
    }
    if ($taskRelease) {
        $taskExpectedCommit = if ($VerifyPublished) { $taskRemoteTag } else { $taskHead }
        if ($taskRelease.target_commitish -ne $taskExpectedCommit) { throw 'Refusing to replace a release from another commit.' }
        if (-not $taskRelease.draft) {
            if (-not $taskRemoteTag) { throw 'The published release tag is missing.' }
            Assert-ReleaseAssets $taskRelease
            Assert-PublishedChecksums $taskRelease
            Write-ReleaseBuildRequired $false
            Write-Host "Already published and verified: $taskTag. No assets were replaced."
            Write-Host $taskRelease.html_url
            return
        }
    }
    Write-ReleaseBuildRequired $true
    if ($CheckOnly) { return }
    foreach ($taskName in $taskNames) { if (-not (Test-Path -LiteralPath (Join-Path $taskOutput $taskName))) { throw "Missing release asset: $taskName" } }
    $taskChecksums = Read-ReleaseChecksums (Join-Path $taskOutput 'SHA256SUMS.txt')
    foreach ($taskName in $taskNames[0..1]) {
        if ((Get-FileHash -LiteralPath (Join-Path $taskOutput $taskName) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskChecksums[$taskName]) { throw 'Release asset checksum mismatch.' }
    }
    if (-not $taskRelease) {
        $taskArgs = @('release','create',$taskTag,'--repo','lpd2005/desktop-dock','--target',$taskHead,'--title',"Desktop Dock $taskVersion",'--notes-file',$taskNotes,'--draft')
        if ($taskVersion.Contains('-')) { $taskArgs += '--prerelease' }
        Invoke-ReleaseCli $taskArgs | Out-Host
    }
    Copy-Item -LiteralPath docs/USER_GUIDE.md -Destination (Join-Path $taskOutput 'UserGuide.txt')
    $taskAssets = $taskNames | ForEach-Object { Join-Path $taskOutput $_ }
    $taskAssets += Join-Path $taskOutput 'UserGuide.txt'
    # Re-read before --clobber so a concurrently published draft is never intentionally overwritten.
    $taskRelease = Get-ReleaseRecord
    if (-not $taskRelease -or -not $taskRelease.draft -or $taskRelease.target_commitish -ne $taskHead) { throw 'The release draft changed before upload.' }
    Invoke-ReleaseCli (@('release', 'upload', $taskTag) + $taskAssets + @('--repo', 'lpd2005/desktop-dock', '--clobber')) | Out-Host
    $taskPublishedAssets = Get-ReleaseRecord
    if (-not $taskPublishedAssets -or -not $taskPublishedAssets.draft -or $taskPublishedAssets.target_commitish -ne $taskHead) { throw 'Release draft metadata changed; publication stopped.' }
    Assert-ReleaseAssets $taskPublishedAssets
    foreach ($taskLocalAsset in $taskAssets) {
        $taskUploaded = $taskPublishedAssets.assets | Where-Object name -CEQ (Split-Path -Leaf $taskLocalAsset)
        if ($taskUploaded.size -ne (Get-Item -LiteralPath $taskLocalAsset).Length -or $taskUploaded.digest -cne ('sha256:' + (Get-FileHash -LiteralPath $taskLocalAsset -Algorithm SHA256).Hash.ToLowerInvariant())) { throw 'Uploaded asset digest mismatch; draft retained.' }
    }
    if ($Publish) {
        Invoke-ReleaseCli @('release', 'edit', $taskTag, '--repo', 'lpd2005/desktop-dock', '--draft=false') | Out-Host
    }
    Invoke-ReleaseCli @('release', 'view', $taskTag, '--repo', 'lpd2005/desktop-dock', '--json', 'url,isDraft,isPrerelease', '--jq', '{url,isDraft,isPrerelease}')
} finally { if ($taskTemporaryToken) { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue } }
