#requires -Version 7.2
<#
.SYNOPSIS
Rebuilds the real DEV source export outside Git with an isolated, offline NuGet cache.
.DESCRIPTION
Uses the already restored .nupkg files and the same SDK/fixed WebView2 runtime as
the initial pack. Rebuilt archives remain temporary; only the caller's log survives.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DevPack,
    [Parameter(Mandatory)][string]$RuntimePath,
    [Parameter(Mandatory)][string]$DotNetPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$ExpectedCommit
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'The real DEV rebuild check requires Windows.' }
. (Join-Path $PSScriptRoot 'SourceProvenance.ps1')
$utf8 = [Text.UTF8Encoding]::new($false)
$repository = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$DevPack = [IO.Path]::GetFullPath($DevPack)
$DotNetPath = [IO.Path]::GetFullPath($DotNetPath)
$RuntimePath = [IO.Path]::GetFullPath($RuntimePath)
$ExpectedCommit = $ExpectedCommit.ToLowerInvariant()
foreach ($path in @($DevPack, $DotNetPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required file unavailable: $path" }
}

function Invoke-DevRebuildProcess {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [hashtable]$Environment = @{},
        [int]$TimeoutSeconds = 120,
        [string]$ExpectedSummary = ''
    )
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.WorkingDirectory = $WorkingDirectory; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8; $start.StandardErrorEncoding = $utf8
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($name in $Environment.Keys) { $start.Environment[$name] = [string]$Environment[$name] }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    $started = $false
    try {
        $started = $process.Start()
        if (-not $started) { throw 'Could not start the DEV rebuild child process.' }
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $finished = $process.WaitForExit($TimeoutSeconds * 1000)
        if (-not $finished) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        if (-not $stdout.Wait(5000) -or -not $stderr.Wait(5000)) { throw 'DEV rebuild output capture exceeded its deadline.' }
        $report = $stdout.Result + $stderr.Result
        Write-Host $report
        if (-not $finished) { throw "DEV rebuild child exceeded its $TimeoutSeconds-second deadline." }
        if ($process.ExitCode -ne 0) { throw "DEV rebuild child failed with exit code $($process.ExitCode)." }
        if ($ExpectedSummary -and -not $report.Contains($ExpectedSummary, [StringComparison]::Ordinal)) {
            throw 'DEV rebuild child did not report the required successful summary.'
        }
        return $stdout.Result
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}

function Read-DevRebuildPack {
    param([Parameter(Mandatory)][string]$Path, [ValidateSet('PROD', 'DEV')][string]$Edition)
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $archive.GetEntry('wad/version.json')
        if (-not $entry -or $entry.Length -gt 64KB) { throw 'Pack metadata missing or oversized.' }
        $reader = [IO.StreamReader]::new($entry.Open(), [Text.Encoding]::UTF8, $true)
        try { $metadata = $reader.ReadToEnd() | ConvertFrom-Json -Depth 8 }
        finally { $reader.Dispose() }
        if ($metadata.application -cne 'GLPI Pilot' -or $metadata.distribution -cne $Edition -or
            $metadata.target -cne 'win-x64' -or -not $metadata.self_contained -or
            $metadata.source_commit -cne $ExpectedCommit -or $metadata.version -notmatch '^\d+\.\d+\.\d+([.-][A-Za-z0-9.-]+)?$') {
            throw "Unexpected $Edition pack identity or source commit."
        }
        $executable = $archive.GetEntry('GLPI-Pilot.exe')
        if (-not $executable) { throw "$Edition executable missing." }
        $stream = $executable.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($metadata.executable_sha256 -cne $hash) { throw "$Edition executable hash does not match its metadata." }
        return $metadata
    }
    finally { $archive.Dispose() }
}

