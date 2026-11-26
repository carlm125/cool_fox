[CmdletBinding()]
param(
    [int]$ProcessId,
    [string]$OutputRoot = (Join-Path ([IO.Path]::GetTempPath()) 'VIctory-monitor'),
    [switch]$NoPanel,
    [Parameter(DontShow)][string]$PanelDirectory
)

$ErrorActionPreference = 'Stop'
if (!('VictoryWindow' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'PerformancePanel.cs') -ReferencedAssemblies @(
        'System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll')
}
if ($PanelDirectory) { exit [GamePanel]::Run($ProcessId, $PanelDirectory) }

if ($ProcessId) {
    $game = Get-Process -Id $ProcessId
} else {
    $games = @(Get-Process -Name 'victory','victory-3d-rtx-pareto-residency' -ErrorAction SilentlyContinue |
        Where-Object { [VictoryWindow]::Find($_.Id) -ne [IntPtr]::Zero })
    if ($games.Count -ne 1) { throw 'Supply -ProcessId with the Windows PID of the running title.' }
    $game = $games[0]
}
# Keep a handle to this process even if Windows later recycles its PID.
$null = $game.Handle
$directory = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) (
    '{0}-{1}-{2}' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'), $game.Id, [guid]::NewGuid().ToString('N').Substring(0,8))
$null = [IO.Directory]::CreateDirectory($directory)
Write-Host "Recording PID $($game.Id) to $directory"

$hardwarePath = Join-Path $directory 'hardware.json'
$active = Join-Path $directory 'active'
$enc = New-Object Text.UTF8Encoding($false)
function Write-Hardware {
    $tmp = Join-Path $directory 'hardware.tmp'
    [IO.File]::WriteAllText($tmp, ($hardware | ConvertTo-Json -Depth 4), $enc)
    if ([IO.File]::Exists($hardwarePath)) { [IO.File]::Replace($tmp, $hardwarePath, [System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($tmp, $hardwarePath) }
}
function To-MiB($bytes) {
    if ($null -ne $bytes) { [math]::Round($bytes / 1MB, 1) }
}
function Csv-Row($values) {
    ($values | ForEach-Object {
        if ($null -eq $_) { '' }
        elseif ($_ -is [IFormattable]) { $_.ToString($null, [cultureinfo]::InvariantCulture) }
        else { [string]$_ }
    }) -join ','
}

$cpus = @(Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue)
$logical = ($cpus | Measure-Object -Property NumberOfLogicalProcessors -Sum).Sum
if (!$logical) { $logical = [Environment]::ProcessorCount }
$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
$visibleMiB = if ($os) { [math]::Round($os.TotalVisibleMemorySize / 1024) }
$names = @{}
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\DirectX\*' -ErrorAction SilentlyContinue |
    Where-Object { $null -ne $_.AdapterLuid -and $_.Description } | ForEach-Object {
        $id = [long]$_.AdapterLuid
        $names[('0x{0:x8}_0x{1:x8}' -f (($id -shr 32) -band 4294967295L), ($id -band 4294967295L))] = $_.Description
    }
$hardware = [ordered]@{
    startedUtc = [DateTime]::UtcNow.ToString('o')
    processId = $game.Id
    cpu = @($cpus | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
    osVisibleMemoryMiB = $visibleMiB
    runtimeAdapter = $null
    runtimeAdapterLuid = $null
    cpuPercentBasis = '100% = all logical processors'
    gpuPercentBasis = 'busiest 3D engine on the selected adapter for this PID'
    frameSource = 'most recent presentation statistics in the window title, polled at each sample'
}
Write-Hardware
[IO.File]::WriteAllText($active, '', $enc)
$samples = $null
$frames = $null
$panel = $null
try {
    $samples = [IO.StreamWriter]::new((Join-Path $directory 'samples.csv'), $false, $enc)
    $frames = [IO.StreamWriter]::new((Join-Path $directory 'frames.csv'), $false, $enc)
    $samples.AutoFlush = $true
    $frames.AutoFlush = $true
    $samples.WriteLine('utc,pid,game_cpu_pct,game_working_set_mib,game_private_commit_mib,gpu_3d_pct,gpu_dedicated_mib,gpu_shared_mib,system_ram_used_mib')
    $frames.WriteLine('utc,fps,ms_per_frame,draws_per_frame')
    if (!$NoPanel) {
        $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $panelArgs = @('-NoProfile','-STA','-WindowStyle','Hidden','-File',('"' + $PSCommandPath + '"'),
            '-ProcessId',$game.Id,'-PanelDirectory',('"' + $directory + '"'))
        $panel = Start-Process $powershell -ArgumentList $panelArgs -PassThru -WindowStyle Hidden
    }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $lastCpu = $null
    $lastTime = 0
    $filter = "Name LIKE 'pid_$($game.Id)_%'"
    $prefix = "pid_$($game.Id)_"
    while (!$game.HasExited) {
        $game.Refresh()
        if ($game.HasExited) { break }
        $utc = [DateTime]::UtcNow.ToString('o')
        $time = $clock.Elapsed.TotalSeconds
        $cpu = $game.TotalProcessorTime.TotalSeconds
        $usage = if ($null -ne $lastCpu -and $time -gt $lastTime) {
            [math]::Round(100 * ($cpu - $lastCpu) / (($time - $lastTime) * $logical), 2)
        }
        $lastCpu = $cpu
        $lastTime = $time
        $working = To-MiB $game.WorkingSet64
        $privateMiB = To-MiB $game.PrivateMemorySize64
        $caption = [VictoryWindow]::Caption([VictoryWindow]::Find($game.Id))
        if ($caption -match '\bVIctory\s+([0-9.]+) fps\s+([0-9.]+) ms/frame\s+([0-9.]+) draws/frame') {
            $frames.WriteLine((@($utc,$matches[1],$matches[2],$matches[3]) -join ','))
        }
        $engines = @(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine -Filter $filter -ErrorAction SilentlyContinue |
            Where-Object { $_.Name.StartsWith($prefix) -and $_.Name -like '*_engtype_3D' })
        $busy = $engines | Where-Object { $_.UtilizationPercentage -gt 0 } |
            Sort-Object UtilizationPercentage -Descending | Select-Object -First 1
        if ($busy -and $busy.Name -match '_luid_(0x[0-9a-f]+_0x[0-9a-f]+)_phys_') {
            $id = $matches[1]
            if ($hardware.runtimeAdapterLuid -ne $id) {
                $hardware.runtimeAdapterLuid = $id
                $hardware.runtimeAdapter = if ($names.ContainsKey($id)) { $names[$id] } else { $id }
                Write-Hardware
            }
        }
        $gpu = $null
        $dedicated = $null
        $shared = $null
        if ($hardware.runtimeAdapterLuid) {
            $adapter = '*_luid_' + $hardware.runtimeAdapterLuid + '_phys_*'
            $gpu = ($engines | Where-Object Name -like $adapter | Measure-Object UtilizationPercentage -Maximum).Maximum
            $mem = @(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory -Filter $filter -ErrorAction SilentlyContinue |
                Where-Object { $_.Name.StartsWith($prefix) -and $_.Name -like $adapter })
            if ($mem.Count) {
                $dedicated = To-MiB (($mem | Measure-Object DedicatedUsage -Sum).Sum)
                $shared = To-MiB (($mem | Measure-Object SharedUsage -Sum).Sum)
            }
        }
        $free = (Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory -ErrorAction SilentlyContinue).AvailableMBytes
        $used = if ($visibleMiB -and $null -ne $free) { [math]::Round($visibleMiB - $free) }
        $samples.WriteLine((Csv-Row @($utc,$game.Id,$usage,$working,$privateMiB,$gpu,$dedicated,$shared,$used)))
        if ($panel -and $panel.HasExited) {
            if ($panel.ExitCode -ne 0) { Write-Warning "Sidebar exited with code $($panel.ExitCode); CSV recording continues." }
            $panel = $null
        }
        if ($game.WaitForExit(1500)) { break }
    }
} catch {
    $cause = $_.Exception.GetBaseException()
    if (!$game.HasExited -or ($cause -isnot [InvalidOperationException] -and $cause -isnot [ComponentModel.Win32Exception])) { throw }
} finally {
    if ($samples) { $samples.Dispose() }
    if ($frames) { $frames.Dispose() }
    [IO.File]::Delete($active)
    $hardware.endedUtc = [DateTime]::UtcNow.ToString('o')
    if ($game.HasExited) { $hardware.exitCode = $game.ExitCode }
    Write-Hardware
    $game.Dispose()
    if ($panel) { $panel.Dispose() }
}
