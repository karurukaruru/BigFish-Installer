$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$config = Get-Content (Join-Path $repo 'src/BigFish.App/bin/Release/net10.0-windows/win-x64/BigFish.runtimeconfig.json') -Raw | ConvertFrom-Json
if (!$config.runtimeOptions.includedFrameworks -or $config.runtimeOptions.framework) { throw 'Expected self-contained runtime config' }
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
foreach ($framework in $config.runtimeOptions.includedFrameworks) {
    $name = $framework.name.ToLowerInvariant() + '.runtime.win-x64'
    $source = Join-Path (Join-Path $cache $name) $framework.version
    if ($framework.name -eq 'Microsoft.NETCore.App') {
        Copy-Item (Join-Path $source 'LICENSE.TXT') (Join-Path $repo 'third-party/DOTNET-LICENSE.txt') -Force
        Copy-Item (Join-Path $source 'THIRD-PARTY-NOTICES.TXT') (Join-Path $repo 'third-party/DOTNET-THIRD-PARTY-NOTICES.txt') -Force
    } elseif ($framework.name -eq 'Microsoft.WindowsDesktop.App') {
        Copy-Item (Join-Path $source 'LICENSE') (Join-Path $repo 'third-party/WPF-LICENSE.txt') -Force
    }
}
