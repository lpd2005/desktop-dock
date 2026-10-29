$ErrorActionPreference = 'Stop'
$taskSource = Join-Path $PSScriptRoot 'Publish-Release.ps1'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
$taskFixture = Join-Path $taskWorkspace ('.tools/release-checks-' + [guid]::NewGuid().ToString('N'))
$taskPwsh = (Get-Process -Id $PID).Path
$taskOldLocation = Get-Location
$taskEnvironment = @{}
foreach ($taskName in @('PATH', 'GH_TOKEN', 'GITHUB_TOKEN', 'GITHUB_REF_TYPE', 'GITHUB_REF_NAME', 'GITHUB_OUTPUT', 'DOCK_RELEASE_FIXTURE')) {
    $taskEnvironment[$taskName] = [Environment]::GetEnvironmentVariable($taskName, 'Process')
}
$taskCount = 0
try {
    foreach ($taskDirectory in @('scripts', 'docs/releases', 'artifacts', '.tools/mock')) {
        New-Item -ItemType Directory -Path (Join-Path $taskFixture $taskDirectory) -Force | Out-Null
    }
    Copy-Item -LiteralPath $taskSource -Destination (Join-Path $taskFixture 'scripts/Publish-Release.ps1')
    Set-Content -LiteralPath (Join-Path $taskFixture 'Directory.Build.props') -Value '<Project><PropertyGroup><Version>1.2.3-preview.1</Version></PropertyGroup></Project>'
    Set-Content -LiteralPath (Join-Path $taskFixture '.gitignore') -Value @('.tools/', 'artifacts/')
    Set-Content -LiteralPath (Join-Path $taskFixture 'docs/USER_GUIDE.md') -Value 'Synthetic guide.'
    Set-Content -LiteralPath (Join-Path $taskFixture 'docs/releases/1.2.3-preview.1.md') -Value 'Synthetic release notes.'
    Set-Location -LiteralPath $taskFixture
    git init --quiet
    git config --local user.name lpd2005
    git config --local user.email 216267583+lpd2005@users.noreply.github.com
    git config --local core.autocrlf false
    git remote add origin https://github.com/lpd2005/desktop-dock.git
    git add -- .gitignore Directory.Build.props docs/USER_GUIDE.md docs/releases/1.2.3-preview.1.md scripts/Publish-Release.ps1
    git -c commit.gpgsign=false commit --quiet -m 'Create synthetic release check fixture'
    if ($LASTEXITCODE -ne 0) { throw 'Could not create release check fixture.' }
    $taskHead = (git rev-parse HEAD).Trim()
    $taskTag = 'v1.2.3-preview.1'
    $taskNames = @('DesktopDock-1.2.3-preview.1-Setup-x64.exe', 'DesktopDock-1.2.3-preview.1-win-x64.zip')
    foreach ($taskName in $taskNames) { [IO.File]::WriteAllText((Join-Path $taskFixture "artifacts/$taskName"), "Synthetic asset: $taskName") }
    Copy-Item -LiteralPath docs/USER_GUIDE.md -Destination artifacts/UserGuide.txt
    $taskLines = foreach ($taskName in $taskNames) { (Get-FileHash -LiteralPath (Join-Path artifacts $taskName) -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $taskName }
    [IO.File]::WriteAllLines((Join-Path $taskFixture 'artifacts/SHA256SUMS.txt'), $taskLines, [Text.UTF8Encoding]::new($false))
    $taskAssets = @(foreach ($taskName in ($taskNames + @('SHA256SUMS.txt', 'UserGuide.txt'))) {
        @{ name = $taskName; size = (Get-Item -LiteralPath (Join-Path artifacts $taskName)).Length; state = 'uploaded'
           digest = 'sha256:' + (Get-FileHash -LiteralPath (Join-Path artifacts $taskName) -Algorithm SHA256).Hash.ToLowerInvariant()
           browser_download_url = "https://github.com/lpd2005/desktop-dock/releases/download/$taskTag/$taskName" }
    })
    $taskBase = @{ release = @{ tag_name = $taskTag; draft = $false; prerelease = $true; target_commitish = $taskHead; assets = $taskAssets; html_url = "https://github.com/lpd2005/desktop-dock/releases/tag/$taskTag" }
        tagCommit = $taskHead; manifest = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $taskFixture 'artifacts/SHA256SUMS.txt'))); error = $false; race = $false; reads = 0 }
    $taskStatePath = Join-Path $taskFixture '.tools/state.json'
    $taskCallsPath = Join-Path $taskFixture '.tools/calls.jsonl'
    $taskFake = @'
