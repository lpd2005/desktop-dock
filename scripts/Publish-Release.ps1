param([switch]$Publish)
$ErrorActionPreference = 'Stop'
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
foreach ($taskName in $taskNames) { if (-not (Test-Path -LiteralPath (Join-Path $taskOutput $taskName))) { throw "Missing release asset: $taskName" } }
foreach ($taskLine in Get-Content -LiteralPath (Join-Path $taskOutput 'SHA256SUMS.txt')) {
    if ($taskLine -notmatch '^([a-f0-9]{64})  (DesktopDock-[A-Za-z0-9.-]+\.(exe|zip))$') { throw 'Invalid checksums file.' }
    if ((Get-FileHash -LiteralPath (Join-Path $taskOutput $Matches[2]) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Matches[1]) { throw 'Release asset checksum mismatch.' }
}
$taskNotes = Join-Path $taskRoot "docs/releases/$taskVersion.md"
if (-not (Test-Path -LiteralPath $taskNotes)) { throw 'Reviewed release notes are missing.' }
$taskGh = Join-Path $taskRoot '.tools/gh/bin/gh.exe'
if (-not (Test-Path -LiteralPath $taskGh)) { $taskGh = (Get-Command gh -ErrorAction Stop).Source }
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
    $taskRemoteTag = & $taskGh api "repos/lpd2005/desktop-dock/commits/$taskTag" --jq '.sha' 2>$null
    if ($LASTEXITCODE -eq 0 -and $taskRemoteTag -ne $taskHead) { throw 'An existing tag points at a different commit.' }
    $taskExisting = & $taskGh release view $taskTag --repo lpd2005/desktop-dock --json isDraft,targetCommitish 2>$null
    if ($LASTEXITCODE -eq 0) {
        $taskRelease = $taskExisting | ConvertFrom-Json
        if (-not $taskRelease.isDraft -or $taskRelease.targetCommitish -ne $taskHead) { throw 'Refusing to replace an already published release or another commit.' }
    } else {
        $taskArgs = @('release','create',$taskTag,'--repo','lpd2005/desktop-dock','--target',$taskHead,'--title',"Desktop Dock $taskVersion",'--notes-file',$taskNotes,'--draft')
        if ($taskVersion.Contains('-')) { $taskArgs += '--prerelease' }
        & $taskGh @taskArgs
        if ($LASTEXITCODE -ne 0) { throw 'Release draft creation failed.' }
    }
    Copy-Item -LiteralPath docs/USER_GUIDE.md -Destination (Join-Path $taskOutput '使用说明.txt')
    $taskAssets = $taskNames | ForEach-Object { Join-Path $taskOutput $_ }
    $taskAssets += Join-Path $taskOutput '使用说明.txt'
    & $taskGh release upload $taskTag @taskAssets --repo lpd2005/desktop-dock --clobber
    if ($LASTEXITCODE -ne 0) { throw 'Release asset upload failed; draft retained.' }
    $taskPublishedAssets = @(((& $taskGh api 'repos/lpd2005/desktop-dock/releases?per_page=100') | ConvertFrom-Json) | Where-Object tag_name -eq $taskTag)
    if ($taskPublishedAssets.Count -ne 1) { throw 'Release draft metadata is unavailable; draft retained.' }
    $taskSetup = @($taskPublishedAssets[0].assets | Where-Object name -eq $taskNames[0])
    if ($taskSetup.Count -ne 1 -or $taskSetup[0].digest -ne ('sha256:' + (Get-FileHash -LiteralPath $taskAssets[0] -Algorithm SHA256).Hash.ToLowerInvariant())) { throw 'Uploaded installer digest mismatch; draft retained.' }
    if ($Publish) {
        & $taskGh release edit $taskTag --repo lpd2005/desktop-dock --draft=false
        if ($LASTEXITCODE -ne 0) { throw 'Release publication failed; draft retained.' }
    }
    & $taskGh release view $taskTag --repo lpd2005/desktop-dock --json url,isDraft,isPrerelease --jq '{url,isDraft,isPrerelease}'
} finally { if ($taskTemporaryToken) { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue } }
