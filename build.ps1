#requires -Version 7.2
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'),
    [string]$ValidationDirectory = '',
    [string]$RuntimePath = '',
    [string[]]$NuGetOfflineSource = @(),
    [string]$DotNetPath = '',
    [switch]$IncludeRebuildFixture,
    [switch]$AllowOnlineRestore
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'build-tools/WebView2Runtime.ps1')
. (Join-Path $PSScriptRoot 'build-tools/SourceProvenance.ps1')
if (-not $DotNetPath) {
    if ($IsWindows -and (Test-Path -LiteralPath 'C:\DomeniaDev\dotnet\dotnet.exe' -PathType Leaf)) { $DotNetPath = 'C:\DomeniaDev\dotnet\dotnet.exe' }
    else {
        $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($dotnetCommand) { $DotNetPath = $dotnetCommand.Source }
    }
}
if (-not $DotNetPath -or -not (Test-Path -LiteralPath $DotNetPath -PathType Leaf)) { throw 'Le SDK .NET 10 est requis. Indiquez son exécutable avec -DotNetPath.' }
$DotNetPath = [IO.Path]::GetFullPath($DotNetPath)
if ($NuGetOfflineSource.Count -eq 0 -and -not $AllowOnlineRestore -and $IsWindows -and (Test-Path -LiteralPath 'C:\DomeniaDev\cache\nuget' -PathType Container)) { $NuGetOfflineSource = @('C:\DomeniaDev\cache\nuget') }
if ($NuGetOfflineSource.Count -eq 0 -and -not $AllowOnlineRestore) { throw 'Aucune source NuGet locale : indiquez -NuGetOfflineSource. La CI connectée peut autoriser explicitement -AllowOnlineRestore.' }
if ($NuGetOfflineSource.Count -gt 0 -and $AllowOnlineRestore) { throw 'Choisissez une restauration locale ou l''autorisation en ligne, pas les deux.' }
$NuGetOfflineSource = @($NuGetOfflineSource | ForEach-Object {
    if ($_ -match '^[a-zA-Z][a-zA-Z0-9+.-]*://' -or -not (Test-Path -LiteralPath $_ -PathType Container)) { throw ('Source NuGet locale invalide : ' + $_) }
    [IO.Path]::GetFullPath($_)
})
$sourceProvenance = Resolve-GlpiBuildProvenance -SourceRoot $PSScriptRoot
Write-Host ('Verified source provenance: ' + $sourceProvenance.Origin + ' ' + $sourceProvenance.Commit)
$runtime = Test-WebView2Runtime $RuntimePath
$project = Join-Path $PSScriptRoot 'src/GlpiPilot/GlpiPilot.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw -Encoding UTF8
$version = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($version -notmatch '^\d+\.\d+\.\d+([.-][A-Za-z0-9.-]+)?$') { throw 'La version du projet est invalide.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$roots = @{}; $archives = @{}
# The third archive is a private input to the CI offline rebuild test only.
$editions = @('PROD', 'PROD-LIGHT')
if ($IncludeRebuildFixture) { $editions += 'DEV' }
foreach ($edition in $editions) {
    $roots[$edition] = Join-Path $OutputDirectory $edition
    $archives[$edition] = Join-Path $OutputDirectory ('GLPI-Pilot_' + $version + '_' + $edition + '_Windows-x64.zip')
    foreach ($destination in @($roots[$edition], $archives[$edition])) {
        if (Test-Path -LiteralPath $destination) { throw ('La destination existe deja : ' + $destination + '. Choisis un autre OutputDirectory.') }
    }
}
$archiveHashes = Join-Path $OutputDirectory 'SHA256SUMS.txt'
if (Test-Path -LiteralPath $archiveHashes) { throw 'SHA256SUMS.txt existe deja dans le dossier de sortie.' }
foreach ($folder in @('src', 'tests', 'web', 'docs', 'support', 'licenses', '.github', 'build-tools')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $folder) -PathType Container)) { throw ('Dossier requis absent : ' + $folder) }
}
foreach ($name in @(
    'README.md', 'docs/Guide-GLPI-Pilot.html', 'docs/Rapport-verification.txt',
    'docs/Dashboard-Builder.txt', 'docs/GitHub-et-compilation.txt',
    'support/DEBLOQUER_TOUT.cmd', 'support/DIAGNOSTIC_DEMARRAGE.cmd', 'support/Diagnostic-Demarrage.ps1', 'support/LIRE-LE-DIAGNOSTIC.txt',
    'licenses/COMPOSANTS.txt', 'licenses/DotNet-ThirdPartyNotices.txt', 'licenses/Playwright-DotNet-MIT.txt',
    'licenses/microsoft.netcore.app.runtime.win-x64-LICENSE.TXT', 'licenses/microsoft.windowsdesktop.app.runtime.win-x64-LICENSE',
    'licenses/WebView2-SDK-LICENSE.txt', 'licenses/WebView2-FixedRuntime-LICENSE.html', 'web/index.html',
    '.github/workflows/windows.yml', 'build-tools/Invoke-WindowsCi.ps1', 'build-tools/Sign-Release.ps1',
    'build-tools/SourceProvenance.ps1', 'build-tools/Test-DevRebuild.ps1', 'tests/SourceProvenanceChecks/Program.ps1',
    'build-tools/WebView2Runtime.json', 'build-tools/WebView2Runtime.files.sha256', 'build-tools/Get-WebView2Runtime.ps1'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $name) -PathType Leaf)) { throw ('Fichier requis absent : ' + $name) }
}
if ($ValidationDirectory -and -not (Test-Path -LiteralPath $ValidationDirectory -PathType Container)) { throw 'Le dossier des journaux de validation est introuvable.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$publish = Join-Path $OutputDirectory ('publish-' + [guid]::NewGuid().ToString('N'))
$publishAdmin = Join-Path $OutputDirectory ('publish-admin-' + [guid]::NewGuid().ToString('N'))
$offlineConfig = $null
try {
    $restoreArguments = @('restore', $project, '-r', 'win-x64', '-p:NuGetAudit=false', '-p:RestoreIgnoreFailedSources=false')
    if ($NuGetOfflineSource.Count -gt 0) {
        # A standalone config clears inherited machine/repository HTTP sources.
        # Global packages already in NUGET_PACKAGES remain usable.
        $offlineConfig = Join-Path $OutputDirectory ('nuget-offline-' + [guid]::NewGuid().ToString('N') + '.config')
        $xmlSettings = [Xml.XmlWriterSettings]::new(); $xmlSettings.Indent = $true; $xmlSettings.Encoding = [Text.UTF8Encoding]::new($false)
        $writer = [Xml.XmlWriter]::Create($offlineConfig, $xmlSettings)
        try {
            $writer.WriteStartDocument(); $writer.WriteStartElement('configuration'); $writer.WriteStartElement('packageSources')
            $writer.WriteStartElement('clear'); $writer.WriteEndElement()
            for ($index = 0; $index -lt $NuGetOfflineSource.Count; $index++) {
                $writer.WriteStartElement('add'); $writer.WriteAttributeString('key', ('offline-' + $index)); $writer.WriteAttributeString('value', $NuGetOfflineSource[$index]); $writer.WriteEndElement()
            }
            $writer.WriteEndElement(); $writer.WriteStartElement('fallbackPackageFolders'); $writer.WriteStartElement('clear'); $writer.WriteEndElement(); $writer.WriteEndElement()
            $writer.WriteEndElement(); $writer.WriteEndDocument()
        } finally { $writer.Dispose() }
        $restoreArguments += @('--configfile', $offlineConfig, '-p:RestoreAdditionalProjectSources=', '-p:RestoreAdditionalProjectFallbackFolders=')
        Write-Host ('Restauration hors ligne depuis : ' + ($NuGetOfflineSource -join ' ; '))
    } else { Write-Host 'Restauration NuGet en ligne explicitement autorisée.' }
    & $DotNetPath @restoreArguments
    if ($LASTEXITCODE -ne 0) { throw 'La restauration a échoué. Aucun téléchargement de secours n''est tenté.' }
} finally { if ($offlineConfig -and (Test-Path -LiteralPath $offlineConfig)) { Remove-Item -LiteralPath $offlineConfig -Force } }
& $DotNetPath publish $project -c Release -r win-x64 --self-contained true --no-restore -p:NuGetAudit=false -p:PlaywrightPlatform=win -p:PilotAdmin=false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'La publication a echoue.' }
& (Join-Path $PSScriptRoot 'build-tools/Sign-Release.ps1') -ExecutablePath (Join-Path $publish 'GLPI-Pilot.exe')
& $DotNetPath publish $project -c Release -r win-x64 --self-contained true --no-restore -p:NuGetAudit=false -p:PlaywrightPlatform=win -p:PilotAdmin=true -o $publishAdmin
if ($LASTEXITCODE -ne 0) { throw 'La publication du lanceur Admin a échoué.' }
& (Join-Path $PSScriptRoot 'build-tools/Sign-Release.ps1') -ExecutablePath (Join-Path $publishAdmin 'GLPI-Pilot-Admin.exe')
$prod = $roots['PROD']; $prodWad = Join-Path $prod 'wad'
New-Item -ItemType Directory -Path $prodWad -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $publish 'GLPI-Pilot.exe') -Destination $prod
Copy-Item -LiteralPath (Join-Path $publishAdmin 'GLPI-Pilot-Admin.exe') -Destination $prod
Copy-Item -LiteralPath (Join-Path $publish '.playwright') -Destination $prodWad -Recurse
# Firefox n'a aucun canal Playwright : le pack complet embarque son propre build, le pack leger n'en dispose pas.
$env:PLAYWRIGHT_BROWSERS_PATH = Join-Path $publish '.local-browsers'
& (Join-Path $publish '.playwright/node/win32_x64/node.exe') (Join-Path $publish '.playwright/package/cli.js') install firefox
if ($LASTEXITCODE -ne 0) { throw "Le build Firefox embarque n'a pas pu etre prepare." }
Copy-Item -LiteralPath $env:PLAYWRIGHT_BROWSERS_PATH -Destination $prodWad -Recurse -Force
if (-not (Get-ChildItem -LiteralPath (Join-Path $prodWad '.local-browsers') -Recurse -File -Filter 'firefox.exe' -ErrorAction SilentlyContinue)) { throw 'Le pack complet ne contient aucun executable Firefox.' }
Copy-Item -LiteralPath (Join-Path $publish 'wad/web') -Destination $prodWad -Recurse
$runtimeDestination = Join-Path $prodWad 'runtime/webview2'
New-Item -ItemType Directory -Path $runtimeDestination -Force | Out-Null
Get-ChildItem -LiteralPath $runtime.Path -Force | Copy-Item -Destination $runtimeDestination -Recurse
# Verify the actual copied output, before it enters either archive.
$copiedRuntime = Test-WebView2Runtime $runtimeDestination
foreach ($source in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'web') -File -Recurse -Force) {
    $relative = [IO.Path]::GetRelativePath((Join-Path $PSScriptRoot 'web'), $source.FullName)
    $copied = Join-Path (Join-Path $prodWad 'web') $relative
    if (-not (Test-Path -LiteralPath $copied -PathType Leaf) -or (Get-FileHash -LiteralPath $source.FullName).Hash -ne (Get-FileHash -LiteralPath $copied).Hash) { throw ('Asset WebView publié absent ou périmé : ' + $relative) }
}
foreach ($folder in @('docs', 'support', 'licenses')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $folder) -Destination $prodWad -Recurse }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $prodWad 'docs/LISEZ-MOI.md')
foreach ($relative in @('.playwright/node/win32_x64/node.exe', '.playwright/package/cli.js', 'web/index.html', 'web/js/app.js', 'web/js/bridge.js', 'runtime/webview2/msedgewebview2.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $prodWad $relative) -PathType Leaf)) { throw ('Pilote Windows incomplet : ' + $relative) }
}
if (Get-ChildItem -LiteralPath $prod -File -Recurse -Force | Where-Object Name -Like '.node.exe.*') { throw 'Un fichier de transfert incomplet est present dans le pilote.' }
$light = $roots['PROD-LIGHT']
New-Item -ItemType Directory -Path $light -Force | Out-Null
Get-ChildItem -LiteralPath $prod -Force | Copy-Item -Destination $light -Recurse
Remove-Item -LiteralPath (Join-Path $light 'wad/runtime') -Recurse -Force
if (Test-Path -LiteralPath (Join-Path $light 'wad/runtime')) { throw 'Le pack allégé contient encore un runtime.' }
# Le pack leger est copie depuis PROD : le build Firefox, livre comme composant installe separement, doit en
# etre retire ici aussi. Sans ce retrait, l'edition legere transporterait un navigateur qu'elle refuse de proposer.
Remove-Item -LiteralPath (Join-Path $light 'wad/.local-browsers') -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath (Join-Path $light 'wad/.local-browsers')) { throw 'Le pack allégé contient encore le build Firefox.' }
if ($IncludeRebuildFixture) {
    $dev = $roots['DEV']; $devWad = Join-Path $dev 'wad'
    New-Item -ItemType Directory -Path $dev -Force | Out-Null
    Get-ChildItem -LiteralPath $prod -Force | Copy-Item -Destination $dev -Recurse
    $sources = Join-Path $devWad 'sources'
    Export-GlpiBuildSources -Provenance $sourceProvenance -Destination $sources
    if ($ValidationDirectory) {
        $validation = Join-Path $devWad 'validation'
        New-Item -ItemType Directory -Path $validation -Force | Out-Null
        Get-ChildItem -LiteralPath $ValidationDirectory -File -Recurse -Force | Where-Object { $_.Extension -in @('.log', '.txt', '.json') } | ForEach-Object {
            $relative = [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($ValidationDirectory), $_.FullName)
            $destination = Join-Path $validation $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $destination
        }
    }
}
# --- Packages de dependances -------------------------------------------------
# Le pack applicatif leger ne transporte ni runtime WebView2 fixe, ni pilote Playwright, ni build Firefox.
# Chacun est livre separement, avec son manifeste et son inventaire verifie, pour etre installe hors ligne.
$componentsDirectory = Join-Path $OutputDirectory 'composants'
New-Item -ItemType Directory -Path $componentsDirectory -Force | Out-Null
function New-ComponentPackage {
    param([string]$Id, [string]$Version, [string]$SourceRoot, [string[]]$Elements)
    $stage = Join-Path $componentsDirectory ('.' + $Id + '-staging')
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    foreach ($element in $Elements) {
        $source = Join-Path $SourceRoot $element
        if (-not (Test-Path -LiteralPath $source)) { throw "Dependance ${Id}: element absent $element." }
        $destination = Join-Path $stage $element
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Recurse -Force
    }
    $bytes = [int64]((Get-ChildItem -LiteralPath $stage -Recurse -File | Measure-Object -Property Length -Sum).Sum)
    [ordered]@{ application = 'GLPI Pilot'; target = 'win-x64'; id = $Id; version = $Version; bytes = $bytes } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stage 'component.json') -Encoding utf8
    $sums = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object FullName | ForEach-Object {
        ((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + ([IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\', '/')))
    }
    (($sums -join "`n") + "`n") | Set-Content -LiteralPath (Join-Path $stage 'SHA256SUMS.txt') -Encoding utf8
    $archive = Join-Path $componentsDirectory ('GLPI-Pilot_' + $Version + '_' + $Id + '.zip')
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
    Remove-Item -LiteralPath $stage -Recurse -Force
    [pscustomobject]@{ Id = $Id; Archive = $archive; Sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant(); Bytes = $bytes }
}
$componentPackages = @(
    New-ComponentPackage -Id 'webview2-fixed' -Version $version -SourceRoot $prodWad -Elements @('runtime/webview2')
    New-ComponentPackage -Id 'playwright-driver' -Version $version -SourceRoot $prodWad -Elements @('.playwright')
    New-ComponentPackage -Id 'firefox' -Version $version -SourceRoot $prodWad -Elements @('.local-browsers')
)
$componentDigests = $componentPackages | ForEach-Object { $_.Sha256 + '  ' + [IO.Path]::GetFileName($_.Archive) }
[IO.File]::WriteAllText((Join-Path $componentsDirectory 'SHA256SUMS.txt'), (($componentDigests -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
foreach ($package in $componentPackages) { Write-Host ('Dependance creee : ' + [IO.Path]::GetFileName($package.Archive) + ' ; SHA-256: ' + $package.Sha256) }
# ---------------------------------------------------------------------------

$utf8 = [Text.UTF8Encoding]::new($false)
$sdkVersion = (& $DotNetPath --version | Select-Object -First 1)
if ($LASTEXITCODE -ne 0) { throw 'La version du SDK est indisponible.' }
$playwrightVersion = $projectXml.SelectSingleNode('/Project/ItemGroup/PackageReference[@Include="Microsoft.Playwright"]').GetAttribute('Version')
$webViewSdkVersion = $projectXml.SelectSingleNode('/Project/ItemGroup/PackageReference[@Include="Microsoft.Web.WebView2"]').GetAttribute('Version')
$executableHash = (Get-FileHash -LiteralPath (Join-Path $prod 'GLPI-Pilot.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
$adminExecutableHash = (Get-FileHash -LiteralPath (Join-Path $prod 'GLPI-Pilot-Admin.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
foreach ($edition in $editions | Where-Object { $_ -ne 'PROD' }) {
    $other = $roots[$edition]
    if ((Get-FileHash -LiteralPath (Join-Path $other 'GLPI-Pilot.exe') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $executableHash) { throw 'Les executables des packs sont differents.' }
    if ((Get-FileHash -LiteralPath (Join-Path $other 'GLPI-Pilot-Admin.exe') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $adminExecutableHash) { throw 'Les lanceurs Admin des packs sont différents.' }
}
if ($adminExecutableHash -eq $executableHash) { throw 'Les lanceurs Standard et Admin doivent porter des capacités distinctes.' }
$sourceCommit = $sourceProvenance.Commit
$digests = [Collections.Generic.List[string]]::new()
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($edition in $editions) {
    $root = $roots[$edition]; $wad = Join-Path $root 'wad'
    $bundled = $edition -ne 'PROD-LIGHT'
    $metadata = [ordered]@{
        application = 'GLPI Pilot'; version = $version; target = 'win-x64'; sdk = $sdkVersion
        playwright = $playwrightVersion; date = (Get-Date -Format 'yyyy-MM-dd'); self_contained = $true
        ui = 'webview2'; webview2_sdk = $webViewSdkVersion; webview2_runtime = $(if ($bundled) { $runtime.Version } else { '' })
        webview2_runtime_sha256 = $(if ($bundled) { $runtime.CabSha256 } else { '' }); webview2_loader = $(if ($bundled) { 'bundled-native-x64' } else { 'evergreen-installed' })
        distribution = $edition; source_commit = $sourceCommit; executable_sha256 = $executableHash; admin_executable_sha256 = $adminExecutableHash
    }
    [IO.File]::WriteAllText((Join-Path $wad 'version.json'), (($metadata | ConvertTo-Json -Depth 4) + "`n"), $utf8)
    $hashLines = @(Get-ChildItem -LiteralPath $root -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/')
        ((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relative)
    })
    [IO.File]::WriteAllText((Join-Path $wad 'SHA256SUMS.txt'), (($hashLines -join "`n") + "`n"), $utf8)
    # false omits the PROD/DEV staging folder: EXE + wad are directly at ZIP root.
    [IO.Compression.ZipFile]::CreateFromDirectory($root, $archives[$edition], [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash -LiteralPath $archives[$edition] -Algorithm SHA256).Hash.ToLowerInvariant()
    $digests.Add($hash + '  ' + [IO.Path]::GetFileName($archives[$edition]))
    Write-Host ('Pack ' + $edition + ' cree : ' + $archives[$edition])
}
[IO.File]::WriteAllText($archiveHashes, (($digests -join "`n") + "`n"), $utf8)
Write-Host ('EXE_SHA256=' + $executableHash)
Write-Host ('ADMIN_EXE_SHA256=' + $adminExecutableHash)
Write-Host ('Source commit: ' + $sourceCommit)
$digests | ForEach-Object { Write-Host $_ }
