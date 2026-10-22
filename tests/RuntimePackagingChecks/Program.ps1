#requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$RuntimePath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repository 'build-tools/WebView2Runtime.ps1')
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('GLPI-Pilot-Checks-Packaging-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$script:passed = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }; $script:passed++; Write-Host ('PASS ' + $Message)
}
function Reject([scriptblock]$Action, [string]$Expected, [string]$Message) {
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_.Exception.Message }
    Check ($null -ne $caught -and $caught.Contains($Expected)) $Message
}
try {
    foreach ($relative in @('build.ps1', 'build-tools/WebView2Runtime.ps1', 'build-tools/Get-WebView2Runtime.ps1')) {
        $tokens = $null; $errors = $null
        [Management.Automation.Language.Parser]::ParseFile((Join-Path $repository $relative), [ref]$tokens, [ref]$errors) | Out-Null
        Check ($errors.Count -eq 0) ('PowerShell parser accepts ' + $relative)
    }
    $manifest = Get-WebView2PinnedManifest; $files = Get-WebView2PinnedFiles
    Check ($manifest.version -eq '154.0.4258.62' -and $manifest.architecture -eq 'x64' -and $files.Count -eq 257) 'Exact official x64 runtime and full file inventory are pinned'
    $runtime = Test-WebView2Runtime $RuntimePath
    Check ($runtime.Bytes -eq 701153191 -and $runtime.Files -eq 257) 'Every real runtime byte matches the official CAB file inventory'
    $cache = Split-Path -Parent $runtime.Path
    if ([IO.Path]::GetFileName($runtime.Path) -eq ('Microsoft.WebView2.FixedVersionRuntime.' + $manifest.version + '.x64')) {
        $reused = & (Join-Path $repository 'build-tools/Get-WebView2Runtime.ps1') -CacheDirectory $cache -Offline
        Check ($reused -eq $runtime.Path) 'Explicit offline preparation reuses only the verified existing cache'
    }
    Reject { Resolve-WebView2RuntimePath (Join-Path $temporary 'missing') } 'introuvable' 'Absent runtime fails without any download fallback'
    $incomplete = Join-Path $temporary 'incomplete'; New-Item -ItemType Directory -Path $incomplete | Out-Null
    [IO.File]::WriteAllText((Join-Path $incomplete 'msedgewebview2.exe'), 'fixture')
    Reject { Test-WebView2Runtime $incomplete } 'incomplet' 'Incomplete runtime is refused before packaging'
    $corrupt = Join-Path $temporary 'corrupt'; New-Item -ItemType Directory -Path $corrupt | Out-Null
    foreach ($name in $files.Keys) {
        $file = Join-Path $corrupt $name; New-Item -ItemType Directory -Path (Split-Path -Parent $file) -Force | Out-Null
        [IO.File]::WriteAllText($file, 'fixture')
    }
    Reject { Test-WebView2Runtime $corrupt } 'altéré' 'A full-looking runtime with incorrect file hashes is refused'
    [IO.File]::WriteAllText((Join-Path $corrupt 'unexpected.txt'), 'fixture')
    Reject { Test-WebView2Runtime $corrupt } 'supplémentaires' 'Extra runtime files are refused before distribution'
    # A local existing file satisfies the SDK path precheck. These cases must
    # fail before invoking it, so the real SDK/runtime/VM is never launched.
    $sdkFixture = Join-Path $temporary 'dotnet-fixture'; [IO.File]::WriteAllText($sdkFixture, '')
    Reject { & (Join-Path $repository 'build.ps1') -DotNetPath $sdkFixture -NuGetOfflineSource 'https://api.nuget.org/v3/index.json' } 'Source NuGet locale invalide' 'Offline source rejects an HTTP feed before restore'
    Reject { & (Join-Path $repository 'build.ps1') -DotNetPath $sdkFixture -NuGetOfflineSource $temporary -AllowOnlineRestore } 'pas les deux' 'Offline source and online permission cannot be silently combined'
    $sourceCopy = Join-Path $temporary 'manifest-copy'; New-Item -ItemType Directory -Path $sourceCopy | Out-Null
    foreach ($name in @('WebView2Runtime.ps1', 'WebView2Runtime.json', 'WebView2Runtime.files.sha256')) { Copy-Item -LiteralPath (Join-Path $repository ('build-tools/' + $name)) -Destination $sourceCopy }
    $indexPath = Join-Path $sourceCopy 'WebView2Runtime.files.sha256'
    [IO.File]::WriteAllText($indexPath, [IO.File]::ReadAllText($indexPath).Replace("`n", "`r`n"), [Text.UTF8Encoding]::new($false))
    & {
        . (Join-Path $sourceCopy 'WebView2Runtime.ps1')
        Check ((Get-WebView2PinnedFiles).Count -eq 257) 'Windows CRLF checkout preserves the pinned inventory digest'
        [IO.File]::AppendAllText($indexPath, 'tampered')
        Reject { Get-WebView2PinnedManifest } 'modifié' 'Tampered pinned inventory is rejected'
    }
    Write-Host ($script:passed.ToString() + '/' + $script:passed + ' runtime packaging checks passed; offline only, no runtime launched.')
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
