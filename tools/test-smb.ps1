# Windows fixture host only. Creating a disposable local SMB share requires administrative rights.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Push-Location (Join-Path $PSScriptRoot '..')
$name = 'MixampFixture-' + [guid]::NewGuid().ToString('N')
$root = Join-Path ([IO.Path]::GetTempPath()) $name
$server = $null
$created = $false
try {
    dotnet build tests/Checks.csproj -c Release
    New-Item -ItemType Directory -Path $root | Out-Null
    $account = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    New-SmbShare -Name $name -Path $root -FullAccess $account | Out-Null
    $created = $true
    $server = Start-Process python -PassThru -NoNewWindow -ArgumentList "tools/mock_plex.py --port 32493 --count 4 --flac-fixture --fixture-parent `"$root`""
    $ready = $false
    for ($attempt=0;$attempt -lt 50;$attempt++) {
        if ($server.HasExited) { throw 'Fixture server exited before becoming ready.' }
        try {
            $meta = Invoke-RestMethod 'http://127.0.0.1:32493/library/metadata/1' -Headers @{'X-Plex-Token'='fixture-only'} -TimeoutSec 1
            $ready = $meta.MediaContainer.Metadata[0].Media[0].audioCodec -eq 'flac'
        } catch {}
        if ($ready) { break }
        Start-Sleep -Milliseconds 200
    }
    if (!$ready) { throw 'Disposable media unavailable.' }
    dotnet tests/bin/Release/net10.0/Checks.dll --smb-fixture "\\localhost\$name"
} finally {
    if ($server -and !$server.HasExited) { Stop-Process -Id $server.Id }
    if ($created) { Remove-SmbShare -Name $name -Force }
    if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    Pop-Location
}
