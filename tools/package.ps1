param([switch]$Benchmark,[switch]$SkipWindowSmoke)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Push-Location (Join-Path $PSScriptRoot '..')
$server = $null
$pendingZip = $null
try {
    dotnet restore CrateDeck.slnx --locked-mode
    dotnet build CrateDeck.slnx -c Release --no-restore
    $out = Join-Path (Get-Location) 'artifacts'
    New-Item -Force -ItemType Directory $out | Out-Null
    $python = (Get-Command python -ErrorAction SilentlyContinue)
    if (!$python) { $python = Get-Command python3 }
    $server = Start-Process $python.Source -ArgumentList 'tools/mock_plex.py','--count','513','--lose-ack' -PassThru -NoNewWindow
    $ready = $false
    for ($i=0;$i -lt 50;$i++) {
        if ($server.HasExited) { throw 'Could not start isolated fixture on loopback port 32491.' }
        try {
            $reply = Invoke-RestMethod 'http://127.0.0.1:32491/identity' -Headers @{'X-Plex-Token'='fixture-only'} -TimeoutSec 1
            $ready = $reply.MediaContainer.machineIdentifier -eq 'cratedeck-demo'
        } catch {}
        if ($ready) { break }
        Start-Sleep -Milliseconds 200
    }
    if (!$ready -or $server.HasExited) { throw 'Demo did not become ready.' }
    $arguments = @('--mock')
    if ($Benchmark) { $arguments += @('--benchmark', (Join-Path $out 'store-benchmark.json')) }
    dotnet tests/bin/Release/net10.0/Checks.dll @arguments
    & $python.Source tools/scenarios.py
    $folder = Join-Path $out 'mixamp-win-x64'
    $payload = Join-Path $folder 'app'
    $venv = Join-Path $out 'launcher-toolchain'
    & $python.Source -m venv $venv
    $compiler = if ($IsWindows) { Join-Path $venv 'Scripts/python.exe' } else { Join-Path $venv 'bin/python' }
    & $compiler -m pip install --disable-pip-version-check ziglang==0.13.0
    New-Item -Force -ItemType Directory $folder | Out-Null
    Push-Location launcher
    try {
        & $compiler -m ziglang cc -target x86_64-windows-gnu -Os -s -municode "-Wl,--subsystem,windows" main.c icon.rc -o (Join-Path $folder 'CrateDeck.exe')
    } finally { Pop-Location }
    dotnet publish src/CrateDeck -c Release -r win-x64 --self-contained true --no-restore -o $payload
    Copy-Item README.md,THIRD-PARTY-NOTICES.md,notices,docs -Destination $folder -Recurse -Force
    Copy-Item tools/mock_plex.py -Destination $folder
    foreach ($name in 'CrateDeck.exe','coreclr.dll','hostfxr.dll','PresentationFramework.dll','e_sqlite3.dll','TagLibSharp.dll','Microsoft.Windows.SDK.NET.dll','WinRT.Runtime.dll','wpfgfx_cor3.dll') {
        if (!(Test-Path (Join-Path $payload $name))) { throw "Package is missing $name" }
    }
    if ($IsWindows -and !$SkipWindowSmoke) {
        $shots = Join-Path $out 'screenshots'
        $app = Start-Process (Join-Path $folder 'CrateDeck.exe') -ArgumentList "--smoke-demo `"$shots`"" -PassThru
        if (!$app.WaitForExit(90000)) { $app.Kill(); throw 'Packaged WPF smoke test timed out.' }
        if ($app.ExitCode -ne 0 -or @(Get-ChildItem "$shots/*.png").Count -ne 5) { throw 'Packaged WPF smoke test failed.' }
    }
    $zip = Join-Path $out 'CrateDeck-win-x64.zip'
    $pendingZip = "$zip.$([guid]::NewGuid()).tmp"
    [System.IO.Compression.ZipFile]::CreateFromDirectory($folder, $pendingZip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    Move-Item $pendingZip $zip -Force
    Get-FileHash $zip
} finally {
    if ($server -and !$server.HasExited) { Stop-Process -Id $server.Id }
    if ($pendingZip -and (Test-Path -LiteralPath $pendingZip)) { Remove-Item -LiteralPath $pendingZip }
    Pop-Location
}
