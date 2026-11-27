param([string]$Exe = 'artifacts/mixamp-win-x64/app/CrateDeck.exe')
$ErrorActionPreference = 'Stop'
# Start the 100k disposable mock with tools/mock_plex.py --count 100000 first.
$folder = (New-Item -ItemType Directory -Path "artifacts/benchmark-$(Get-Date -Format yyyyMMdd-HHmmss)").FullName
Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer,Model,NumberOfLogicalProcessors,TotalPhysicalMemory | ConvertTo-Json > "$folder/hardware.json"
Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber | ConvertTo-Json > "$folder/os.json"
Get-PhysicalDisk | Select-Object FriendlyName,MediaType,BusType | ConvertTo-Json > "$folder/storage.json"
Get-FileHash $Exe | ConvertTo-Json > "$folder/executable.json"
function Run-Phase($phase,$id) {
    $report = Join-Path $folder "$phase-$id.json"
    $proc = Start-Process $Exe -PassThru -ArgumentList "--bench-demo `"$folder`" $phase `"$report`""
    if ($phase -eq 'idle' -or $phase.StartsWith('scroll-')) {
        $samples = @()
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (!$proc.WaitForExit(1000) -and $watch.Elapsed.TotalMinutes -lt 10) {
            if ($phase -eq 'idle' -and $id -eq 'minimized' -and $watch.Elapsed.TotalSeconds -gt 5) {
                [CrateDeckBenchWindow]::ShowWindowAsync($proc.MainWindowHandle,6) | Out-Null
            }
            $perf = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess = $($proc.Id)"
            if ($perf) {
                $samples += [pscustomobject]@{elapsedMs=$watch.Elapsed.TotalMilliseconds;privateWorkingSetBytes=$perf.WorkingSetPrivate;totalMachineCpuPercent=$perf.PercentProcessorTime/[Environment]::ProcessorCount}
            }
        }
        $samples | ConvertTo-Json > (Join-Path $folder "$phase-$id-counters.json")
    }
    if (!$proc.WaitForExit(600000)) { $proc.Kill(); throw "Timeout in $phase" }
    if ($proc.ExitCode -ne 0) { throw "Failed $phase, see $report" }
    Get-Content $report -Raw | ConvertFrom-Json
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CrateDeckBenchWindow {
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr window, int command);
}
'@
$seed = Run-Phase seed 0
$launches = @(foreach ($n in 1..30) { (Run-Phase launch $n).raw[0].launchMs })
$queries = (Run-Phase query 0).raw | Where-Object { $_.PSObject.Properties['visibleMs'] }
$idle = Run-Phase idle 0
$null = Run-Phase idle minimized
[pscustomobject]@{
    initialSyncMs = $seed.raw[0].syncMs
    warmLaunchP95Ms = ($launches | Sort-Object)[28]
    searchP95Ms = ($queries.visibleMs | Sort-Object)[113]
    idlePercentTotalCPU = 100 * $idle.raw[-1].cpuMs / $idle.raw[-1].elapsedMs / $idle.cores
    evidenceDirectory = $folder
}
foreach ($phase in 'scroll-silver','scroll-amp') {
    $record = Run-Phase $phase 0
    $frames = @($record.raw[-1].frames | Sort-Object)
    [pscustomobject]@{layout=$phase;frameP95Ms=$frames[[math]::Ceiling(.95*$frames.Count)-1];maxRealizedRows=$record.raw[-1].maxRows;activeSyncThroughEnd=$record.raw[-1].stillSyncing}
}
