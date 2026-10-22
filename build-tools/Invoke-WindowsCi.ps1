#requires -Version 7.2
<#
.SYNOPSIS
Builds and runs the Windows CI stages without modifying production app code.
.DESCRIPTION
The WPF harness runs real controls and has an external 90-second process deadline.
BrowserChecks includes the ticket-detail reader against intercepted synthetic pages,
with the same isolated test browser and an externally bounded process as the other browser suites.
PackStartup is restricted to an interactive GitHub-hosted Windows runner. It refuses
any existing GLPI-Pilot state, redirects only that absent app directory to a unique
temporary sandbox through a junction, and never changes Windows known-folder settings.
Only synthetic settings are used. No extraction, browser launch or SSO is requested.
This script does not imply that Windows checks passed until each executable succeeds.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('BclChecks', 'BrowserChecks', 'WpfChecks', 'BuildPack', 'PackStartup', 'UpdateStartup')]
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
            'wad/version.json', 'wad/SHA256SUMS.txt'
        )
        if ($Edition -eq 'DEV') {
            $required += @(
                'wad/sources/build.ps1', 'wad/sources/README.md',
                'wad/sources/src/GlpiPilot/GlpiPilot.csproj',
                'wad/sources/tests/WindowsStartupChecks/Program.cs',
                'wad/sources/build-tools/Invoke-WindowsCi.ps1',
                'wad/sources/.github/workflows/windows.yml'
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

function Test-PackStartup {
    if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows') {
        throw 'PackStartup only runs in a disposable GitHub-hosted Windows job; no local user profile is modified.'
    }
    if (-not [Environment]::UserInteractive -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0) {
        throw 'NOT RUN: the full pack test requires an interactive Windows desktop, not a service/session-0 process.'
    }
    if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { throw 'The runner temporary directory is unavailable.' }
    if (Get-Process -Name 'GLPI-Pilot' -ErrorAction SilentlyContinue) { throw 'An existing GLPI-Pilot process prevents an isolated startup test.' }
    $localData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if (-not [IO.Directory]::Exists($localData)) { throw 'The Windows LocalApplicationData known folder is unavailable.' }
    $stateLink = Join-Path $localData 'GLPI-Pilot'
    # Enumerating the parent also catches a pre-existing dangling junction.
    if (Get-ChildItem -LiteralPath $localData -Force | Where-Object Name -EQ 'GLPI-Pilot') {
        throw 'Existing GLPI-Pilot application state detected. It will not be moved, read or overwritten.'
    }
    $sandbox = Join-Path $env:RUNNER_TEMP ('glpi-pilot-pack-check-' + [guid]::NewGuid().ToString('N'))
    $state = Join-Path $sandbox 'state'
    $temporary = Join-Path $sandbox 'temp'
    $extract = Join-Path $sandbox 'pack'
    $packLogs = Join-Path $logs 'pack-startup'
    New-Item -ItemType Directory -Path $state, $temporary, $extract, $packLogs -Force | Out-Null
    $messages = [Collections.Generic.List[string]]::new()
    $process = $null; $secondary = $null; $linkCreated = $false; $passed = $false
    try {
        $pack = Get-Pack
        [void](Assert-PackContents $pack -Edition PROD)
        [IO.Compression.ZipFile]::ExtractToDirectory($pack.FullName, $extract)
        $executable = Join-Path $extract 'GLPI-Pilot.exe'
        # Explicit synthetic state; the app must preserve the exact file on startup.
        $settings = Join-Path $state 'settings.json'
        @{
            PortalUrl = 'https://glpi.example.invalid/front/ticket.php'
            Theme = 'dark'
            ExportFolder = (Join-Path $sandbox 'exports')
            Dashboard = @{ PersonalTechnician = 'alex exemple' }
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $settings -Encoding utf8
        $settingsHash = (Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash
        New-Item -ItemType Junction -Path $stateLink -Target $state | Out-Null
        $linkCreated = $true
        $target = [IO.DirectoryInfo]::new($stateLink).ResolveLinkTarget($true)
        if ($null -eq $target -or $target.FullName -ne [IO.Path]::GetFullPath($state)) { throw 'The temporary application-state junction could not be verified.' }
        $messages.Add('Synthetic state redirected to a unique runner temporary sandbox; no previous application state existed.')

        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class GlpiPilotCiWindow {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShowWindow(IntPtr window, int command);
}
'@
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $executable
        $start.WorkingDirectory = Split-Path -Parent $executable
        $start.UseShellExecute = $false
        # Process-only paths also isolate fallback logs and single-file extraction.
        $start.Environment['TEMP'] = $temporary
        $start.Environment['TMP'] = $temporary
        $start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $sandbox 'bundle'
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $start
        $clock = [Diagnostics.Stopwatch]::StartNew()
        if (-not $process.Start()) { throw 'The packaged executable did not start.' }
        $ready = $false
        $startupLog = Join-Path $state 'Logs/demarrage-dernier.log'
        while ($clock.Elapsed.TotalSeconds -lt 90) {
            $process.Refresh()
            if ($process.HasExited) { throw "The pack exited before its first render (code $($process.ExitCode))." }
            $text = ''
            if (Test-Path -LiteralPath $startupLog) {
                try { $text = [IO.File]::ReadAllText($startupLog) }
                catch [IO.IOException] { } # The app can be replacing its bounded latest-copy file.
            }
            if ($text.Contains('[ÉCHEC]', [StringComparison]::Ordinal)) { throw 'The packaged app reported a startup failure; see the attached startup log.' }
            $window = $process.MainWindowHandle
            if ($text.Contains('[PRÊT]', [StringComparison]::Ordinal) -and $window -ne [IntPtr]::Zero -and $process.MainWindowTitle -eq 'GLPI Pilot') {
                $rectangle = [GlpiPilotCiWindow+Rect]::new()
                [uint32]$windowProcess = 0
                [void][GlpiPilotCiWindow]::GetWindowThreadProcessId($window, [ref]$windowProcess)
                $ready = [GlpiPilotCiWindow]::IsWindowVisible($window) -and
                    [GlpiPilotCiWindow]::GetWindowRect($window, [ref]$rectangle) -and
                    $windowProcess -eq $process.Id -and $rectangle.Right -gt $rectangle.Left -and $rectangle.Bottom -gt $rectangle.Top
                if ($ready) { break }
            }
            Start-Sleep -Milliseconds 250
        }
        if (-not $ready) { throw 'Pack startup exceeded 90 seconds without both ContentRendered evidence and a visible main window.' }
        $messages.Add('PASS: the extracted standalone executable reached ContentRendered and owns a visible nonzero main window.')
        [void][GlpiPilotCiWindow]::ShowWindow($process.MainWindowHandle, 6)
        if (-not [GlpiPilotCiWindow]::IsIconic($process.MainWindowHandle)) { throw 'The primary window could not be minimized for the second-launch check.' }
        $secondary = [Diagnostics.Process]::new(); $secondary.StartInfo = $start
        if (-not $secondary.Start() -or -not $secondary.WaitForExit(10000) -or $secondary.ExitCode -ne 0) {
            throw 'A second launch did not signal the primary instance and close normally within ten seconds.'
        }
        $secondary.Dispose(); $secondary = $null
        $activationClock = [Diagnostics.Stopwatch]::StartNew()
        while ([GlpiPilotCiWindow]::IsIconic($process.MainWindowHandle) -and $activationClock.Elapsed.TotalSeconds -lt 5) { Start-Sleep -Milliseconds 100 }
        $process.Refresh()
        if ($process.HasExited -or [GlpiPilotCiWindow]::IsIconic($process.MainWindowHandle) -or -not [GlpiPilotCiWindow]::IsWindowVisible($process.MainWindowHandle)) {
            throw 'The first instance did not restore its visible window after the explicit second launch.'
        }
        $messages.Add('PASS: a second launch exits normally and restores the existing minimized window through same-user IPC.')
        if (-not $process.CloseMainWindow()) { throw 'The visible main window did not accept a normal close request.' }
        $remaining = [Math]::Min(10000, [Math]::Max(0, [int](90000 - $clock.Elapsed.TotalMilliseconds)))
        if (-not $process.WaitForExit($remaining)) { throw 'The packaged app did not close within the startup-test deadline.' }
        if ($process.ExitCode -ne 0) { throw "The packaged app closed with code $($process.ExitCode)." }
        if ((Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash -ne $settingsHash) { throw 'The synthetic settings file changed during startup or shutdown.' }
        $messages.Add('PASS: normal shutdown, exit code 0, exact synthetic settings preserved. No browser or SSO interaction requested.')

        # User-initiated zoom must persist across two real EXE launches. Keep the
        # no-interaction startup byte-preservation check above independent.
        $process.Dispose(); $process = $null
        $harness = Join-Path $OutputDirectory 'wpf-harness/WindowsStartupChecks.exe'
        if (-not (Test-Path -LiteralPath $harness -PathType Leaf)) { throw 'The already validated Windows UI harness is unavailable.' }
        foreach ($mode in @('exercise', 'verify-restore')) {
            if (Test-Path -LiteralPath $startupLog) { Remove-Item -LiteralPath $startupLog -Force }
            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $start
            $clock.Restart()
            if (-not $process.Start()) { throw "The packaged executable did not start for zoom $mode." }
            $ready = $false
            while ($clock.Elapsed.TotalSeconds -lt 90) {
                $process.Refresh()
                if ($process.HasExited) { throw "The pack exited before zoom $mode (code $($process.ExitCode))." }
                $text = ''
                if (Test-Path -LiteralPath $startupLog) {
                    try { $text = [IO.File]::ReadAllText($startupLog) }
                    catch [IO.IOException] { }
                }
                if ($text.Contains('[ÉCHEC]', [StringComparison]::Ordinal)) { throw "The packaged app reported a startup failure for zoom $mode." }
                if ($text.Contains('[PRÊT]', [StringComparison]::Ordinal) -and $process.MainWindowHandle -ne [IntPtr]::Zero -and
                    $process.MainWindowTitle -eq 'GLPI Pilot' -and [GlpiPilotCiWindow]::IsWindowVisible($process.MainWindowHandle)) {
                    $ready = $true; break
                }
                Start-Sleep -Milliseconds 250
            }
            if (-not $ready) { throw "The pack did not render within 90 seconds for zoom $mode." }
            $summary = if ($mode -eq 'exercise') { 'PASS: packaged window zoom exercise completed;' }
                else { 'PASS: packaged window zoom persisted at 110 % after restart and reset to 100 % through the real button.' }
            Invoke-CheckedProcess -Executable $harness -Arguments @('--inspect-pack-window', $process.Id.ToString(), $mode) `
                -LogName ("pack-zoom-$mode.log") -TimeoutSeconds 60 -ExpectedSummary $summary
            if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(10000)) { throw "Normal shutdown after zoom $mode did not finish within 10 seconds." }
            if ($process.ExitCode -ne 0) { throw "The packaged app closed with code $($process.ExitCode) after zoom $mode." }
            $saved = [IO.File]::ReadAllText($settings) | ConvertFrom-Json
            $expectedZoom = if ($mode -eq 'exercise') { 110 } else { 100 }
            if ($saved.InterfaceZoomPercent -ne $expectedZoom -or $saved.PortalUrl -ne 'https://glpi.example.invalid/front/ticket.php' -or
                $saved.Theme -ne 'dark' -or $saved.ExportFolder -ne (Join-Path $sandbox 'exports') -or $saved.Dashboard.PersonalTechnician -ne 'alex exemple') {
                throw "Zoom $mode did not preserve the synthetic source, theme, export folder, personal identity and expected scale."
            }
            $messages.Add("PASS: real packaged-window zoom $mode; saved zoom $expectedZoom %, other synthetic preferences preserved, normal shutdown.")
            $process.Dispose(); $process = $null
        }
        $passed = $true
    }
    catch {
        $messages.Add('FAIL: ' + $_.Exception.Message)
        throw
    }
    finally {
        try {
            if ($secondary) {
                try { if (-not $secondary.HasExited) { $secondary.Kill($true); [void]$secondary.WaitForExit(5000) } }
                finally { $secondary.Dispose() }
            }
            if ($process) {
                try { if (-not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) } }
                finally { $process.Dispose() }
            }
            foreach ($directory in @((Join-Path $state 'Logs'), (Join-Path $temporary 'GLPI-Pilot/Logs'))) {
                if (Test-Path -LiteralPath $directory) {
                    Get-ChildItem -LiteralPath $directory -Filter '*.log' -File | Copy-Item -Destination $packLogs -Force
                }
            }
            [IO.File]::WriteAllLines((Join-Path $logs 'pack-startup.log'), $messages, $utf8)
            foreach ($message in $messages) { Write-Host $message }
        }
        finally {
            if ($linkCreated) {
                $target = [IO.DirectoryInfo]::new($stateLink).ResolveLinkTarget($true)
                if ($null -eq $target -or $target.FullName -ne [IO.Path]::GetFullPath($state)) { throw 'Unexpected junction target during cleanup; application state was left untouched.' }
                # Delete the junction itself, never recursively delete anything in a user profile.
                [IO.Directory]::Delete($stateLink, $false)
            }
            Remove-Item -LiteralPath $sandbox -Recurse -Force
        }
    }
    if (-not $passed) { throw 'Pack startup did not complete.' }
}

Push-Location $repository
try {
    switch ($Stage) {
        'BclChecks' {
            $suites = @(
                @{ Project = 'StartupDiagnosticsChecks'; Log = 'startup-diagnostics-checks.log'; Summary = 'Startup diagnostics: 14 passed, 0 failed.' },
                @{ Project = 'StartupResilienceChecks'; Log = 'startup-resilience-checks.log'; Summary = 'startup resilience checks passed.' },
                @{ Project = 'FoundationChecks'; Log = 'foundation-checks.log'; Summary = ''; Pattern = '\d+/\d+ Foundation checks passed\.' },
                @{ Project = 'WorkplaceChecks'; Log = 'workplace-checks.log'; Summary = 'workplace checks passed.' },
                @{ Project = 'ConfigurationChecks'; Log = 'configuration-checks.log'; Summary = '39/39 vérifications de configuration réussies.' },
                @{ Project = 'DashboardChecks'; Log = 'dashboard-checks.log'; Summary = '112/112' },
                @{ Project = 'DashboardLayoutChecks'; Log = 'dashboard-layout-checks.log'; Summary = '29/29 checks passed.' },
                @{ Project = 'PreviewQueryChecks'; Log = 'preview-query-checks.log'; Summary = '44/44' },
                @{ Project = 'TechnicianWorkloadChecks'; Log = 'technician-workload-checks.log'; Summary = '35/35' },
                @{ Project = 'CityDetectionChecks'; Log = 'city-detection-checks.log'; Summary = '91/91' },
                @{ Project = 'TourPlanningChecks'; Log = 'tour-planning-checks.log'; Summary = '87/87' },
                @{ Project = 'QueryChecks'; Log = 'query-checks.log'; Summary = '99/99 vérifications réussies.' },
                @{ Project = 'AnalyticsChecks'; Log = 'analytics-checks.log'; Summary = '32/32 vérifications réussies.' },
                @{ Project = 'CompletionChecks'; Log = 'completion-checks.log'; Summary = '44/44 Completion checks passed.' },
                @{ Project = 'UpdateChecks'; Log = 'update-checks.log'; Summary = '24/24 update checks passed.' },
                @{ Project = 'LanguageAssistantChecks'; Log = 'language-assistant-checks.log'; Summary = ''; Pattern = '\d+/\d+ language assistant checks passed; synthetic handlers only, no provider contacted\.' },
                @{ Project = 'NativeImportChecks'; Log = 'native-import-checks.log'; Summary = '38/38' },
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
            $browserCandidates = @(
                (Join-Path ${env:ProgramFiles(x86)} 'Microsoft/Edge/Application/msedge.exe'),
                (Join-Path $env:ProgramFiles 'Microsoft/Edge/Application/msedge.exe'),
                (Join-Path $env:ProgramFiles 'Google/Chrome/Application/chrome.exe')
            )
            $browser = $browserCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
            if (-not $browser) { throw 'No supported Chromium browser is available for the isolated synthetic checks.' }
            $env:GLPI_TEST_CHROMIUM = $browser
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
                -LogName 'native-dom-checks.log' -TimeoutSeconds 120 -ExpectedSummary '82 native export DOM checks passed.'
        }
        'WpfChecks' {
            $harness = Join-Path $OutputDirectory 'wpf-harness'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('publish', 'tests/WindowsStartupChecks/WindowsStartupChecks.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-o', $harness) `
                -LogName 'windows-startup-build.log' -TimeoutSeconds 420
            Invoke-CheckedProcess -Executable (Join-Path $harness 'WindowsStartupChecks.exe') `
                -Arguments @('--screenshots', (Join-Path $logs 'ui-previews')) `
                -LogName 'windows-startup-checks.log' -TimeoutSeconds 90 -ExpectedSummary 'real Windows UI checks completed; no browser or user settings accessed.'
        }
        'BuildPack' {
            $release = Join-Path $OutputDirectory 'release'
            Invoke-CheckedProcess -Executable (Get-Process -Id $PID).Path -Arguments @('-NoProfile', '-NonInteractive', '-File', (Join-Path $repository 'build.ps1'), '-OutputDirectory', $release, '-ValidationDirectory', $logs) `
                -LogName 'pack-build.log' -TimeoutSeconds 660
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
        }
        'PackStartup' { Test-PackStartup }
        'UpdateStartup' {
            $harness = Join-Path $OutputDirectory 'update-harness'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('publish', 'tests/WindowsUpdateChecks/WindowsUpdateChecks.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-o', $harness) `
                -LogName 'windows-update-build.log' -TimeoutSeconds 420
            $pack = Get-Pack -Edition 'PROD'
            Invoke-CheckedProcess -Executable (Join-Path $harness 'WindowsUpdateChecks.exe') -Arguments @($pack.FullName, (Join-Path $logs 'windows-update')) `
                -LogName 'windows-update-checks.log' -TimeoutSeconds 300 -ExpectedSummary '4/4 Windows update checks passed.'
        }
    }
}
finally { Pop-Location }
