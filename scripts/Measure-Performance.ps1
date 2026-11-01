#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Executable,
    [ValidateRange(1,40)][int[]]$IconCounts = @(10,20,40),
    [ValidateSet('base','widgets','previews','glass')][string[]]$Profiles = @('base'),
    [ValidateRange(2,7200)][int]$Seconds = 600,
    [ValidateRange(0,300)][int]$WarmupSeconds = 30,
    [ValidatePattern('^[A-Za-z0-9_-]{1,40}$')][string]$Label = 'sample',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$taskExe = (Resolve-Path -LiteralPath $Executable).Path
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot ('artifacts/performance-' + [guid]::NewGuid().ToString('N')) }
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $taskOutput) -and @(Get-ChildItem -LiteralPath $taskOutput -Force).Count) { throw 'Choose a new empty output directory.' }
if (@(Get-Process -Name DesktopDock -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq (Get-Process -Id $PID).SessionId }).Count) {
    throw 'Exit the current Dock normally before starting isolated performance runs.'
}
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskEnvironment = [ordered]@{
    CpuModels = @(Get-CimInstance Win32_Processor | Select-Object -ExpandProperty Name)
    Graphics = @(Get-CimInstance Win32_VideoController | Select-Object Name,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate)
    AssemblySHA256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $taskExe) 'DesktopDock.dll') -Algorithm SHA256).Hash
    Notes = 'Driver-reported display information. No computer name, user paths, application list or window titles. DWM counters include unrelated desktop activity.'
}
$taskEnvironment | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskOutput 'environment.json') -Encoding utf8
$taskResults = @()
foreach ($taskProfile in $Profiles) {
    foreach ($taskCount in $IconCounts) {
        $taskData = Join-Path $taskOutput ($Label + '-' + $taskProfile + '-' + $taskCount)
        $taskInfo = [Diagnostics.ProcessStartInfo]::new($taskExe)
        $taskInfo.UseShellExecute = $false; $taskInfo.CreateNoWindow = $true; $taskInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $taskInfo.WorkingDirectory = Split-Path -Parent $taskExe
        foreach ($taskArgument in @('--performance-run','--data-dir',$taskData,'--perf-count',"$taskCount",'--perf-profile',$taskProfile,'--perf-seconds',"$Seconds",'--perf-warmup',"$WarmupSeconds",'--perf-label',$Label)) { $taskInfo.ArgumentList.Add($taskArgument) }
        $taskProcess = [Diagnostics.Process]::Start($taskInfo)
        $taskWorkers = @(); $taskWorkerSamples = @(); $taskWorkerLast = @{}
        try {
            $taskDeadline = [DateTime]::UtcNow.AddSeconds($WarmupSeconds + 60)
            while (-not (Test-Path -LiteralPath (Join-Path $taskData 'performance-ready.json'))) {
                if ($taskProcess.WaitForExit(200)) { throw 'Performance process exited before sampling; inspect its local last-error.txt.' }
                if ([DateTime]::UtcNow -gt $taskDeadline) { throw 'Performance process did not finish warming up.' }
            }
            $taskSession = (Get-Process -Id $PID).SessionId
            foreach ($taskDwm in @(Get-Process -Name dwm -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $taskSession })) { $taskWorkers += @{ Role='DWM'; Process=$taskDwm } }
            foreach ($taskChild in @(Get-CimInstance Win32_Process -Filter ('ParentProcessId = ' + $taskProcess.Id))) {
                $taskRole = if ($taskChild.Name -like 'TranslucentTB*') { 'Glass' } elseif ($taskChild.CommandLine -like '*--taskbar-guard*') { 'Guard' } else { $null }
                if ($taskRole) { $taskWorkers += @{ Role=$taskRole; Process=[Diagnostics.Process]::GetProcessById([int]$taskChild.ProcessId) } }
            }
            $taskWatch = [Diagnostics.Stopwatch]::StartNew()
            $taskUnavailable = @()
            foreach ($taskWorker in $taskWorkers) {
                try { $taskWorker.Process.Refresh(); $taskWorkerLast[$taskWorker.Process.Id] = @{ Cpu=$taskWorker.Process.TotalProcessorTime; Seconds=$taskWatch.Elapsed.TotalSeconds } }
                catch { $taskUnavailable += $taskWorker.Role }
            }
            $taskDeadline = [DateTime]::UtcNow.AddSeconds($Seconds + 120)
            while (-not $taskProcess.WaitForExit(5000)) {
                foreach ($taskWorker in $taskWorkers) {
                    if ($taskWorker.Role -in $taskUnavailable -or $taskWorker.Process.HasExited -or $taskWatch.Elapsed.TotalSeconds -gt $Seconds) { continue }
                    try {
                        $taskWorker.Process.Refresh(); $taskNow=$taskWatch.Elapsed.TotalSeconds; $taskLast=$taskWorkerLast[$taskWorker.Process.Id]
                        $taskCpu=$taskWorker.Process.TotalProcessorTime
                        $taskWorkerSamples += [pscustomobject]@{ Role=$taskWorker.Role; Seconds=$taskNow; CpuPercent=($taskCpu-$taskLast.Cpu).TotalSeconds/($taskNow-$taskLast.Seconds)/[Environment]::ProcessorCount*100; WorkingSetMiB=$taskWorker.Process.WorkingSet64/1MB; PrivateMiB=$taskWorker.Process.PrivateMemorySize64/1MB }
                        $taskWorkerLast[$taskWorker.Process.Id]=@{Cpu=$taskCpu;Seconds=$taskNow}
                    } catch { $taskUnavailable += $taskWorker.Role }
                }
                if ([DateTime]::UtcNow -gt $taskDeadline) { throw 'Performance run exceeded its time limit; inspect the running process.' }
            }
            $taskProcess.Refresh()
            if ($taskProcess.ExitCode -ne 0) { throw 'Performance run failed; inspect its local last-error.txt.' }
            $taskReport = Get-Content -LiteralPath (Join-Path $taskData 'performance-report.json') -Raw | ConvertFrom-Json
            @{ Samples=@($taskWorkerSamples); UnavailableRoles=@($taskUnavailable) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskData 'worker-samples.json') -Encoding utf8
            $taskResults += $taskReport | Select-Object Version,Label,Profile,PinnedIcons,SampleSeconds,ShortSample,CpuMeanPercent,WorkingSetMeanMiB,PrivateMeanMiB,AllocatedBytesPerSecond,PrivateGrowthMiB,IdleAnimationUpdates,ExercisePrivateDeltaMiB
            $taskResults[-1] | Format-List
        } finally {
            if (-not $taskProcess.HasExited) {
                $taskQuitInfo=[Diagnostics.ProcessStartInfo]::new($taskExe); $taskQuitInfo.UseShellExecute=$false; $taskQuitInfo.CreateNoWindow=$true
                $taskQuitInfo.ArgumentList.Add('--quit'); $taskQuitInfo.ArgumentList.Add('--data-dir'); $taskQuitInfo.ArgumentList.Add($taskData)
                $taskQuit=[Diagnostics.Process]::Start($taskQuitInfo)
                try { $null=$taskQuit.WaitForExit(10000); $null=$taskProcess.WaitForExit(10000) } finally { $taskQuit.Dispose() }
            }
            foreach ($taskWorker in $taskWorkers) { $taskWorker.Process.Dispose() }
            $taskProcess.Dispose()
        }
    }
}
ConvertTo-Json -InputObject @($taskResults) -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOutput 'summary.json') -Encoding utf8
Write-Output ('Saved local performance results to ' + $taskOutput)
