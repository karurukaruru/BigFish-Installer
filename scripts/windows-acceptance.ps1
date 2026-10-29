param([switch]$Bili, [ValidateSet('cn','global')][string]$Region = 'cn')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$testParent = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$testRoot = Join-Path $testParent ('BigFish 验收 & spaces ' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $testRoot 'data'
$workspace = Join-Path $testRoot 'workspace'
$reports = Join-Path $repo "artifacts/acceptance/$Region"
New-Item -ItemType Directory -Force $reports | Out-Null
Push-Location $repo
try {
    # Force the clean-machine path even though the runner already has Node.
    dotnet publish src/BigFish.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true "-p:BigFishDownloadRegion=$Region" -o "artifacts/cli/$Region"
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed' }
    $env:DOTNET_ROOT = Join-Path $testRoot 'no-runtime-here'
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $app = Join-Path $repo "artifacts/channels/$Region/BigFish.exe"
    $runtime = Get-Content (Join-Path $repo 'src/BigFish.App/bin/Release/net10.0-windows/win-x64/BigFish.runtimeconfig.json') -Raw | ConvertFrom-Json
    if (!$runtime.runtimeOptions.includedFrameworks -or $runtime.runtimeOptions.framework) { throw 'App is not self-contained' }
    $uiReport = Join-Path $reports 'wpf-self-test.json'
    $p = Start-Process $app -ArgumentList @('--self-test', ('"' + $uiReport + '"')) -PassThru -Wait
    if ($p.ExitCode -ne 0 -or !(Test-Path $uiReport)) { throw 'Self-contained WPF launch failed' }
    $ui = Get-Content $uiReport -Raw | ConvertFrom-Json
    if (!$ui.wpfLoaded -or !$ui.viewModelBound -or $ui.pagesVisited -ne 4 -or $ui.bindingErrors.Count -ne 0 -or !$ui.osSupported -or $ui.downloadRegion -ne $(if ($Region -eq 'cn') { 'China' } else { 'Global' })) { throw 'WPF or environment detection failed' }
    $acceptanceArgs = @('smoke', '--region', $Region, '--root', $data, '--workspace', $workspace, '--private-node', '--setup-smoke', '--report', (Join-Path $reports 'windows-engine.json'))
    if ($Bili) { $acceptanceArgs += '--bili' }
    & (Join-Path $repo "artifacts/cli/$Region/BigFish.Cli.exe") @acceptanceArgs
    if ($LASTEXITCODE -ne 0) { throw 'Real Windows install/start/repeat acceptance failed' }
    if (!$Bili -and (Test-Path (Join-Path $data 'dsh-home/bili-duty/.venv'))) { throw 'Base install unexpectedly installed Python' }
    $record = Get-Content (Join-Path $reports 'windows-engine.json') -Raw | ConvertFrom-Json
    if (!$record.idempotent -or !$record.sentinelPreserved -or !$record.imRpc -or !$record.credentialRoundTrip -or !$record.apiKeyPreserved -or $record.interfaceLanguage -ne 'zh') { throw 'Incomplete acceptance evidence' }
} finally {
    # Only these test-specific folders belong to this script. Keep failures for local inspection.
    Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:DOTNET_MULTILEVEL_LOOKUP -ErrorAction SilentlyContinue
    Pop-Location
}