$ErrorActionPreference = 'Stop'
$taskRoot = $env:DOCK_RELEASE_FIXTURE
$taskStatePath = Join-Path $taskRoot '.tools/state.json'
$taskState = Get-Content -LiteralPath $taskStatePath -Raw | ConvertFrom-Json
Add-Content -LiteralPath (Join-Path $taskRoot '.tools/calls.jsonl') -Value (ConvertTo-Json -InputObject @($args) -Compress)
function Save-State { $taskState | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $taskStatePath }
if ($taskState.error) { Write-Output 'Simulated API failure.'; exit 1 }
if ($args[0] -eq 'api') {
    if ($args[1] -like '*/releases?*') {
        $taskState.reads++; if ($taskState.race -and $taskState.reads -eq 2) { $taskState.release.draft = $false }; Save-State
        $taskRecords = @(); if ($taskState.release) { $taskRecords = @($taskState.release) }
        # An empty first page exercises paginated lookup, including drafts.
        ConvertTo-Json -InputObject @(@(), $taskRecords) -Depth 15 -Compress
    } elseif ($args[1] -like '*/matching-refs/*') {
        $taskRefs = @(); if ($taskState.tagCommit) { $taskRefs = @(@{ ref = 'refs/tags/v1.2.3-preview.1' }) }
        ConvertTo-Json -InputObject $taskRefs -Compress
    } elseif ($args[1] -like '*/commits/*') { $taskState.tagCommit }
    else { throw 'Unexpected API request.' }
} elseif ($args[0] -eq 'release') {
    switch ($args[1]) {
        download {
            $taskDirectory = $args[[Array]::IndexOf($args, '--dir') + 1]
            [IO.File]::WriteAllBytes((Join-Path $taskDirectory 'SHA256SUMS.txt'), [Convert]::FromBase64String($taskState.manifest))
        }
        create {
            $taskState.release = @{ tag_name = 'v1.2.3-preview.1'; draft = $true; target_commitish = (git -C $taskRoot rev-parse HEAD).Trim(); assets = @() }
            Save-State
        }
        upload {
            $taskState.release.assets = @(Get-Content -LiteralPath (Join-Path $taskRoot '.tools/assets.json') -Raw | ConvertFrom-Json)
            Save-State
        }
        edit { $taskState.release.draft = $false; Save-State }
        view { '{"isDraft":false}' }
        default { throw 'Unexpected release mutation.' }
    }
} else { throw 'Unexpected CLI request.' }
'@
    Set-Content -LiteralPath (Join-Path $taskFixture '.tools/mock/fake-gh.ps1') -Value $taskFake
    [IO.File]::WriteAllText((Join-Path $taskFixture '.tools/mock/gh.cmd'), "@echo off`r`n@`"$taskPwsh`" -NoProfile -File `"%~dp0fake-gh.ps1`" %*`r`n")
    $taskAssets | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $taskFixture '.tools/assets.json')
    $env:PATH = (Join-Path $taskFixture '.tools/mock') + ';' + $env:PATH
    $env:GH_TOKEN = 'synthetic-test-value'
    $env:GITHUB_TOKEN = $null; $env:GITHUB_REF_TYPE = $null; $env:GITHUB_REF_NAME = $null
    $env:GITHUB_OUTPUT = Join-Path $taskFixture '.tools/output.txt'
    $env:DOCK_RELEASE_FIXTURE = $taskFixture

    function Test-Publishing {
        param([string]$Name, [scriptblock]$Change = {}, [string[]]$Arguments = @(), [bool]$Success = $true, [string[]]$Mutations = @(), [string]$BuildRequired = '')
        $taskScenario = $taskBase | ConvertTo-Json -Depth 15 | ConvertFrom-Json
        & $Change $taskScenario
        $taskScenario | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $taskStatePath
        [IO.File]::WriteAllText($taskCallsPath, '')
        [IO.File]::WriteAllText($env:GITHUB_OUTPUT, '')
        $taskResult = & $taskPwsh -NoProfile -File (Join-Path $taskFixture 'scripts/Publish-Release.ps1') @Arguments 2>&1
        if (($LASTEXITCODE -eq 0) -ne $Success) { throw "${Name}: unexpected exit. $taskResult" }
        $taskCalls = @(Get-Content -LiteralPath $taskCallsPath | ForEach-Object { ,(ConvertFrom-Json -InputObject $_) })
        $taskWrites = @($taskCalls | Where-Object { $_[0] -eq 'release' -and $_[1] -in @('create','upload','edit') } | ForEach-Object { $_[1] })
        if (($taskWrites -join ',') -ne ($Mutations -join ',')) { throw "${Name}: unexpected publication mutation." }
        if ($BuildRequired -and (Get-Content -LiteralPath $env:GITHUB_OUTPUT -Raw).Trim() -ne "build_required=$BuildRequired") { throw "${Name}: wrong build decision." }
        $script:taskCount++
        Write-Host "PASS $Name"
    }
    Test-Publishing 'published tag repeat succeeds without upload' -Arguments @('-Publish')
    Test-Publishing 'published tag skips build before local artifacts are needed' -Arguments @('-CheckOnly') -BuildRequired false
    Test-Publishing 'missing public asset is refused' { param($s) $s.release.assets = @($s.release.assets | Select-Object -Skip 1) } -Success $false
    Test-Publishing 'unexpected public asset is refused' { param($s) $s.release.assets += $s.release.assets[0] } -Success $false
    Test-Publishing 'missing digest is refused' { param($s) $s.release.assets[0].digest = $null } -Success $false
    Test-Publishing 'empty public asset is refused' { param($s) $s.release.assets[0].size = 0 } -Success $false
    Test-Publishing 'incomplete upload is refused' { param($s) $s.release.assets[0].state = 'new' } -Success $false
    Test-Publishing 'external asset URL is refused' { param($s) $s.release.assets[0].browser_download_url = 'https://example.invalid/asset.exe' } -Success $false
    Test-Publishing 'published digest mismatch is refused' { param($s) $s.release.assets[0].digest = 'sha256:' + ('a' * 64) } -Success $false
    Test-Publishing 'modified manifest is refused' { param($s) $s.manifest = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('changed')) } -Success $false
    Test-Publishing 'release from another commit is refused' { param($s) $s.release.target_commitish = 'b' * 40 } -Success $false
    Test-Publishing 'moved tag cannot publish new source' { param($s) $s.tagCommit = 'b' * 40 } -Arguments @('-Publish') -Success $false
    Test-Publishing 'missing tag is refused' { param($s) $s.tagCommit = $null } -Success $false
    Test-Publishing 'API failure cannot create a release' { param($s) $s.error = $true } -Success $false
    Test-Publishing 'read-only check can verify an older source' { param($s) $s.tagCommit = 'b' * 40; $s.release.target_commitish = $s.tagCommit } -Arguments @('-VerifyPublished','-CheckOnly') -BuildRequired false
    Test-Publishing 'read-only check cannot publish' -Arguments @('-VerifyPublished','-Publish') -Success $false
    Test-Publishing 'read-only check refuses a draft' { param($s) $s.release.draft = $true } -Arguments @('-VerifyPublished') -Success $false
    Test-Publishing 'new version preflight requests build' { param($s) $s.release = $null; $s.tagCommit = $null } -Arguments @('-CheckOnly') -BuildRequired true
    Test-Publishing 'draft preflight requests build' { param($s) $s.release.draft = $true } -Arguments @('-CheckOnly') -BuildRequired true
    Test-Publishing 'new version still creates and publishes' { param($s) $s.release = $null; $s.tagCommit = $null } -Arguments @('-Publish') -Mutations @('create','upload','edit')
    Test-Publishing 'draft retry still uploads and publishes' { param($s) $s.release.draft = $true } -Arguments @('-Publish') -Mutations @('upload','edit')
    Test-Publishing 'manual default retains draft' { param($s) $s.release.draft = $true } -Mutations @('upload')
    Test-Publishing 'concurrently published draft blocks upload' { param($s) $s.release.draft = $true; $s.race = $true } -Arguments @('-Publish') -Success $false
    Write-Host "$taskCount release publishing checks passed. No network access or real publication."
    # GitHub's PowerShell runner propagates the last native exit code, including expected failures above.
    $global:LASTEXITCODE = 0
} finally {
    Set-Location -LiteralPath $taskOldLocation
    foreach ($taskName in $taskEnvironment.Keys) { [Environment]::SetEnvironmentVariable($taskName, $taskEnvironment[$taskName], 'Process') }
    $taskResolved = [IO.Path]::GetFullPath($taskFixture)
    $taskBoundary = [IO.Path]::GetFullPath((Join-Path $taskWorkspace '.tools')) + [IO.Path]::DirectorySeparatorChar
    if (-not $taskResolved.StartsWith($taskBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid release check cleanup path.' }
    if (Test-Path -LiteralPath $taskResolved) { Remove-Item -LiteralPath $taskResolved -Recurse -Force }
}
