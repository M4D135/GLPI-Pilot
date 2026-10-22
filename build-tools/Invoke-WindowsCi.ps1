#requires -Version 7.2
<#
.SYNOPSIS
Builds and runs the Windows CI stages without modifying production app code.
.DESCRIPTION
Portable suites exercise the real C# engines and local ES modules on synthetic data.
PackStartup then drives the actual packaged WinForms/WebView2 host, with its fixed
runtime, through a loopback-only test endpoint available only in a guarded temporary
profile. UpdateStartup exercises replacement and rollback on disposable pack copies.
BuildPack also rebuilds the exported DEV sources outside Git with an empty child
NuGet cache and a local feed, then verifies the rebuilt PROD through UpdateChecks.
Neither native harness reads an existing application profile or signs in to GLPI.
All child processes have an external deadline and must report a successful summary.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('BclChecks', 'BrowserChecks', 'WebViewChecks', 'BuildPack', 'PackStartup', 'UpdateStartup')]
    [string]$Stage,
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/windows-ci')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'These CI stages require Windows; a cross-build is not a Windows test.' }
$repository = Split-Path -Parent $PSScriptRoot
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$logs = Join-Path $OutputDirectory 'logs'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)

function Invoke-CheckedProcess {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)][string]$LogName,
        [int]$TimeoutSeconds = 300,
        [string]$ExpectedSummary = '',
        [string]$ExpectedPattern = ''
    )
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.WorkingDirectory = $repository
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $stdout = $null; $stderr = $null; $timedOut = $false
    try {
        if (-not $process.Start()) { throw "Could not start $LogName." }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $timedOut = $true
            $process.Kill($true)
            [void]$process.WaitForExit(5000)
        }
        if (-not $stdout.Wait(5000) -or -not $stderr.Wait(5000)) { throw "Output capture did not finish for $LogName." }
        $report = $stdout.Result + $stderr.Result
        if ($timedOut) { $report += "`nFAIL: process deadline of $TimeoutSeconds seconds exceeded.`n" }
        [IO.File]::WriteAllText((Join-Path $logs $LogName), $report, $utf8)
        Write-Host $report
        if ($timedOut) { throw "$LogName exceeded its $TimeoutSeconds-second deadline." }
        if ($process.ExitCode -ne 0) { throw "$LogName failed with exit code $($process.ExitCode)." }
        if ($ExpectedSummary -and -not $report.Contains($ExpectedSummary, [StringComparison]::Ordinal)) {
            throw "$LogName exited without the expected successful test summary."
        }
        if ($ExpectedPattern -and $report -notmatch $ExpectedPattern) {
            throw "$LogName exited without the expected successful test summary pattern."
        }
    }
    finally {
        if ($stdout -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}

function Get-Pack {
    param([ValidateSet('PROD', 'DEV')][string]$Edition = 'PROD')
    $pattern = 'GLPI-Pilot_*_' + $Edition + '_Windows-x64.zip'
    $files = @(Get-ChildItem -LiteralPath (Join-Path $OutputDirectory 'release') -Filter $pattern -File)
    if ($files.Count -ne 1) { throw "Expected exactly one $Edition Windows ZIP from build.ps1." }
    return $files[0]
}

function Get-EntryHash {
    param([IO.Compression.ZipArchiveEntry]$Entry)
    $stream = $Entry.Open()
    try { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
    finally { $stream.Dispose() }
}

function Assert-PackContents {
    param([IO.FileInfo]$Pack, [ValidateSet('PROD', 'DEV')][string]$Edition = 'PROD')
    $archive = [IO.Compression.ZipFile]::OpenRead($Pack.FullName)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        $top = @($entries | ForEach-Object { ($_ -split '/')[0] } | Sort-Object -Unique)
        if ($top.Count -ne 2 -or $top -notcontains 'GLPI-Pilot.exe' -or $top -notcontains 'wad') {
            throw "$Edition ZIP must contain only GLPI-Pilot.exe and wad at its root."
        }
        $required = @(
            'GLPI-Pilot.exe', 'wad/.playwright/node/win32_x64/node.exe',
            'wad/.playwright/package/cli.js', 'wad/docs/Guide-GLPI-Pilot.html',
            'wad/support/DEBLOQUER_TOUT.cmd', 'wad/support/DIAGNOSTIC_DEMARRAGE.cmd', 'wad/licenses/COMPOSANTS.txt',
            'wad/version.json', 'wad/SHA256SUMS.txt',
            'wad/web/index.html', 'wad/web/js/app.js', 'wad/web/js/bridge.js', 'wad/web/css/app.css',
            'wad/runtime/webview2/msedgewebview2.exe', 'wad/runtime/webview2/msedge.dll',
            'wad/licenses/WebView2-SDK-LICENSE.txt', 'wad/licenses/WebView2-FixedRuntime-LICENSE.html'
        )
        if ($Edition -eq 'DEV') {
            $required += @(
                'wad/sources/build.ps1', 'wad/sources/README.md',
                'wad/sources/src/GlpiPilot/GlpiPilot.csproj',
                'wad/sources/tests/WindowsStartupChecks/Program.cs',
                'wad/sources/build-tools/Invoke-WindowsCi.ps1', 'wad/sources/web/index.html',
                'wad/sources/src/GlpiPilot/Desktop/MainForm.cs', 'wad/sources/build-tools/WebView2Runtime.json',
                'wad/sources/.github/workflows/windows.yml', 'wad/sources/GLPI-Pilot.sources.json',
                'wad/sources/build-tools/SourceProvenance.ps1', 'wad/sources/build-tools/Test-DevRebuild.ps1',
                'wad/sources/tests/SourceProvenanceChecks/Program.ps1',
                'wad/sources/tests/UpdateChecks/Program.cs', 'wad/sources/tests/UpdateChecks/UpdateChecks.csproj'
            )
        }
        foreach ($name in $required) { if ($entries -notcontains $name) { throw "Incomplete $Edition pack: $name is missing." } }
        if ($Edition -eq 'PROD' -and ($entries | Where-Object { $_ -like 'wad/sources/*' })) { throw 'PROD unexpectedly contains development sources.' }
        if ($entries | Where-Object { $_ -match '(^|/)(settings\.json|Browser|Profiles|Exports)(/|$)|(^|/)\.node\.exe\.' }) {
            throw 'Application state or a partial transfer is present in the pack.'
        }
        $manifestReader = [IO.StreamReader]::new($archive.GetEntry('wad/SHA256SUMS.txt').Open(), [Text.Encoding]::UTF8, $true)
        try { $lines = $manifestReader.ReadToEnd() -split '\r?\n' }
        finally { $manifestReader.Dispose() }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($line in $lines) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            if ($line -notmatch '^([a-f0-9]{64})  (.+)$') { throw "Invalid SHA-256 manifest in $Edition." }
            $expected = $Matches[1]; $name = $Matches[2]
            if (-not $seen.Add($name)) { throw "Duplicate manifest path: $name" }
            $entry = $archive.GetEntry($name)
            if ($null -eq $entry -or (Get-EntryHash $entry) -ne $expected) { throw "Missing or corrupted $Edition file: $name" }
        }
        $fileEntries = @($archive.Entries | Where-Object { -not $_.FullName.EndsWith('/') })
        if ($seen.Count -ne $fileEntries.Count - 1) { throw "$Edition manifest does not cover every packaged file." }
        $metadataReader = [IO.StreamReader]::new($archive.GetEntry('wad/version.json').Open(), [Text.Encoding]::UTF8, $true)
        try { $metadata = $metadataReader.ReadToEnd() | ConvertFrom-Json }
        finally { $metadataReader.Dispose() }
        $exeHash = Get-EntryHash $archive.GetEntry('GLPI-Pilot.exe')
        if ($metadata.distribution -ne $Edition -or $metadata.executable_sha256 -ne $exeHash) { throw "$Edition metadata does not match its executable." }
        if ($env:GITHUB_SHA -and $metadata.source_commit -ne $env:GITHUB_SHA) { throw "$Edition is not associated with the current source commit." }
        Write-Host "PASS: $Edition root, required files and all $($seen.Count) SHA-256 entries verified. EXE_SHA256=$exeHash"
        return $exeHash
    }
    finally { $archive.Dispose() }
}

