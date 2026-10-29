param([switch]$Setup, [ValidateSet('cn','global','all')][string]$Region = 'all')
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    $version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
    if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -ne "v$version") { throw 'Tag/version mismatch' }
    dotnet restore BigFish.sln
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    dotnet test tests/BigFish.Tests -c Release --logger 'trx;LogFileName=tests.trx' --results-directory artifacts/test-results
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    $channels = if ($Region -eq 'all') { @('cn','global') } else { @($Region) }
    New-Item -ItemType Directory -Force artifacts/release | Out-Null
    foreach ($channel in $channels) {
        $label = if ($channel -eq 'cn') { 'CN' } else { 'Global' }
        $output = Join-Path $PWD "artifacts/channels/$channel"
        if (Test-Path $output) { Remove-Item $output -Recurse -Force }
        dotnet publish src/BigFish.App -c Release -r win-x64 --self-contained true "-p:BigFishDownloadRegion=$channel" -o $output
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $channel" }
        & (Join-Path $PSScriptRoot 'collect-runtime-notices.ps1')
        Copy-Item LICENSE, THIRD_PARTY_NOTICES.md, README.md $output
        Copy-Item third-party, docs $output -Recurse -Force
        Copy-Item "$output/BigFish.exe" "artifacts/release/BigFish-$label.exe" -Force
        if ($Setup) {
            $iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
            if (!(Test-Path $iscc)) { throw 'Install Inno Setup 6 from https://jrsoftware.org/isinfo.php' }
            & $iscc "/DAppVersion=$version" "/DPublishDir=$output" "/DOutputName=BigFishSetup-$label" installer/BigFish.iss
            if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed: $channel" }
        }
        $zip = "artifacts/release/BigFish-$label-win-x64.zip"
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Compress-Archive "$output/*" $zip -CompressionLevel Optimal
    }
    $lines = Get-ChildItem artifacts/release -File | Where-Object { $_.Extension -in '.exe','.zip' } | Sort-Object Name | ForEach-Object {
        "$( (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() )  $($_.Name)"
    }
    [IO.File]::WriteAllLines((Join-Path $PWD 'artifacts/release/SHA256SUMS.txt'), [string[]]$lines, [Text.UTF8Encoding]::new($false))
} finally { Pop-Location }
