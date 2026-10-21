#requires -Version 7.2
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'),
    [string]$ValidationDirectory = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Le SDK .NET 10 est requis pour compiler les sources.' }
$project = Join-Path $PSScriptRoot 'src/GlpiPilot/GlpiPilot.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw -Encoding UTF8
$version = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($version -notmatch '^\d+\.\d+\.\d+([.-][A-Za-z0-9.-]+)?$') { throw 'La version du projet est invalide.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$roots = @{}; $archives = @{}
foreach ($edition in @('PROD', 'DEV')) {
    $roots[$edition] = Join-Path $OutputDirectory $edition
    $archives[$edition] = Join-Path $OutputDirectory ('GLPI-Pilot_' + $version + '_' + $edition + '_Windows-x64.zip')
    foreach ($destination in @($roots[$edition], $archives[$edition])) {
        if (Test-Path -LiteralPath $destination) { throw ('La destination existe deja : ' + $destination + '. Choisis un autre OutputDirectory.') }
    }
}
$archiveHashes = Join-Path $OutputDirectory 'SHA256SUMS.txt'
if (Test-Path -LiteralPath $archiveHashes) { throw 'SHA256SUMS.txt existe deja dans le dossier de sortie.' }
foreach ($folder in @('src', 'tests', 'docs', 'support', 'licenses', '.github', 'build-tools')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $folder) -PathType Container)) { throw ('Dossier requis absent : ' + $folder) }
}
foreach ($name in @(
    'README.md', 'docs/Guide-GLPI-Pilot.html', 'docs/Rapport-verification.txt',
    'docs/Dashboard-Builder.txt', 'docs/GitHub-et-compilation.txt',
    'support/DIAGNOSTIC_DEMARRAGE.cmd', 'support/Diagnostic-Demarrage.ps1', 'support/LIRE-LE-DIAGNOSTIC.txt',
    'licenses/COMPOSANTS.txt', 'licenses/DotNet-ThirdPartyNotices.txt', 'licenses/Playwright-DotNet-MIT.txt',
    'licenses/microsoft.netcore.app.runtime.win-x64-LICENSE.TXT', 'licenses/microsoft.windowsdesktop.app.runtime.win-x64-LICENSE',
    '.github/workflows/windows.yml', 'build-tools/Invoke-WindowsCi.ps1'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $name) -PathType Leaf)) { throw ('Fichier requis absent : ' + $name) }
}
if ($ValidationDirectory -and -not (Test-Path -LiteralPath $ValidationDirectory -PathType Container)) { throw 'Le dossier des journaux de validation est introuvable.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$publish = Join-Path $OutputDirectory ('publish-' + [guid]::NewGuid().ToString('N'))
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:PlaywrightPlatform=win -o $publish
if ($LASTEXITCODE -ne 0) { throw 'La publication a echoue.' }
$prod = $roots['PROD']; $prodWad = Join-Path $prod 'wad'
New-Item -ItemType Directory -Path $prodWad -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $publish 'GLPI-Pilot.exe') -Destination $prod
Copy-Item -LiteralPath (Join-Path $publish '.playwright') -Destination $prodWad -Recurse
foreach ($folder in @('docs', 'support', 'licenses')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $folder) -Destination $prodWad -Recurse }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $prodWad 'docs/LISEZ-MOI.md')
foreach ($relative in @('.playwright/node/win32_x64/node.exe', '.playwright/package/cli.js')) {
    if (-not (Test-Path -LiteralPath (Join-Path $prodWad $relative) -PathType Leaf)) { throw ('Pilote Windows incomplet : ' + $relative) }
}
if (Get-ChildItem -LiteralPath $prod -File -Recurse -Force | Where-Object Name -Like '.node.exe.*') { throw 'Un fichier de transfert incomplet est present dans le pilote.' }
$dev = $roots['DEV']; $devWad = Join-Path $dev 'wad'
New-Item -ItemType Directory -Path $dev -Force | Out-Null
Get-ChildItem -LiteralPath $prod -Force | Copy-Item -Destination $dev -Recurse
$sources = Join-Path $devWad 'sources'
New-Item -ItemType Directory -Path $sources -Force | Out-Null
foreach ($folder in @('src', 'tests', 'docs', 'support', 'licenses', '.github')) {
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $folder) -File -Recurse -Force |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules|artifacts|__pycache__|\.git)[\\/]' } |
        ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($PSScriptRoot, $_.FullName)
            $destination = Join-Path $sources $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $destination
        }
}
$rootFiles = @('README.md', 'build.ps1', '.gitignore', 'global.json', 'NuGet.Config', 'nuget.config', 'Directory.Packages.props')
$rootFiles += @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'Directory.Build.*' -File | ForEach-Object Name)
foreach ($name in @($rootFiles | Select-Object -Unique)) {
    $source = Join-Path $PSScriptRoot $name
    if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination (Join-Path $sources $name) }
}
$toolsDestination = Join-Path $sources 'build-tools'
New-Item -ItemType Directory -Path $toolsDestination -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'build-tools') -File |
    Where-Object { $_.Extension -in @('.py', '.ps1') } | Copy-Item -Destination $toolsDestination
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
$utf8 = [Text.UTF8Encoding]::new($false)
$sdkVersion = (& dotnet --version | Select-Object -First 1)
if ($LASTEXITCODE -ne 0) { throw 'La version du SDK est indisponible.' }
$playwrightVersion = $projectXml.SelectSingleNode('/Project/ItemGroup/PackageReference[@Include="Microsoft.Playwright"]').GetAttribute('Version')
$executableHash = (Get-FileHash -LiteralPath (Join-Path $prod 'GLPI-Pilot.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
if ((Get-FileHash -LiteralPath (Join-Path $dev 'GLPI-Pilot.exe') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $executableHash) { throw 'Les executables PROD et DEV sont differents.' }
$sourceCommit = if ($env:GITHUB_SHA -match '^[a-fA-F0-9]{40}$') { $env:GITHUB_SHA.ToLowerInvariant() } else { $null }
$digests = [Collections.Generic.List[string]]::new()
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($edition in @('PROD', 'DEV')) {
    $root = $roots[$edition]; $wad = Join-Path $root 'wad'
    $metadata = [ordered]@{
        application = 'GLPI Pilot'; version = $version; target = 'win-x64'; sdk = $sdkVersion
        playwright = $playwrightVersion; date = (Get-Date -Format 'yyyy-MM-dd'); self_contained = $true
        distribution = $edition; source_commit = $sourceCommit; executable_sha256 = $executableHash
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
Write-Host ('Source commit: ' + $sourceCommit)
$digests | ForEach-Object { Write-Host $_ }