function Set-TestBrowser {
    $browserCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft/Edge/Application/msedge.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft/Edge/Application/msedge.exe'),
        (Join-Path $env:ProgramFiles 'Google/Chrome/Application/chrome.exe')
    )
    $browser = $browserCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $browser) { throw 'No supported Chromium browser is available for the isolated synthetic checks.' }
    $env:GLPI_TEST_CHROMIUM = $browser
}

function Test-PackStartup {
    $harness = Join-Path $OutputDirectory 'webview-native-harness'
    Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('publish', 'tests/WindowsStartupChecks/WindowsStartupChecks.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:PlaywrightPlatform=win', '-o', $harness) `
        -LogName 'windows-startup-build.log' -TimeoutSeconds 420
    $pack = Get-Pack -Edition PROD
    [void](Assert-PackContents $pack -Edition PROD)
    Invoke-CheckedProcess -Executable (Join-Path $harness 'WindowsStartupChecks.exe') `
        -Arguments @('--pack', $pack.FullName, '--out', (Join-Path $logs 'native-webview')) `
        -LogName 'windows-startup-checks.log' -TimeoutSeconds 360 -ExpectedPattern '\d+/\d+ native Windows WebView checks passed; isolated profile, no GLPI session accessed\.'
}

Push-Location $repository
try {
    switch ($Stage) {
        'BclChecks' {
            Invoke-CheckedProcess -Executable (Get-Process -Id $PID).Path -Arguments @('-NoProfile', '-NonInteractive', '-File', (Join-Path $repository 'tests/SourceProvenanceChecks/Program.ps1')) `
                -LogName 'source-provenance-checks.log' -TimeoutSeconds 180 -ExpectedPattern '\d+/\d+ source provenance checks passed\.'
            $suites = @(
                @{ Project = 'StartupDiagnosticsChecks'; Log = 'startup-diagnostics-checks.log'; Summary = 'Startup diagnostics: 14 passed, 0 failed.' },
                @{ Project = 'StartupResilienceChecks'; Log = 'startup-resilience-checks.log'; Summary = 'startup resilience checks passed.' },
                @{ Project = 'FoundationChecks'; Log = 'foundation-checks.log'; Summary = ''; Pattern = '\d+/\d+ Foundation checks passed\.' },
                @{ Project = 'WorkplaceChecks'; Log = 'workplace-checks.log'; Summary = 'workplace checks passed.' },
                @{ Project = 'ConfigurationChecks'; Log = 'configuration-checks.log'; Summary = '39/39 vérifications de configuration réussies.' },
                @{ Project = 'DashboardChecks'; Log = 'dashboard-checks.log'; Summary = '114/114' },
                @{ Project = 'DashboardLayoutChecks'; Log = 'dashboard-layout-checks.log'; Summary = '34/34 checks passed.' },
                @{ Project = 'PreviewQueryChecks'; Log = 'preview-query-checks.log'; Summary = '44/44' },
                @{ Project = 'TechnicianWorkloadChecks'; Log = 'technician-workload-checks.log'; Summary = '35/35' },
                @{ Project = 'CityDetectionChecks'; Log = 'city-detection-checks.log'; Summary = '91/91' },
                @{ Project = 'TourPlanningChecks'; Log = 'tour-planning-checks.log'; Summary = '87/87' },
                @{ Project = 'QueryChecks'; Log = 'query-checks.log'; Summary = '99/99 vérifications réussies.' },
                @{ Project = 'AnalyticsChecks'; Log = 'analytics-checks.log'; Summary = '32/32 vérifications réussies.' },
                @{ Project = 'CompletionChecks'; Log = 'completion-checks.log'; Summary = '44/44 Completion checks passed.' },
                @{ Project = 'UpdateChecks'; Log = 'update-checks.log'; Summary = ''; Pattern = '\d+/\d+ update checks passed\.' },
                @{ Project = 'LanguageAssistantChecks'; Log = 'language-assistant-checks.log'; Summary = ''; Pattern = '\d+/\d+ language assistant checks passed; synthetic handlers only, no provider contacted\.' },
                @{ Project = 'NativeImportChecks'; Log = 'native-import-checks.log'; Summary = '40/40' },
                @{ Project = 'ExportChecks'; Log = 'export-checks.log'; Summary = '6/6' }
            )
            foreach ($suite in $suites) {
                $project = 'tests/' + $suite.Project + '/' + $suite.Project + '.csproj'
                $pattern = if ($suite.ContainsKey('Pattern')) { $suite.Pattern } else { '' }
                Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', $project, '-c', 'Release', '-p:PlaywrightPlatform=win') `
                    -LogName $suite.Log -TimeoutSeconds 180 -ExpectedSummary $suite.Summary -ExpectedPattern $pattern
            }
        }
        'BrowserChecks' {
            Set-TestBrowser
            foreach ($suite in @('BrowserChecks', 'AutomationChecks')) {
                Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', "tests/$suite/$suite.csproj", '-c', 'Release') `
                    -LogName ($suite.ToLowerInvariant() + '.log') -TimeoutSeconds 180 -ExpectedPattern '\d+/\d+ vérifications.*réussies\.'
            }
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/TicketDetailChecks/TicketDetailChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win') `
                -LogName 'ticketdetailchecks.log' -TimeoutSeconds 180 -ExpectedSummary '32/32 contrôles de lecture de fiche réussis.'
            $driver = Join-Path $repository 'tests/AutomationChecks/bin/Release/net10.0/.playwright'
            $env:GLPI_TEST_PLAYWRIGHT_MODULE = Join-Path $driver 'package'
            Invoke-CheckedProcess -Executable (Join-Path $driver 'node/win32_x64/node.exe') `
                -Arguments @('tests/NativeDomChecks/run.cjs', (Join-Path $logs 'native-dom-detail.log')) `
                -LogName 'native-dom-checks.log' -TimeoutSeconds 120 -ExpectedSummary '159 native export DOM checks passed.'
        }
        'WebViewChecks' {
            Set-TestBrowser
            foreach ($suite in @('WebViewChecks', 'RuntimeSessionChecks', 'AnalyticsBridgeChecks', 'WorkplaceBridgeChecks', 'TerrainBridgeChecks')) {
                $arguments = @('run', '--project', "tests/$suite/$suite.csproj", '-c', 'Release', '-p:PlaywrightPlatform=win')
                $pattern = '\d+/\d+ '
                switch ($suite) {
                    'WebViewChecks' { $arguments += @('--', '--screenshots', (Join-Path $logs 'web-preview')); $pattern += 'WebView integration checks passed\.' }
                    'RuntimeSessionChecks' { $pattern += 'Runtime session isolation and shutdown checks passed\.' }
                    'AnalyticsBridgeChecks' { $arguments += @('--', '--web'); $pattern += 'analytics bridge and scoped-history checks passed\.' }
                    'WorkplaceBridgeChecks' { $pattern += 'Workplace bridge and WebView checks passed\.' }
                    'TerrainBridgeChecks' { $pattern += 'terrain bridge checks passed\.' }
                }
                Invoke-CheckedProcess -Executable 'dotnet' -Arguments $arguments -LogName ($suite.ToLowerInvariant() + '.log') -TimeoutSeconds 240 -ExpectedPattern $pattern
            }
            $driver = Join-Path $repository 'tests/TerrainBridgeChecks/bin/Release/net10.0/.playwright'
            $env:GLPI_TEST_PLAYWRIGHT_MODULE = Join-Path $driver 'package'
            Invoke-CheckedProcess -Executable (Join-Path $driver 'node/win32_x64/node.exe') -Arguments @('tests/TerrainWebChecks/run.cjs') `
                -LogName 'terrain-webview-checks.log' -TimeoutSeconds 180 -ExpectedSummary '18/18 terrain WebView page checks passed.'
        }
        'BuildPack' {
            $release = Join-Path $OutputDirectory 'release'
            . (Join-Path $PSScriptRoot 'SourceProvenance.ps1')
            $buildProvenance = Resolve-GlpiBuildProvenance -SourceRoot $repository
            $dotnetPath = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
            $runtimePath = & (Join-Path $PSScriptRoot 'Get-WebView2Runtime.ps1') -CacheDirectory (Join-Path $env:RUNNER_TEMP 'GLPI-WebView2-Cache')
            Invoke-CheckedProcess -Executable (Get-Process -Id $PID).Path -Arguments @('-NoProfile', '-NonInteractive', '-File', (Join-Path $repository 'tests/RuntimePackagingChecks/Program.ps1'), '-RuntimePath', $runtimePath) -LogName 'runtime-packaging-checks.log' -TimeoutSeconds 180 -ExpectedSummary '14/14 runtime packaging checks passed; offline only, no runtime launched.'
            if (-not $runtimePath -or -not (Test-Path -LiteralPath (Join-Path $runtimePath 'msedgewebview2.exe'))) { throw 'Verified fixed WebView2 runtime unavailable.' }
            Invoke-CheckedProcess -Executable (Get-Process -Id $PID).Path -Arguments @('-NoProfile', '-NonInteractive', '-File', (Join-Path $repository 'build.ps1'), '-OutputDirectory', $release, '-ValidationDirectory', $logs, '-RuntimePath', $runtimePath, '-DotNetPath', $dotnetPath, '-AllowOnlineRestore') `
                -LogName 'pack-build.log' -TimeoutSeconds 900
            $exeHashes = @{}; $hashLines = [Collections.Generic.List[string]]::new()
            foreach ($edition in @('PROD', 'DEV')) {
                $pack = Get-Pack -Edition $edition
                $exeHashes[$edition] = Assert-PackContents $pack -Edition $edition
                $digest = (Get-FileHash -LiteralPath $pack.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                $hashLines.Add("$digest  $($pack.Name)")
                if ($env:GITHUB_OUTPUT) {
                    "$($edition.ToLowerInvariant())-pack-path=$($pack.FullName)" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
                }
                Write-Host "Complete $edition Windows pack: $($pack.Name); SHA-256: $digest"
            }
            if ($exeHashes['PROD'] -ne $exeHashes['DEV']) { throw 'PROD and DEV do not contain the same executable.' }
            [IO.File]::WriteAllText((Join-Path $logs 'pack-sha256.txt'), (($hashLines -join "`n") + "`n"), $utf8)
            [IO.File]::WriteAllText((Join-Path $logs 'executable-sha256.txt'), ($exeHashes['PROD'] + "  GLPI-Pilot.exe`n"), $utf8)
            Invoke-CheckedProcess -Executable (Get-Process -Id $PID).Path -Arguments @('-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot 'Test-DevRebuild.ps1'), '-DevPack', (Get-Pack -Edition DEV).FullName, '-RuntimePath', $runtimePath, '-DotNetPath', $dotnetPath, '-ExpectedCommit', $buildProvenance.Commit) `
                -LogName 'dev-rebuild-checks.log' -TimeoutSeconds 1200 -ExpectedSummary '5/5 DEV rebuild checks passed; exported sources, isolated offline restore, no rebuilt pack published.'
        }
        'PackStartup' { Test-PackStartup }
        'UpdateStartup' {
            $harness = Join-Path $OutputDirectory 'update-harness'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('publish', 'tests/WindowsUpdateChecks/WindowsUpdateChecks.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-o', $harness) `
                -LogName 'windows-update-build.log' -TimeoutSeconds 420
            $pack = Get-Pack -Edition 'PROD'
            Invoke-CheckedProcess -Executable (Join-Path $harness 'WindowsUpdateChecks.exe') -Arguments @($pack.FullName, (Join-Path $logs 'windows-update')) `
                -LogName 'windows-update-checks.log' -TimeoutSeconds 960 -ExpectedSummary '4/4 Windows update checks passed.'
        }
    }
}
finally { Pop-Location }
