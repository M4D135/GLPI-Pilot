param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Le SDK .NET 10 est requis pour compiler les sources.' }
$project = Join-Path $PSScriptRoot 'src\GlpiPilot\GlpiPilot.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw -Encoding UTF8
$version = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($version -notmatch '^\d+\.\d+\.\d+([.-][A-Za-z0-9.-]+)?$') { throw 'La version du projet est invalide.' }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$root = Join-Path $OutputDirectory 'GLPI-Pilot'
$zipPath = Join-Path $OutputDirectory ('GLPI-Pilot_' + $version + '_Windows-x64.zip')
foreach ($destination in @($root, $zipPath)) {
    if (Test-Path -LiteralPath $destination) { throw ('La destination existe deja : ' + $destination + '. Choisis un autre OutputDirectory pour conserver le pack precedent.') }
}
foreach ($folder in @('src', 'tests', 'docs', 'support', 'licenses', '.github', 'build-tools')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $folder) -PathType Container)) {
        throw ('Dossier requis absent : ' + $folder)
    }
}
foreach ($name in @(
    'README.md', 'docs\Guide-GLPI-Pilot.html', 'docs\Rapport-verification.txt',
    'docs\Dashboard-Builder.txt', 'docs\GitHub-et-compilation.txt',
    'support\DIAGNOSTIC_DEMARRAGE.cmd', 'support\Diagnostic-Demarrage.ps1', 'support\LIRE-LE-DIAGNOSTIC.txt',
    'licenses\COMPOSANTS.txt', 'licenses\DotNet-ThirdPartyNotices.txt', 'licenses\Playwright-DotNet-MIT.txt',
    'licenses\microsoft.netcore.app.runtime.win-x64-LICENSE.TXT', 'licenses\microsoft.windowsdesktop.app.runtime.win-x64-LICENSE',
    '.github\workflows\windows.yml', 'build-tools\Invoke-WindowsCi.ps1'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $name) -PathType Leaf)) {
        throw ('Fichier requis absent : ' + $name)
    }
}
$publish = Join-Path $OutputDirectory ('publish-' + [guid]::NewGuid().ToString('N'))
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'La publication a echoue.' }

$wad = Join-Path $root 'wad'
New-Item -ItemType Directory -Path $wad -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $publish 'GLPI-Pilot.exe') -Destination $root
Copy-Item -LiteralPath (Join-Path $publish '.playwright') -Destination $wad -Recurse
foreach ($folder in @('docs', 'support', 'licenses')) {
    $source = Join-Path $PSScriptRoot $folder
    Copy-Item -LiteralPath $source -Destination $wad -Recurse
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $wad 'docs\LISEZ-MOI.md')
$driver = Join-Path $wad '.playwright\node\win32_x64\node.exe'
if (-not (Test-Path -LiteralPath $driver -PathType Leaf)) { throw 'Le pilote Windows node.exe manque dans la publication.' }
if (-not (Test-Path -LiteralPath (Join-Path $wad '.playwright\package\cli.js') -PathType Leaf)) { throw 'Le pilote Playwright cli.js manque dans la publication.' }
$sources = Join-Path $wad 'sources'
New-Item -ItemType Directory -Path $sources -Force | Out-Null
$sourceStage = Join-Path $OutputDirectory ('sources-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sourceStage -Force | Out-Null
foreach ($folder in @('src', 'tests', 'docs', 'support', 'licenses', '.github')) {
    $base = Join-Path $PSScriptRoot $folder
    if (-not (Test-Path -LiteralPath $base)) { continue }
    Get-ChildItem -LiteralPath $base -File -Recurse -Force | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules|artifacts)[\\/]' } | ForEach-Object {
        $relative = $_.FullName.Substring($PSScriptRoot.Length).TrimStart('\', '/')
        $destination = Join-Path $sourceStage $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destination
    }
}
foreach ($name in @('README.md', 'build.ps1', '.gitignore')) {
    $source = Join-Path $PSScriptRoot $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $sourceStage }
}
$buildTools = Join-Path $PSScriptRoot 'build-tools'
if (Test-Path -LiteralPath $buildTools) {
    $toolsDestination = Join-Path $sourceStage 'build-tools'
    New-Item -ItemType Directory -Path $toolsDestination -Force | Out-Null
    Get-ChildItem -LiteralPath $buildTools -File | Where-Object { $_.Extension -in @('.py', '.ps1') } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $toolsDestination
    }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($sourceStage, (Join-Path $sources 'GLPI-Pilot-Sources.zip'), [System.IO.Compression.CompressionLevel]::Optimal, $false)

$sdkVersion = (& dotnet --version | Select-Object -First 1)
if ($LASTEXITCODE -ne 0) { throw 'La version du SDK est indisponible.' }
$playwrightVersion = $projectXml.SelectSingleNode('/Project/ItemGroup/PackageReference[@Include="Microsoft.Playwright"]').GetAttribute('Version')
$metadata = [ordered]@{
    application = 'GLPI Pilot'; version = $version; target = 'win-x64'; sdk = $sdkVersion
    playwright = $playwrightVersion; date = (Get-Date -Format 'yyyy-MM-dd'); self_contained = $true; distribution = 'complete'
}
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $wad 'version.json'), (($metadata | ConvertTo-Json -Depth 4) + "`n"), $utf8)
$hashLines = @(Get-ChildItem -LiteralPath $root -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
    ((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relative)
})
[System.IO.File]::WriteAllText((Join-Path $wad 'SHA256SUMS.txt'), (($hashLines -join "`n") + "`n"), $utf8)

# ZipFile includes the complete driver folder even when its name begins with a dot.
[System.IO.Compression.ZipFile]::CreateFromDirectory($root, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
Write-Host ('Pack complet cree : ' + $zipPath)