function Expand-DevRebuildSources {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Destination)
    if (Test-Path -LiteralPath $Destination) { throw 'DEV source destination must be new.' }
    [void][IO.Directory]::CreateDirectory($Destination)
    $prefix = [IO.Path]::GetFullPath($Destination) + [IO.Path]::DirectorySeparatorChar
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $bytes = 0L
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($entry in $archive.Entries) {
            if (-not $entry.FullName.StartsWith('wad/sources/', [StringComparison]::Ordinal) -or $entry.FullName.EndsWith('/')) { continue }
            $relative = $entry.FullName.Substring('wad/sources/'.Length)
            if ($relative.Contains('\') -or $relative -match '[\x00-\x1f<>:"|?*]' -or
                @($relative.Split('/') | Where-Object { $_ -in @('', '.', '..', '.git') -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count -gt 0 -or
                -not $seen.Add($relative) -or $seen.Count -gt 10001) { throw 'Invalid or duplicate path in DEV sources.' }
            $bytes += $entry.Length
            if ($entry.Length -gt 128MB -or $bytes -gt 512MB) { throw 'DEV source export exceeds the bounded extraction budget.' }
            $destinationFile = [IO.Path]::GetFullPath((Join-Path $Destination $relative))
            if (-not $destinationFile.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'DEV source path escaped its temporary root.' }
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destinationFile))
            $sourceStream = $entry.Open(); $targetStream = [IO.File]::Open($destinationFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
            try { $sourceStream.CopyTo($targetStream) }
            finally { $targetStream.Dispose(); $sourceStream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    if ($seen.Count -eq 0) { throw 'The DEV ZIP contains no source export.' }
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('GLPI-DevRebuild-' + [guid]::NewGuid().ToString('N'))
if ($temporary.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The DEV rebuild temporary directory must be outside the repository.'
}
$ancestor = [IO.Path]::GetDirectoryName($temporary)
while ($ancestor) {
    if (Test-Path -LiteralPath (Join-Path $ancestor '.git')) { throw 'The DEV rebuild must not inherit a Git worktree.' }
    $ancestor = [IO.Path]::GetDirectoryName($ancestor)
}
$clearedEnvironment = @('GITHUB_SHA', 'GIT_DIR', 'GIT_WORK_TREE', 'GIT_COMMON_DIR', 'GIT_INDEX_FILE', 'GLPI_SIGN_CERT_THUMBPRINT', 'GLPI_SIGN_TIMESTAMP_URL', 'NUGET_FALLBACK_PACKAGES')
$savedEnvironment = @{}
$created = $false
try {
    foreach ($name in $clearedEnvironment) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
    if (Test-Path -LiteralPath $temporary) { throw 'DEV rebuild temporary directory already exists.' }
    [void][IO.Directory]::CreateDirectory($temporary); $created = $true
    $sources = Join-Path $temporary 'sources'
    $original = Read-DevRebuildPack -Path $DevPack -Edition DEV
    Expand-DevRebuildSources -Path $DevPack -Destination $sources
    $provenance = Resolve-GlpiBuildProvenance -SourceRoot $sources -ExpectedCommit $ExpectedCommit
    if ($provenance.Origin -cne 'Export') { throw 'The DEV rebuild did not resolve provenance from its exported manifest.' }
    Write-Host "PASS: exported DEV sources verified outside Git; $($provenance.Files.Count) files; commit $($provenance.Commit)."

    $cacheReport = Invoke-DevRebuildProcess -Executable $DotNetPath -Arguments @('nuget', 'locals', 'global-packages', '--list', '--force-english-output') -WorkingDirectory $repository -TimeoutSeconds 30
    if ($cacheReport -notmatch '(?m)^global-packages:\s*(.+?)\s*$') { throw 'Could not locate the packages restored by the original build.' }
    $packageCache = [IO.Path]::GetFullPath($Matches[1].Trim())
    if (-not (Test-Path -LiteralPath $packageCache -PathType Container)) { throw 'The original NuGet package cache is unavailable.' }
    $feed = Join-Path $temporary 'offline-feed'; [void][IO.Directory]::CreateDirectory($feed)
    $packageCount = 0
    foreach ($package in Get-ChildItem -LiteralPath $packageCache -Filter '*.nupkg' -File -Recurse) {
        $destination = Join-Path $feed $package.Name
        if (Test-Path -LiteralPath $destination) {
            if ((Get-FileHash -LiteralPath $destination).Hash -cne (Get-FileHash -LiteralPath $package.FullName).Hash) { throw 'Conflicting packages in the local offline feed.' }
            continue
        }
        [IO.File]::Copy($package.FullName, $destination, $false); $packageCount++
    }
    if ($packageCount -eq 0) { throw 'No restored .nupkg files are available for the offline rebuild.' }
    $childPackages = Join-Path $temporary 'child-packages'; [void][IO.Directory]::CreateDirectory($childPackages)
    $childTemp = Join-Path $temporary 'child-temp'; [void][IO.Directory]::CreateDirectory($childTemp)
    $childEnvironment = @{
        NUGET_PACKAGES = $childPackages; NUGET_HTTP_CACHE_PATH = (Join-Path $temporary 'http-cache')
        NUGET_PLUGINS_CACHE_PATH = (Join-Path $temporary 'plugin-cache'); NUGET_CERT_REVOCATION_MODE = 'offline'
        DOTNET_CLI_HOME = (Join-Path $temporary 'dotnet-home')
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'; DOTNET_CLI_TELEMETRY_OPTOUT = '1'; DOTNET_NOLOGO = '1'
        TEMP = $childTemp; TMP = $childTemp
    }
    $sdk = (Invoke-DevRebuildProcess -Executable $DotNetPath -Arguments @('--version') -WorkingDirectory $sources -Environment $childEnvironment -TimeoutSeconds 30).Trim()
    if ($sdk -cne $original.sdk) { throw "DEV rebuild SDK differs from the original: $sdk versus $($original.sdk)." }
    if (@(Get-ChildItem -LiteralPath $childPackages -Force).Count -ne 0) { throw 'The child NuGet cache must start empty.' }
    Write-Host "SDK=$sdk; OFFLINE_NUPKG_COUNT=$packageCount; CHILD_NUGET_CACHE=Empty; CI_GIT_ENV=Removed; SIGNING_NETWORK=Disabled."
    $release = Join-Path $temporary 'release'
    [void](Invoke-DevRebuildProcess -Executable (Get-Process -Id $PID).Path -Arguments @('-NoProfile', '-NonInteractive', '-File', (Join-Path $sources 'build.ps1'), '-OutputDirectory', $release, '-RuntimePath', $RuntimePath, '-NuGetOfflineSource', $feed, '-DotNetPath', $DotNetPath) -WorkingDirectory $sources -Environment $childEnvironment -TimeoutSeconds 600 -ExpectedSummary 'EXE_SHA256=')
    Write-Host 'PASS: real exported build.ps1 completed with the same SDK, fixed runtime and an isolated offline restore.'

    $rebuilt = @{}; $rebuiltPaths = @{}
    foreach ($edition in @('PROD', 'DEV')) {
        $archives = @(Get-ChildItem -LiteralPath $release -Filter ('GLPI-Pilot_*_' + $edition + '_Windows-x64.zip') -File)
        if ($archives.Count -ne 1) { throw "Expected one rebuilt $edition archive." }
        $rebuiltPaths[$edition] = $archives[0].FullName
        $rebuilt[$edition] = Read-DevRebuildPack -Path $archives[0].FullName -Edition $edition
        if ($rebuilt[$edition].version -cne $original.version -or $rebuilt[$edition].sdk -cne $original.sdk -or
            $rebuilt[$edition].webview2_runtime -cne $original.webview2_runtime -or
            $rebuilt[$edition].webview2_runtime_sha256 -cne $original.webview2_runtime_sha256) { throw "Rebuilt $edition version, SDK or fixed runtime differs from its original." }
    }
    if ($rebuilt['PROD'].executable_sha256 -cne $rebuilt['DEV'].executable_sha256) { throw 'Rebuilt PROD and DEV contain different executables.' }
    Write-Host "PASS: rebuilt PROD/DEV metadata and identical EXE verified; version $($original.version); EXE_SHA256=$($rebuilt['PROD'].executable_sha256)."
    $secondSources = Join-Path $temporary 'rebuilt-sources'
    Expand-DevRebuildSources -Path $rebuiltPaths['DEV'] -Destination $secondSources
    $second = Resolve-GlpiBuildProvenance -SourceRoot $secondSources -ExpectedCommit $ExpectedCommit
    if ($second.Origin -cne 'Export' -or $second.Tree -cne $provenance.Tree -or $second.Files.Count -ne $provenance.Files.Count) { throw 'Rebuilt DEV source provenance changed.' }
    foreach ($name in $provenance.Files.Keys) {
        if (-not $second.Files.ContainsKey($name) -or $second.Files[$name] -cne $provenance.Files[$name]) { throw "Rebuilt DEV source changed: $name" }
    }
    Write-Host 'PASS: the rebuilt DEV preserves the verified source inventory and original commit/tree.'

    $config = Join-Path $temporary 'verifier-nuget.config'
    $escapedFeed = [Security.SecurityElement]::Escape($feed)
    [IO.File]::WriteAllText($config, ('<configuration><packageSources><clear/><add key="offline" value="' + $escapedFeed + '"/></packageSources><fallbackPackageFolders><clear/></fallbackPackageFolders></configuration>'), $utf8)
    $verifier = Join-Path $sources 'tests/UpdateChecks/UpdateChecks.csproj'
    [void](Invoke-DevRebuildProcess -Executable $DotNetPath -Arguments @('restore', $verifier, '--configfile', $config, '-p:NuGetAudit=false', '-p:RestoreIgnoreFailedSources=false', '-p:RestoreAdditionalProjectSources=', '-p:RestoreAdditionalProjectFallbackFolders=') -WorkingDirectory $sources -Environment $childEnvironment -TimeoutSeconds 180)
    $digest = (Get-FileHash -LiteralPath $rebuiltPaths['PROD'] -Algorithm SHA256).Hash.ToLowerInvariant()
    [void](Invoke-DevRebuildProcess -Executable $DotNetPath -Arguments @('run', '--project', $verifier, '-c', 'Release', '--no-restore', '--', '--verify-pack', $rebuiltPaths['PROD'], $digest, $ExpectedCommit) -WorkingDirectory $sources -Environment $childEnvironment -TimeoutSeconds 360 -ExpectedSummary 'PASS package accepted by the production update verifier: PROD')
    Write-Host 'PASS: rebuilt PROD accepted by the actual update verifier; staged only, no application launched.'
}
finally {
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    if ($created -and (Test-Path -LiteralPath $temporary)) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
Write-Host '5/5 DEV rebuild checks passed; exported sources, isolated offline restore, no rebuilt pack published.'
