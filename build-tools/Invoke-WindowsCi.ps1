#requires -Version 7.2
<#
.SYNOPSIS
Builds and runs the Windows CI stages without modifying production app code.
.DESCRIPTION
Portable suites exercise the real C# engines and local ES modules on synthetic data.
PackStartup then drives the actual packaged WinForms/WebView2 host, with its fixed
runtime, through a loopback-only test endpoint available only in a guarded temporary
profile. UpdateStartup exercises replacement and rollback on disposable copies of the
published PROD-LIGHT pack. BuildPack -IncludeDevRebuild (manual DEV rebuild workflow,
outside the release gate) also rebuilds the exported DEV sources outside Git with an
empty child NuGet cache and a local feed, then verifies the rebuilt PROD through UpdateChecks.
Neither native harness reads an existing application profile or signs in to GLPI.
All child processes have an external deadline and must report a successful summary.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('BclChecks', 'BrowserChecks', 'LibraryAcquisitionChecks', 'WebViewChecks', 'BuildPack', 'PackStartup', 'PreparePackStartup', 'PackStartupProd', 'PackStartupLight', 'UpdateStartup')]
    [string]$Stage,
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/windows-ci'),
    # La reconstruction DEV (environ 10 min et 9 Go écrits) n'est plus dans la qualification : elle prouve que les
    # sources exportées se reconstruisent, pas que le pack publié démarre. Le workflow manuel dev-rebuild.yml la lance.
    [switch]$IncludeDevRebuild
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
        # Un processus enfant peut heriter du tuyau stdout et le maintenir ouvert apres la sortie du processus
        # surveille : ReadToEndAsync reste en attente alors que la suite a deja rendu son verdict. Cinq secondes
        # etaient trop justes pour les suites qui demarrent un vrai navigateur et faisaient echouer la CI au hasard.
        $drainGraceSeconds = 60
        if (-not $stdout.Wait($drainGraceSeconds * 1000) -or -not $stderr.Wait($drainGraceSeconds * 1000)) {
            throw "Output capture did not finish for $LogName within $drainGraceSeconds seconds after the process exited with code $($process.ExitCode)."
        }
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
    param([ValidateSet('PROD', 'PROD-LIGHT', 'DEV')][string]$Edition = 'PROD')
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
    param([IO.FileInfo]$Pack, [ValidateSet('PROD', 'PROD-LIGHT', 'DEV')][string]$Edition = 'PROD')
    $archive = [IO.Compression.ZipFile]::OpenRead($Pack.FullName)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        $top = @($entries | ForEach-Object { ($_ -split '/')[0] } | Sort-Object -Unique)
        if ($top.Count -ne 3 -or $top -notcontains 'GLPI-Pilot.exe' -or $top -notcontains 'GLPI-Pilot-Admin.exe' -or $top -notcontains 'wad') {
            throw "$Edition ZIP must contain only GLPI-Pilot.exe, GLPI-Pilot-Admin.exe and wad at its root."
        }
        $required = @(
            'GLPI-Pilot.exe', 'GLPI-Pilot-Admin.exe', 'wad/.playwright/node/win32_x64/node.exe',
            'wad/.playwright/package/cli.js', 'wad/docs/Guide-GLPI-Pilot.html',
            'wad/docs/Bibliotheque-et-partage.md', 'wad/docs/RELEASE-3.2.0.md', 'wad/docs/RELEASE-3.2.1.md', 'wad/docs/RELEASE-3.2.2.md', 'wad/docs/RELEASE-3.3.0.md', 'wad/docs/RELEASE-3.3.1.md', 'wad/docs/RELEASE-3.3.2.md', 'wad/docs/RELEASE-3.3.3.md', 'wad/docs/RELEASE-3.3.4.md',
            'wad/support/DEBLOQUER_TOUT.cmd', 'wad/support/DIAGNOSTIC_DEMARRAGE.cmd', 'wad/licenses/COMPOSANTS.txt',
            'wad/version.json', 'wad/SHA256SUMS.txt',
            'wad/web/index.html', 'wad/web/js/app.js', 'wad/web/js/bridge.js', 'wad/web/css/app.css',
            'wad/licenses/WebView2-SDK-LICENSE.txt', 'wad/licenses/WebView2-FixedRuntime-LICENSE.html',
            'wad/licenses/Sqlite-Npgsql-NuGet-Notices.txt', 'wad/licenses/Microsoft-Data-Sqlite-MIT.txt',
            'wad/licenses/Npgsql-PostgreSQL.txt', 'wad/licenses/SQLitePCLRaw-Apache-2.0.txt',
            'wad/licenses/Microsoft-Extensions-ThirdPartyNotices.txt',
            'wad/web/js/pages/library.js', 'wad/web/css/library.css',
            'wad/web/js/pages/terrain-update-shared.js',
            'wad/web/js/navigation.js', 'wad/web/css/navigation.css',
            'wad/web/js/domenia-motion.js', 'wad/web/css/domenia.css',
            'wad/web/js/workspace-panels.js', 'wad/web/css/workspaces.css', 'wad/web/css/home.css', 'wad/web/css/tickets.css',
            'wad/web/js/pages/home-workspace.js', 'wad/web/js/pages/pilot-workspace.js', 'wad/web/js/pages/data-workspace.js',
            'wad/web/js/pages/settings-workspace.js', 'wad/web/js/pages/tickets-workspace.js', 'wad/web/js/pages/terrain-map.js',
            'wad/web/js/pages/workspace-grid.js', 'wad/web/js/pages/workspace-grid-ui.js', 'wad/web/css/workspace-grid.css',
            'wad/web/js/pages/workspace-widget-editor.js', 'wad/web/js/pages/weather-scene.js', 'wad/docs/RELEASE-3.4.0.md',
            'wad/web/js/team-scope.js', 'wad/docs/RELEASE-3.4.1.md',
            'wad/docs/Calendrier-et-interventions.md', 'wad/docs/RELEASE-3.5.0.md',
            'wad/web/css/calendar.css', 'wad/web/css/work-context.css',
            'wad/web/css/identity.css', 'wad/web/js/identity-picker.js', 'wad/docs/RELEASE-3.6.0.md', 'wad/docs/RELEASE-3.7.0.md',
            'wad/web/js/source-cache.js', 'wad/web/js/pages/pilot-presentation.js', 'wad/web/css/presentation.css',
            'wad/docs/Reunions-et-hors-connexion.txt',
            'wad/web/js/pages/settings-deployment.js', 'wad/web/css/deployment.css', 'wad/docs/Configuration-deploiement.txt', 'wad/docs/RELEASE-3.8.0.md', 'wad/docs/RELEASE-3.8.1.md',
            'wad/web/js/pages/weather-selection-editor.js', 'wad/docs/Seuils-meteo.txt', 'wad/docs/RELEASE-3.9.0.md',
            'wad/web/js/pages/presentation-editor.js', 'wad/web/js/pages/presentation-layout.js',
            'wad/web/js/pages/terrain-correction.js', 'wad/docs/Presentation.md', 'wad/docs/RELEASE-3.10.0.md',
            'wad/web/js/grouped-collection.js', 'wad/docs/RELEASE-3.10.1.md', 'wad/docs/RELEASE-3.10.2.md',
            'wad/web/css/motion-controls.css', 'wad/web/js/motion-controls.js',
            'wad/web/js/calendar-layout.js', 'wad/web/js/work-context.js', 'wad/web/js/workspace-scope.js',
            'wad/web/js/shared-records.js', 'wad/web/js/pages/shared-business.js', 'wad/web/js/pages/interventions-workspace.js',
            'wad/web/js/pages/shared-operations.js',
            'wad/web/js/command-palette.js', 'wad/web/js/library-ticket.js', 'wad/web/css/command-palette.css',
            'wad/web/js/library-resolution.js', 'wad/web/css/library-resolution.css',
            'wad/web/js/local-file-test.js', 'wad/web/js/pages/analytics-business.js',
            'wad/web/js/pages/maps.js', 'wad/web/css/maps.css',
            'wad/web/vendor/leaflet/leaflet.js', 'wad/web/vendor/leaflet/leaflet.css',
            'wad/web/vendor/leaflet/images/layers.png', 'wad/web/vendor/leaflet/images/layers-2x.png',
            'wad/web/vendor/leaflet/images/marker-icon.png', 'wad/web/vendor/leaflet/images/marker-icon-2x.png',
            'wad/web/vendor/leaflet/images/marker-shadow.png', 'wad/web/vendor/natural-earth/land.js',
            'wad/licenses/Leaflet-LICENSE.txt', 'wad/licenses/Embedded-Maps-Notices.txt', 'wad/licenses/NaturalEarth-PUBLIC-DOMAIN.txt'
        )
        if ($Edition -eq 'DEV') {
            $required += @(
                'wad/sources/build.ps1', 'wad/sources/README.md', 'wad/sources/.gitattributes',
                'wad/sources/src/GlpiPilot/GlpiPilot.csproj',
                'wad/sources/src/GlpiPilot/Core/BusinessDashboardWorkspace.cs',
                'wad/sources/src/GlpiPilot/Core/AdministrationCapabilities.cs',
                'wad/sources/src/GlpiPilot/Core/UpdateService.SharedFolder.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.SitePatches.cs',
                'wad/sources/tests/AdministrationChecks/AdministrationChecks.csproj',
                'wad/sources/tests/WebViewChecks/Program.SharedUpdates.cs',
                'wad/sources/web/js/pages/terrain-update-shared.js',
                'wad/sources/src/GlpiPilot/Core/BusinessWeatherScope.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.Analytics.Weather.cs',
                'wad/sources/tests/AnalyticsBridgeChecks/Program.MapWorkspace.cs',
                'wad/sources/tests/AnalyticsBridgeChecks/Program.WeatherScope.cs',
                'wad/sources/tests/DashboardChecks/WorkspaceChecks.cs',
                'wad/sources/tests/DashboardChecks/WeatherScopeChecks.cs',
                'wad/sources/tests/WebViewChecks/Program.Motion.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.MapWorkspace.cs',
                'wad/sources/tests/WindowsStartupChecks/Program.cs',
                'wad/sources/build-tools/Invoke-WindowsCi.ps1', 'wad/sources/web/index.html',
                'wad/sources/src/GlpiPilot/Desktop/MainForm.cs', 'wad/sources/src/GlpiPilot/Core/SiteReferenceInitializer.cs', 'wad/sources/build-tools/WebView2Runtime.json',
                'wad/sources/.github/workflows/windows.yml', 'wad/sources/GLPI-Pilot.sources.json',
                'wad/sources/build-tools/SourceProvenance.ps1', 'wad/sources/build-tools/Test-DevRebuild.ps1',
                'wad/sources/tests/SourceProvenanceChecks/Program.ps1',
                'wad/sources/tests/UpdateChecks/Program.cs', 'wad/sources/tests/UpdateChecks/UpdateChecks.csproj',
                'wad/sources/src/GlpiPilot/Core/BrowserTicketLibrary.cs',
                'wad/sources/src/GlpiPilot/Core/TicketLibraryStore.cs',
                'wad/sources/src/GlpiPilot/Core/TeamWorkspaceSyncService.cs',
                'wad/sources/src/GlpiPilot/Core/TeamWorkspaceBusinessDocuments.cs',
                'wad/sources/src/GlpiPilot/Core/InterventionCalendarModels.cs',
                'wad/sources/src/GlpiPilot/Core/InterventionCalendarService.cs',
                'wad/sources/src/GlpiPilot/Core/InterventionCalendarConstraints.cs',
                'wad/sources/src/GlpiPilot/Core/InterventionCalendarExport.cs',
                'wad/sources/src/GlpiPilot/Core/CalendarWorkspaceDocuments.cs',
                'wad/sources/src/GlpiPilot/Core/TeamWorkspaceStore.Calendar.cs',
                'wad/sources/src/GlpiPilot/Core/TeamWorkspaceStore.CalendarSync.cs',
                'wad/sources/src/GlpiPilot/Core/TeamWorkspaceCalendarProtocol.cs',
                'wad/sources/src/GlpiPilot/Core/InMemoryTeamWorkspaceReplica.cs',
                'wad/sources/src/GlpiPilot/Core/SharedFolderWorkspace.cs',
                'wad/sources/src/GlpiPilot/Core/FolderTeamWorkspaceReplica.cs',
                'wad/sources/src/GlpiPilot/Core/FolderTicketLibraryReplica.cs',
                'wad/sources/src/GlpiPilot/Core/LocalFolderReplicaIndex.cs',
                'wad/sources/src/GlpiPilot/Core/LocalFolderReplicaIndex.Team.cs',
                'wad/sources/src/GlpiPilot/Core/LocalFolderReplicaIndex.Tickets.cs',
                'wad/sources/src/GlpiPilot/Core/SharedFolderSerialization.cs',
                'wad/sources/src/GlpiPilot/Core/SharedBusinessDocuments.cs',
                'wad/sources/src/GlpiPilot/Core/TeamWorkspaceStore.Business.cs',
                'wad/sources/src/GlpiPilot/Core/TeamWorkspaceStore.BusinessStaging.cs',
                'wad/sources/src/GlpiPilot/Core/PreviewQueryCache.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.Calendar.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.Identity.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.Calendar.Catalog.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.Calendar.Mutations.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.Calendar.Tours.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.Calendar.Conflicts.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.LibrarySharing.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.SharedBusiness.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.WorkOverview.cs',
                'wad/sources/tests/CalendarChecks/CalendarChecks.csproj', 'wad/sources/tests/CalendarChecks/Program.cs',
                'wad/sources/tests/CalendarBridgeChecks/CalendarBridgeChecks.csproj', 'wad/sources/tests/CalendarBridgeChecks/Program.cs',
                'wad/sources/tests/CalendarBridgeChecks/Fixture.cs', 'wad/sources/tests/CalendarBridgeChecks/SharingChecks.cs',
                'wad/sources/tests/CalendarBridgeChecks/IdentityChecks.cs', 'wad/sources/tests/WebViewChecks/Program.Identity.cs',
                'wad/sources/tests/WebViewChecks/Program.MotionVisibility.cs',
                'wad/sources/tests/SharedFolderChecks/SharedFolderChecks.csproj', 'wad/sources/tests/SharedFolderChecks/Program.cs',
                'wad/sources/tests/SharedBusinessChecks/SharedBusinessChecks.csproj', 'wad/sources/tests/SharedBusinessChecks/Program.cs',
                'wad/sources/tests/SharedWorkspaceChecks/Program.Calendar.cs', 'wad/sources/tests/SharedWorkspaceChecks/Program.BusinessStaging.cs',
                'wad/sources/tests/WebViewChecks/Program.Calendar.cs',
                'wad/sources/tests/RuntimeSessionChecks/WorkspaceOverviewChecks.cs',
                'wad/sources/web/css/calendar.css', 'wad/sources/web/css/work-context.css',
                'wad/sources/web/css/identity.css', 'wad/sources/web/js/identity-picker.js', 'wad/sources/docs/RELEASE-3.6.0.md', 'wad/sources/docs/RELEASE-3.7.0.md',
                'wad/sources/src/GlpiPilot/Core/LastSourceCacheStore.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.SourceCache.cs',
                'wad/sources/tests/WebViewChecks/Program.SourceCache.cs',
                'wad/sources/tests/WebViewChecks/Program.Presentation.cs',
                'wad/sources/tests/WebViewChecks/Program.LocationCorrection.cs',
                'wad/sources/tests/TerrainBridgeChecks/TerrainCorrectionChecks.cs',
                'wad/sources/tests/TerrainBridgeChecks/TerrainPreviewValidityChecks.cs',
                'wad/sources/tests/AnalyticsBridgeChecks/Program.Presentation.cs',
                'wad/sources/tests/WindowsStartupChecks/Program.Presentation.cs',
                'wad/sources/src/GlpiPilot/Core/DashboardLayout.Presentation.cs',
                'wad/sources/src/GlpiPilot/Core/ReportService.Presentation.cs',
                'wad/sources/src/GlpiPilot/Core/CityCorrectionProjection.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.Analytics.Presentation.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.TerrainCorrections.cs',
                'wad/sources/web/js/pages/presentation-editor.js', 'wad/sources/web/js/pages/presentation-layout.js',
                'wad/sources/web/js/pages/terrain-correction.js', 'wad/sources/docs/Presentation.md', 'wad/sources/docs/RELEASE-3.10.0.md',
                'wad/sources/web/js/grouped-collection.js', 'wad/sources/tests/WebViewChecks/Program.DataSourceWorkflow.cs', 'wad/sources/docs/RELEASE-3.10.1.md',
                'wad/sources/tests/WebViewChecks/Program.WorkspaceRefresh.cs', 'wad/sources/docs/RELEASE-3.10.2.md',
                'wad/sources/tests/RuntimeSessionChecks/SourceCacheChecks.cs',
                'wad/sources/tests/AnalyticsBridgeChecks/Program.MeetingExports.cs',
                'wad/sources/src/GlpiPilot/Core/ReportService.Selection.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.Analytics.Reports.cs',
                'wad/sources/src/GlpiPilot/Core/DeploymentConfiguration.cs', 'wad/sources/src/GlpiPilot/Core/DeploymentConfiguration.Options.cs',
                'wad/sources/src/GlpiPilot/Core/DeploymentConfiguration.Transaction.cs', 'wad/sources/src/GlpiPilot/Core/DeploymentConfiguration.Initializer.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.DeploymentConfiguration.cs', 'wad/sources/tests/RuntimeSessionChecks/DeploymentConfigurationChecks.cs', 'wad/sources/tests/RuntimeSessionChecks/DeploymentOptionChecks.cs',
                'wad/sources/tests/TerrainBridgeChecks/TerrainHistoricalReferenceChecks.cs',
                'wad/sources/tests/WebViewChecks/Program.ReferenceSavedObservation.cs', 'wad/sources/docs/RELEASE-3.8.1.md',
                'wad/sources/src/GlpiPilot/Core/DashboardLayout.SelectionWeather.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.Analytics.SelectionWeather.cs',
                'wad/sources/tests/AnalyticsBridgeChecks/Program.SelectionWeather.cs',
                'wad/sources/tests/CityDetectionChecks/CityReferenceComplementChecks.cs',
                'wad/sources/tests/WebViewChecks/Program.WeatherSelection.cs',
                'wad/sources/web/js/pages/weather-selection-editor.js', 'wad/sources/docs/Seuils-meteo.txt', 'wad/sources/docs/RELEASE-3.9.0.md',
                'wad/sources/tests/WebViewChecks/Program.Deployment.cs', 'wad/sources/web/js/pages/settings-deployment.js', 'wad/sources/web/css/deployment.css',
                'wad/sources/web/js/source-cache.js', 'wad/sources/web/js/pages/pilot-presentation.js', 'wad/sources/web/css/presentation.css',
                'wad/sources/web/css/motion-controls.css', 'wad/sources/web/js/motion-controls.js',
                'wad/sources/web/js/calendar-layout.js', 'wad/sources/web/js/work-context.js', 'wad/sources/web/js/workspace-scope.js',
                'wad/sources/web/js/shared-records.js', 'wad/sources/web/js/pages/shared-business.js', 'wad/sources/web/js/pages/interventions-workspace.js',
                'wad/sources/docs/Calendrier-et-interventions.md', 'wad/sources/docs/Bibliotheque-et-partage.md', 'wad/sources/docs/RELEASE-3.5.0.md',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.SharedOperations.cs',
                'wad/sources/tests/TicketLibraryChecks/Program.cs', 'wad/sources/tests/TicketLibraryChecks/PostgresChecks.cs',
                'wad/sources/tests/SharedWorkspaceChecks/Program.cs',
                'wad/sources/tests/TicketLibraryAcquisitionChecks/Program.cs',
                'wad/sources/tests/WebViewChecks/Program.Library.cs',
                'wad/sources/tests/WebViewChecks/Program.Navigation.cs',
                'wad/sources/tests/WebViewChecks/Program.SharedOperations.cs',
                'wad/sources/tests/RuntimeSessionChecks/SharedOperationsChecks.cs',
                'wad/sources/tests/RuntimeSessionChecks/SharedConflictChecks.cs',
                'wad/sources/tests/WebViewChecks/Program.Palette.cs',
                'wad/sources/tests/WebViewChecks/Program.LibraryWorkflows.cs',
                'wad/sources/tests/WebViewChecks/Program.Resolution.cs',
                'wad/sources/tests/WebViewChecks/Program.LocalFileTest.cs',
                'wad/sources/tests/WebViewChecks/Program.Maps.cs',
                'wad/sources/tests/RuntimeSessionChecks/EmbeddedMapsChecks.cs',
                'wad/sources/src/GlpiPilot/Core/EmbeddedMapsService.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.Maps.cs',
                'wad/sources/tests/WebViewChecks/Program.BusinessPreview.cs',
                'wad/sources/tests/WebViewChecks/Program.WeatherScope.cs',
                'wad/sources/tests/RuntimeSessionChecks/OfflineImportChecks.cs',
                'wad/sources/tests/RuntimeSessionChecks/SimulationChecks.cs',
                'wad/sources/tests/WorkbookProfileChecks/WorkbookProfileChecks.csproj',
                'wad/sources/tests/WorkbookProfileChecks/Program.cs',
                'wad/sources/src/GlpiPilot/Core/WorkbookPilotProfile.cs',
                'wad/sources/src/GlpiPilot/Core/BusinessDashboardTemplates.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.Analytics.Business.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.Simulation.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.SimulationIntegrations.cs',
                'wad/sources/src/GlpiPilot/Core/TicketLibraryCaptureStore.cs',
                'wad/sources/src/GlpiPilot/Core/TeamWorkspaceConflictComparison.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.SharedConflicts.cs',
                'wad/sources/src/GlpiPilot/Core/TicketLibraryResolutionService.cs',
                'wad/sources/src/GlpiPilot/Runtime/PilotApplication.LibraryResolution.cs',
                'wad/sources/tests/TicketResolutionChecks/TicketResolutionChecks.csproj',
                'wad/sources/tests/TicketResolutionChecks/Program.cs'
            )
        }
        # The light pack uses the WebView2 runtime installed on Windows; the complete packs carry the fixed runtime.
        if ($Edition -eq 'PROD-LIGHT') {
            if ($entries | Where-Object { $_ -like 'wad/runtime/*' }) { throw 'PROD-LIGHT unexpectedly contains a bundled runtime.' }
            # Le pack leger n'embarque pas le build Firefox : le choix doit y rester honnetement ferme.
            if ($entries | Where-Object { $_ -like 'wad/.local-browsers/*' }) { throw 'PROD-LIGHT unexpectedly contains a bundled Firefox build.' }
        } else { $required += @('wad/runtime/webview2/msedgewebview2.exe', 'wad/runtime/webview2/msedge.dll') }
        # Firefox n'existe qu'en pack complet : sans executable, le reglage mentirait au poste.
        if ($Edition -eq 'PROD' -and -not ($entries | Where-Object { $_ -like 'wad/.local-browsers/*firefox.exe' })) { throw 'PROD pack contains no Firefox executable.' }
        foreach ($name in $required) { if ($entries -notcontains $name) { throw "Incomplete $Edition pack: $name is missing." } }
        # Independent upstream identities published by Leaflet, in addition to the pack manifest.
        $leafletHashes = @{
            'wad/web/vendor/leaflet/leaflet.js' = 'db49d009c841f5ca34a888c96511ae936fd9f5533e90d8b2c4d57596f4e5641a'
            'wad/web/vendor/leaflet/leaflet.css' = 'a7837102824184820dfa198d1ebcd109ff6d0ff9a2672a074b9a1b4d147d04c6'
        }
        foreach ($name in $leafletHashes.Keys) {
            if ((Get-EntryHash $archive.GetEntry($name)) -ne $leafletHashes[$name]) { throw "Unexpected Leaflet 1.9.4 resource in $Edition pack: $name" }
        }
        if ($Edition -ne 'DEV' -and ($entries | Where-Object { $_ -like 'wad/sources/*' })) { throw "$Edition unexpectedly contains development sources." }
        # -cmatch : les noms d'etat de l'application sont écrits exactement ainsi (Browser, Profiles, Exports).
        # Un -match insensible a la casse ferait rejeter le pack complet, dont wad/.local-browsers/embarque le
        # repertoire legitime firefox/browser du build Playwright de Firefox.
        if ($entries | Where-Object { $_ -cmatch '(^|/)(settings\.json|library\.local\.json|library-sync\.local\.json|shared-business\.local\.json|glpi-pilot-library\.db(?:-(?:wal|shm|journal))?|GLPI-Pilot-Shared|SharedIndexes|SharedFolderIndexes|Browser|Profiles|Exports)(/|$)|(^|/)\.node\.exe\.' }) {
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
        $adminHash = Get-EntryHash $archive.GetEntry('GLPI-Pilot-Admin.exe')
        if ($metadata.distribution -ne $Edition -or $metadata.executable_sha256 -ne $exeHash -or $metadata.admin_executable_sha256 -ne $adminHash -or $adminHash -eq $exeHash) { throw "$Edition metadata does not match its executable." }
        $expectedLoader = if ($Edition -eq 'PROD-LIGHT') { 'evergreen-installed' } else { 'bundled-native-x64' }
        if ($metadata.webview2_loader -ne $expectedLoader -or ($Edition -eq 'PROD-LIGHT') -ne [string]::IsNullOrEmpty($metadata.webview2_runtime)) { throw "$Edition metadata does not declare its WebView2 loader." }
        if ($env:GITHUB_SHA -and $metadata.source_commit -ne $env:GITHUB_SHA) { throw "$Edition is not associated with the current source commit." }
        Write-Host "PASS: $Edition root, required files and all $($seen.Count) SHA-256 entries verified. EXE_SHA256=$exeHash"
        return @{ Standard = $exeHash; Admin = $adminHash }
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

function Initialize-PackStartup {
    $harness = Join-Path $OutputDirectory 'webview-native-harness'
    Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('publish', 'tests/WindowsStartupChecks/WindowsStartupChecks.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:PlaywrightPlatform=win', '-o', $harness) `
        -LogName 'windows-startup-build.log' -TimeoutSeconds 420
    $deploymentFixture = Join-Path $OutputDirectory 'deployment-native-fixture/GLPI-Pilot.deployment.json'
    # Le job des packs part d'un checkout propre, séparé du job des contrôles WebView qui compilait ce projet :
    # sans cette compilation, le --no-build suivant échouait faute de binaire.
    Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('build', 'tests/RuntimeSessionChecks/RuntimeSessionChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win') `
        -LogName 'deployment-native-fixture-build.log' -TimeoutSeconds 300
    Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/RuntimeSessionChecks/RuntimeSessionChecks.csproj', '-c', 'Release', '--no-build', '--', '--write-deployment-fixture', $deploymentFixture) `
        -LogName 'deployment-native-fixture.log' -TimeoutSeconds 120 -ExpectedSummary 'Deployment native fixture written.'
}

function Test-PackStartup {
    param([ValidateSet('PROD','PROD-LIGHT')][string]$Edition)
    $pack = Get-Pack -Edition $Edition
    [void](Assert-PackContents $pack -Edition $Edition)
    $harness = Join-Path $OutputDirectory 'webview-native-harness/WindowsStartupChecks.exe'
    $deploymentFixture = Join-Path $OutputDirectory 'deployment-native-fixture/GLPI-Pilot.deployment.json'
    $suffix = ''
    if ($Edition -eq 'PROD-LIGHT') {
        $suffix = '-light'
        $evergreen = @('HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') |
            Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { (Get-ItemProperty -LiteralPath $_).pv } | Where-Object { $_ } | Select-Object -First 1
        if (-not $evergreen) { throw 'PROD-LIGHT cannot be qualified: install WebView2 Evergreen on the Windows runner.' }
        Write-Host "Installed WebView2 Evergreen runtime $evergreen; starting the light pack on it."
    }
    Invoke-CheckedProcess -Executable $harness `
        -Arguments @('--pack', $pack.FullName, '--out', (Join-Path $logs "native-webview$suffix"), '--deployment', $deploymentFixture) `
        -LogName "windows-startup-checks$suffix.log" -TimeoutSeconds 480 -ExpectedPattern '(?m)^([1-9]\d*)/\1 native Windows WebView checks passed; isolated profile, no GLPI session accessed\.\r?$'
}

Push-Location $repository
try {
    switch ($Stage) {
        'BclChecks' {
            Invoke-CheckedProcess -Executable 'node' -Arguments @('--test', '--test-reporter=tap', 'tests/WorkspaceGridChecks/run.mjs') `
                -LogName 'workspace-grid-geometry.log' -TimeoutSeconds 120 -ExpectedPattern '(?m)^# fail 0\r?$'
            Invoke-CheckedProcess -Executable (Get-Process -Id $PID).Path -Arguments @('-NoProfile', '-NonInteractive', '-File', (Join-Path $repository 'tests/SourceProvenanceChecks/Program.ps1')) `
                -LogName 'source-provenance-checks.log' -TimeoutSeconds 180 -ExpectedPattern '\d+/\d+ source provenance checks passed\.'
            $suites = @(
                @{ Project = 'FieldPreparationChecks'; Log = 'field-engine.log'; Summary = '18/18 field preparation checks passed.' },
                @{ Project = 'FieldPreparationWorkspaceChecks'; Log = 'field-workspace.log'; Summary = '15/15 field workspace checks passed.' },
                @{ Project = 'FieldPreparationBridgeChecks'; Log = 'field-bridge.log'; Summary = '18/18 field bridge checks passed.' },
                @{ Project = 'FieldPreparationGuardChecks'; Log = 'field-guards.log'; Summary = '2/2 field guard checks passed.' },
                @{ Project = 'FieldCompletionChecks'; Log = 'field-completion.log'; Summary = '14/14 field completion checks passed (folder).' },
                @{ Project = 'FieldTourChecks'; Log = 'field-tours.log'; Summary = '7/7 field tour checks passed.' },
                @{ Project = 'StartupDiagnosticsChecks'; Log = 'startup-diagnostics-checks.log'; Summary = 'Startup diagnostics: 14 passed, 0 failed.' },
                @{ Project = 'StartupResilienceChecks'; Log = 'startup-resilience-checks.log'; Summary = 'startup resilience checks passed.' },
                @{ Project = 'FoundationChecks'; Log = 'foundation-checks.log'; Summary = ''; Pattern = '(?m)^37/37 Foundation checks passed\.\r?$' },
                @{ Project = 'WorkplaceChecks'; Log = 'workplace-checks.log'; Summary = 'workplace checks passed.' },
                @{ Project = 'ConfigurationChecks'; Log = 'configuration-checks.log'; Summary = '67/67 vérifications de configuration réussies.' },
                @{ Project = 'DashboardChecks'; Log = 'dashboard-checks.log'; Summary = '156/156' },
                @{ Project = 'DashboardLayoutChecks'; Log = 'dashboard-layout-checks.log'; Summary = '47/47 checks passed.' },
                @{ Project = 'WorkbookProfileChecks'; Log = 'workbook-profile-checks.log'; Summary = '21/21 workbook profile checks passed.' },
                @{ Project = 'PreviewQueryChecks'; Log = 'preview-query-checks.log'; Summary = ''; Pattern = '(?m)^48/48 checks passed\.\r?$' },
                @{ Project = 'TechnicianWorkloadChecks'; Log = 'technician-workload-checks.log'; Summary = '35/35' },
                @{ Project = 'CityDetectionChecks'; Log = 'city-detection-checks.log'; Summary = '113/113' },
                @{ Project = 'TourPlanningChecks'; Log = 'tour-planning-checks.log'; Summary = '87/87' },
                @{ Project = 'QueryChecks'; Log = 'query-checks.log'; Summary = '99/99 vérifications réussies.' },
                @{ Project = 'AnalyticsChecks'; Log = 'analytics-checks.log'; Summary = '39/39 vérifications réussies.' },
                @{ Project = 'CompletionChecks'; Log = 'completion-checks.log'; Summary = '44/44 Completion checks passed.' },
                @{ Project = 'AdministrationChecks'; Log = 'administration-checks.log'; Summary = ''; Pattern = '(?m)^([1-9]\d*)/\1 administration checks passed\.\r?$' },
                @{ Project = 'ComponentChecks'; Log = 'component-checks.log'; Summary = ''; Pattern = '(?m)^([1-9]\d*)/\1 vérifications de composants réussies\.\r?$' },
                @{ Project = 'UpdateChecks'; Log = 'update-checks.log'; Summary = ''; Pattern = '\d+/\d+ update checks passed\.' },
                @{ Project = 'LanguageAssistantChecks'; Log = 'language-assistant-checks.log'; Summary = ''; Pattern = '\d+/\d+ language assistant checks passed; synthetic handlers only, no provider contacted\.' },
                @{ Project = 'NativeImportChecks'; Log = 'native-import-checks.log'; Summary = '40/40' },
                @{ Project = 'ExportChecks'; Log = 'export-checks.log'; Summary = '6/6' },
                @{ Project = 'TicketLibraryChecks'; Log = 'ticket-library-checks.log'; Summary = ''; Pattern = '(?m)^([1-9]\d*)/\1 ticket library checks passed\.\r?$' },
                @{ Project = 'TicketResolutionChecks'; Log = 'ticket-resolution-checks.log'; Summary = ''; Pattern = '(?m)^([1-9]\d*)/\1 ticket resolution checks passed\.\r?$' },
                @{ Project = 'SharedWorkspaceChecks'; Log = 'shared-workspace-checks.log'; Summary = ''; Pattern = '(?m)^25/25 shared workspace checks passed\.\r?$' },
                @{ Project = 'CalendarChecks'; Log = 'calendar-checks.log'; Summary = ''; Pattern = '(?m)^52/52 contrôles calendrier réussis\.\r?$' },
                @{ Project = 'CalendarBridgeChecks'; Log = 'calendar-bridge-checks.log'; Summary = ''; Pattern = '(?m)^52/52 Calendar bridge checks passed\.\r?$' },
                @{ Project = 'SharedFolderChecks'; Log = 'shared-folder-checks.log'; Summary = ''; Pattern = '(?m)^22/22 shared folder checks passed\.\r?$' },
                @{ Project = 'SharedBusinessChecks'; Log = 'shared-business-checks.log'; Summary = ''; Pattern = '(?m)^23/23 shared business checks passed\.\r?$' }
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
                # Keep cold restore/compilation separate from the browser process's deadline.
                $project = "tests/$suite/$suite.csproj"
                Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('build', $project, '-c', 'Release', '-p:PlaywrightPlatform=win') `
                    -LogName ($suite.ToLowerInvariant() + '-build.log') -TimeoutSeconds 120
                $assembly = Join-Path $repository "tests/$suite/bin/Release/net10.0/$suite.dll"
                if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw "Missing browser test assembly: $suite." }
                # Mesuré sur le runner qualifié : AutomationChecks a rendu ses 40/40 vérifications puis a dépassé
                # le plafond de 180 s avant de rendre la main. Ces deux suites pilotent un vrai WebView2 et un vrai
                # navigateur, comme les voisines déjà plafonnées à 300 s ; le même budget leur est appliqué.
                Invoke-CheckedProcess -Executable 'dotnet' -Arguments @($assembly) `
                    -LogName ($suite.ToLowerInvariant() + '.log') -TimeoutSeconds 300 -ExpectedPattern '(?m)^([1-9]\d*)/\1 vérifications[^\r\n]*réussies\.\r?$'
            }
            # Same rule as the neighbours: cold compilation must not eat the browser process's deadline.
            $detailProject = 'tests/TicketDetailChecks/TicketDetailChecks.csproj'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('build', $detailProject, '-c', 'Release', '-p:PlaywrightPlatform=win') `
                -LogName 'ticketdetailchecks-build.log' -TimeoutSeconds 120
            $detailAssembly = Join-Path $repository 'tests/TicketDetailChecks/bin/Release/net10.0/TicketDetailChecks.dll'
            if (-not (Test-Path -LiteralPath $detailAssembly -PathType Leaf)) { throw 'Missing compiled ticket detail checks.' }
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @($detailAssembly) `
                -LogName 'ticketdetailchecks.log' -TimeoutSeconds 180 -ExpectedSummary '32/32 contrôles de lecture de fiche réussis.'
            $driver = Join-Path $repository 'tests/AutomationChecks/bin/Release/net10.0/.playwright'
            $env:GLPI_TEST_PLAYWRIGHT_MODULE = Join-Path $driver 'package'
            Invoke-CheckedProcess -Executable (Join-Path $driver 'node/win32_x64/node.exe') `
                -Arguments @('tests/NativeDomChecks/run.cjs', (Join-Path $logs 'native-dom-detail.log')) `
                -LogName 'native-dom-checks.log' -TimeoutSeconds 120 -ExpectedSummary '159 native export DOM checks passed.'
        }
        'LibraryAcquisitionChecks' {
            # Own stage since 3.13.3: with its 25 checks, this suite no longer fits the browser stage's budget beside the others.
            Set-TestBrowser
            # Les 300 secondes couvrent les 25 contrôles et l'arrêt du vrai processus navigateur. A 180 secondes,
            # la suite passait ou cassait selon la charge du poste : le lancement de Chromium absorbait le surplus.
            $suite = 'tests/TicketLibraryAcquisitionChecks'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('build', "$suite/TicketLibraryAcquisitionChecks.csproj", '-c', 'Release', '-p:PlaywrightPlatform=win') `
                -LogName 'ticket-library-acquisition-build.log' -TimeoutSeconds 120
            $assembly = Join-Path $repository "$suite/bin/Release/net10.0/TicketLibraryAcquisitionChecks.dll"
            if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw 'Missing compiled ticket library acquisition checks.' }
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @($assembly) `
                -LogName 'ticket-library-acquisition-checks.log' -TimeoutSeconds 300 -ExpectedSummary "25/25 contrôles d’acquisition de bibliothèque réussis."
        }
        'WebViewChecks' {
            Set-TestBrowser
            foreach ($edition in @('false', 'true')) {
                Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/FieldIntegratedUiChecks/FieldIntegratedUiChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', "-p:PilotAdmin=$edition") `
                    -LogName "field-integrated-$edition.log" -TimeoutSeconds 240 -ExpectedPattern '(?m)^([1-9]\d*)/\1 integrated field workflow checks passed\.\r?$'
            }
            foreach ($edition in @('false', 'true')) {
                Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', "-p:PilotAdmin=$edition", '--', '--workspace-grid-only', '--screenshots', (Join-Path $logs "workspace-grid-$edition")) `
                    -LogName "workspace-grid-$edition.log" -TimeoutSeconds 300 -ExpectedPattern '(?m)^([1-9]\d*)/\1 workspace grid WebView checks passed\.\r?$'
            }
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '--', '--identity-only', '--screenshots', (Join-Path $logs 'identity-preview')) `
                -LogName 'identity-webview-checks.log' -TimeoutSeconds 240 -ExpectedPattern '(?m)^19/19 personal identity WebView checks passed\.\r?$'
            foreach ($suite in @('WebViewChecks', 'RuntimeSessionChecks', 'AnalyticsBridgeChecks', 'WorkplaceBridgeChecks', 'TerrainBridgeChecks')) {
                $arguments = @('run', '--project', "tests/$suite/$suite.csproj", '-c', 'Release', '-p:PlaywrightPlatform=win')
                $pattern = '\d+/\d+ '
                switch ($suite) {
                    'WebViewChecks' { $arguments += @('--', '--screenshots', (Join-Path $logs 'web-preview')); $pattern += 'WebView integration checks passed\.' }
                    'RuntimeSessionChecks' { $pattern = '(?m)^([1-9]\d*)/\1 Runtime session isolation and shutdown checks passed\.\r?$' }
                    'AnalyticsBridgeChecks' { $arguments += @('--', '--web'); $pattern += 'analytics bridge and scoped-history checks passed\.' }
                    'WorkplaceBridgeChecks' { $pattern += 'Workplace bridge and WebView checks passed\.' }
                    'TerrainBridgeChecks' { $pattern += 'terrain bridge checks passed\.' }
                }
                Invoke-CheckedProcess -Executable 'dotnet' -Arguments $arguments -LogName ($suite.ToLowerInvariant() + '.log') -TimeoutSeconds 240 -ExpectedPattern $pattern
            }
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '--', '--library-only', '--screenshots', (Join-Path $logs 'library-preview')) `
                -LogName 'ticket-library-webview-checks.log' -TimeoutSeconds 240 -ExpectedPattern '(?m)^21/21 ticket library WebView checks passed\.\r?$'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '--', '--calendar-only', '--screenshots', (Join-Path $logs 'calendar-preview')) `
                -LogName 'calendar-webview-checks.log' -TimeoutSeconds 300 -ExpectedPattern '(?m)^32/32 intervention calendar WebView checks passed\.\r?$'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '--', '--navigation-only', '--screenshots', (Join-Path $logs 'navigation-preview')) `
                -LogName 'navigation-webview-checks.log' -TimeoutSeconds 240 -ExpectedPattern '(?m)^([1-9]\d*)/\1 navigation WebView checks passed\.\r?$'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '--', '--presentation-only', '--screenshots', (Join-Path $logs 'presentation-preview')) `
                -LogName 'presentation-webview-checks.log' -TimeoutSeconds 300 -ExpectedPattern '(?m)^([1-9]\d*)/\1 presentation WebView checks passed\.\r?$'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '--', '--deployment-only', '--screenshots', (Join-Path $logs 'deployment-preview')) `
                -LogName 'deployment-webview-checks.log' -TimeoutSeconds 240 -ExpectedPattern '(?m)^([1-9]\d*)/\1 deployment configuration WebView checks passed\.\r?$'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '--', '--reference-saved-observation-only', '--screenshots', (Join-Path $logs 'reference-saved-observation-preview')) `
                -LogName 'reference-saved-observation-webview-checks.log' -TimeoutSeconds 240 -ExpectedPattern '(?m)^([1-9]\d*)/\1 saved observation reference WebView checks passed\.\r?$'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '--', '--source-cache-only', '--screenshots', (Join-Path $logs 'source-cache-preview')) `
                -LogName 'source-cache-webview-checks.log' -TimeoutSeconds 240 -ExpectedSummary '11/11 source cache WebView checks passed.'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '--', '--shared-updates-only') `
                -LogName 'shared-updates-webview-checks.log' -TimeoutSeconds 240 -ExpectedSummary '17/17 shared updates WebView checks passed.'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '--', '--motion-only', '--screenshots', (Join-Path $logs 'motion-preview')) `
                -LogName 'motion-webview-checks.log' -TimeoutSeconds 360 -ExpectedPattern '(?m)^34/34 continuous motion WebView checks passed\.\r?$'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '--', '--business-preview-only', '--screenshots', (Join-Path $logs 'business-preview')) `
                -LogName 'native-pilot-webview-checks.log' -TimeoutSeconds 240 -ExpectedPattern '(?m)^([1-9]\d*)/\1 native pilot views WebView checks passed\.\r?$'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '--', '--shared-operations-only', '--screenshots', (Join-Path $logs 'shared-operations-preview')) `
                -LogName 'shared-operations-webview-checks.log' -TimeoutSeconds 240 -ExpectedSummary '8/8 shared operations WebView checks passed.'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '--', '--palette-only', '--screenshots', (Join-Path $logs 'palette-preview')) `
                -LogName 'command-palette-webview-checks.log' -TimeoutSeconds 240 -ExpectedSummary '15/15 command palette WebView checks passed.'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '--', '--library-workflows-only', '--screenshots', (Join-Path $logs 'library-workflows-preview')) `
                -LogName 'library-workflows-webview-checks.log' -TimeoutSeconds 240 -ExpectedPattern '(?m)^([1-9]\d*)/\1 library workflows WebView checks passed\.\r?$'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '--', '--resolution-only', '--screenshots', (Join-Path $logs 'resolution-preview')) `
                -LogName 'ticket-resolution-webview-checks.log' -TimeoutSeconds 240 -ExpectedPattern '(?m)^([1-9]\d*)/\1 ticket resolution WebView checks passed\.\r?$'
            $driver = Join-Path $repository 'tests/TerrainBridgeChecks/bin/Release/net10.0/.playwright'
            $env:GLPI_TEST_PLAYWRIGHT_MODULE = Join-Path $driver 'package'
            Invoke-CheckedProcess -Executable (Join-Path $driver 'node/win32_x64/node.exe') -Arguments @('tests/TerrainWebChecks/run.cjs') `
                -LogName 'terrain-webview-checks.log' -TimeoutSeconds 180 -ExpectedSummary '31/31 terrain WebView page checks passed.'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '-p:PilotAdmin=false', '--', '--administration-only', '--screenshots', (Join-Path $logs 'administration-standard-preview')) `
                -LogName 'administration-standard-webview-checks.log' -TimeoutSeconds 240 -ExpectedSummary '6/6 administration WebView checks passed.'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '-p:PilotAdmin=false', '--', '--data-source-workflow-only', '--screenshots', (Join-Path $logs 'data-sources-standard-preview')) `
                -LogName 'data-source-standard-webview-checks.log' -TimeoutSeconds 300 -ExpectedPattern '(?m)^([1-9]\d*)/\1 data source workflow WebView checks passed\.\r?$'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '-p:PilotAdmin=false', '--', '--workspace-refresh-only', '--screenshots', (Join-Path $logs 'workspace-refresh-standard-preview')) `
                -LogName 'workspace-refresh-standard-webview-checks.log' -TimeoutSeconds 300 -ExpectedPattern '(?m)^([1-9]\d*)/\1 workspace refresh WebView checks passed\.\r?$'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '-p:PilotAdmin=false', '--', '--weather-selection-only', '--screenshots', (Join-Path $logs 'weather-selection-standard-preview')) `
                -LogName 'weather-selection-standard-webview-checks.log' -TimeoutSeconds 300 -ExpectedSummary '27/27 weather selection WebView checks passed.'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '-p:PilotAdmin=false', '--', '--location-correction-only', '--screenshots', (Join-Path $logs 'location-correction-standard-preview')) `
                -LogName 'location-correction-standard-webview-checks.log' -TimeoutSeconds 300 -ExpectedPattern '(?m)^([1-9]\d*)/\1 location correction WebView checks passed\.\r?$'
            # Keep Admin suites last: all preceding UI suites exercise Standard.
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '-p:PilotAdmin=true', '--', '--administration-only', '--screenshots', (Join-Path $logs 'administration-admin-preview')) `
                -LogName 'administration-admin-webview-checks.log' -TimeoutSeconds 240 -ExpectedSummary '8/8 administration WebView checks passed.'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '-p:PilotAdmin=true', '--', '--data-source-workflow-only', '--screenshots', (Join-Path $logs 'data-sources-admin-preview')) `
                -LogName 'data-source-admin-webview-checks.log' -TimeoutSeconds 300 -ExpectedPattern '(?m)^([1-9]\d*)/\1 data source workflow WebView checks passed\.\r?$'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '-p:PilotAdmin=true', '--', '--workspace-refresh-only', '--screenshots', (Join-Path $logs 'workspace-refresh-admin-preview')) `
                -LogName 'workspace-refresh-admin-webview-checks.log' -TimeoutSeconds 300 -ExpectedPattern '(?m)^([1-9]\d*)/\1 workspace refresh WebView checks passed\.\r?$'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '-p:PilotAdmin=true', '--', '--weather-selection-only', '--screenshots', (Join-Path $logs 'weather-selection-admin-preview')) `
                -LogName 'weather-selection-admin-webview-checks.log' -TimeoutSeconds 300 -ExpectedSummary '27/27 weather selection WebView checks passed.'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('run', '--project', 'tests/WebViewChecks/WebViewChecks.csproj', '-c', 'Release', '-p:PlaywrightPlatform=win', '-p:PilotAdmin=true', '--', '--location-correction-only', '--screenshots', (Join-Path $logs 'location-correction-admin-preview')) `
                -LogName 'location-correction-admin-webview-checks.log' -TimeoutSeconds 300 -ExpectedPattern '(?m)^([1-9]\d*)/\1 location correction WebView checks passed\.\r?$'
        }
        'BuildPack' {
            $release = Join-Path $OutputDirectory 'release'
            . (Join-Path $PSScriptRoot 'SourceProvenance.ps1')
            $buildProvenance = Resolve-GlpiBuildProvenance -SourceRoot $repository
            $dotnetPath = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
            $runtimePath = & (Join-Path $PSScriptRoot 'Get-WebView2Runtime.ps1') -CacheDirectory (Join-Path $env:RUNNER_TEMP 'GLPI-WebView2-Cache')
            Invoke-CheckedProcess -Executable (Get-Process -Id $PID).Path -Arguments @('-NoProfile', '-NonInteractive', '-File', (Join-Path $repository 'tests/RuntimePackagingChecks/Program.ps1'), '-RuntimePath', $runtimePath) -LogName 'runtime-packaging-checks.log' -TimeoutSeconds 180 -ExpectedSummary '14/14 runtime packaging checks passed; offline only, no runtime launched.'
            if (-not $runtimePath -or -not (Test-Path -LiteralPath (Join-Path $runtimePath 'msedgewebview2.exe'))) { throw 'Verified fixed WebView2 runtime unavailable.' }
            $buildArguments = @('-NoProfile', '-NonInteractive', '-File', (Join-Path $repository 'build.ps1'), '-OutputDirectory', $release, '-ValidationDirectory', $logs, '-RuntimePath', $runtimePath, '-DotNetPath', $dotnetPath, '-AllowOnlineRestore')
            if ($IncludeDevRebuild) { $buildArguments += '-IncludeRebuildFixture' }
            Invoke-CheckedProcess -Executable (Get-Process -Id $PID).Path -Arguments $buildArguments `
                -LogName 'pack-build.log' -TimeoutSeconds 900
            $exeHashes = @{}; $hashLines = [Collections.Generic.List[string]]::new()
            $editions = @('PROD', 'PROD-LIGHT') + $(if ($IncludeDevRebuild) { @('DEV') } else { @() })
            foreach ($edition in $editions) {
                $pack = Get-Pack -Edition $edition
                $exeHashes[$edition] = Assert-PackContents $pack -Edition $edition
                $digest = (Get-FileHash -LiteralPath $pack.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($edition -eq 'DEV') { continue } # Internal rebuild fixture; do not advertise a CI download.
                $hashLines.Add("$digest  $($pack.Name)")
                if ($env:GITHUB_OUTPUT) {
                    "$($edition.ToLowerInvariant())-pack-path=$($pack.FullName)" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
                }
                Write-Host "Complete $edition Windows pack: $($pack.Name); SHA-256: $digest"
            }
            foreach ($edition in $editions | Where-Object { $_ -ne 'PROD' }) {
                if ($exeHashes['PROD'].Standard -ne $exeHashes[$edition].Standard -or $exeHashes['PROD'].Admin -ne $exeHashes[$edition].Admin) { throw "PROD and $edition do not contain the same executable." }
            }
            # Le pack leger ne vaut que s'il reste raccordable hors ligne : les trois ZIP de dependances
            # doivent exister, etre references par une empreinte verifiable et porter leur charge utile.
            $componentsRelease = Join-Path $release 'composants'
            if (-not (Test-Path -LiteralPath $componentsRelease)) { throw 'build.ps1 produced no composants directory.' }
            $componentSumsPath = Join-Path $componentsRelease 'SHA256SUMS.txt'
            if (-not (Test-Path -LiteralPath $componentSumsPath)) { throw 'composants/SHA256SUMS.txt is missing.' }
            $componentDigests = @{}
            foreach ($line in [IO.File]::ReadAllLines($componentSumsPath)) {
                if (-not $line.Trim()) { continue }
                $parts = $line -split '\s+', 2
                if ($parts.Count -ne 2) { throw "Malformed composants/SHA256SUMS.txt line: $line" }
                $componentDigests[$parts[1].Trim()] = $parts[0].ToLowerInvariant()
            }
            $componentPayloads = @{
                'webview2-fixed' = 'runtime/webview2/msedgewebview2.exe'
                'playwright-driver' = '.playwright/package/cli.js'
                'firefox' = 'firefox'
            }
            foreach ($id in $componentPayloads.Keys) {
                $archives = @(Get-ChildItem -LiteralPath $componentsRelease -Filter ('GLPI-Pilot_*_' + $id + '.zip') -File)
                if ($archives.Count -ne 1) { throw "Expected exactly one dependency package for $id." }
                $archive = $archives[0]
                if ($archive.Length -eq 0) { throw "Dependency package $id is empty." }
                $expected = $componentDigests[$archive.Name]
                if (-not $expected) { throw "Dependency package $($archive.Name) is not listed in composants/SHA256SUMS.txt." }
                if ((Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw "Dependency package $id does not match composants/SHA256SUMS.txt." }
                $zip = [IO.Compression.ZipFile]::OpenRead($archive.FullName)
                try {
                    $entries = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
                    if ($entries -notcontains 'component.json') { throw "Dependency package $id is missing component.json." }
                    if ($id -eq 'firefox') {
                        if (-not ($entries | Where-Object { $_ -like '*firefox.exe' })) { throw "Dependency package $id carries no Firefox executable." }
                    } elseif ($entries -notcontains $componentPayloads[$id]) { throw "Dependency package $id is missing $($componentPayloads[$id])." }
                } finally { $zip.Dispose() }
                Write-Host "Dependency package ready for offline install: $($archive.Name); SHA-256: $expected"
            }
            [IO.File]::WriteAllText((Join-Path $logs 'pack-sha256.txt'), (($hashLines -join "`n") + "`n"), $utf8)
            [IO.File]::WriteAllText((Join-Path $logs 'executable-sha256.txt'), ($exeHashes['PROD'].Standard + "  GLPI-Pilot.exe`n" + $exeHashes['PROD'].Admin + "  GLPI-Pilot-Admin.exe`n"), $utf8)
            if ($IncludeDevRebuild) {
                Invoke-CheckedProcess -Executable (Get-Process -Id $PID).Path -Arguments @('-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot 'Test-DevRebuild.ps1'), '-DevPack', (Get-Pack -Edition DEV).FullName, '-RuntimePath', $runtimePath, '-DotNetPath', $dotnetPath, '-ExpectedCommit', $buildProvenance.Commit) `
                    -LogName 'dev-rebuild-checks.log' -TimeoutSeconds 1200 -ExpectedSummary '5/5 DEV rebuild checks passed; exported sources, isolated offline restore, no rebuilt pack published.'
            }
        }
        'PreparePackStartup' { Initialize-PackStartup }
        'PackStartupProd' { Test-PackStartup -Edition 'PROD' }
        'PackStartupLight' { Test-PackStartup -Edition 'PROD-LIGHT' }
        'PackStartup' {
            Initialize-PackStartup
            Test-PackStartup -Edition 'PROD'
            Test-PackStartup -Edition 'PROD-LIGHT'
        }
        'UpdateStartup' {
            $harness = Join-Path $OutputDirectory 'update-harness'
            Invoke-CheckedProcess -Executable 'dotnet' -Arguments @('publish', 'tests/WindowsUpdateChecks/WindowsUpdateChecks.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-o', $harness) `
                -LogName 'windows-update-build.log' -TimeoutSeconds 420
            # Seul PROD-LIGHT est publié depuis 3.14.0 : le remplacement réel se vérifie sur ce pack, plus léger
            # (sans runtime WebView2 fixe ni Firefox) et démarré sur le runtime Evergreen du runner.
            $pack = Get-Pack -Edition 'PROD-LIGHT'
            # Le fixture s'est donné un budget par contrôle (12 min, 20 min pour les trois contrôles qui laissent
            # l'assistant de maintenance travailler), soit 72 min au pire. Ce plafond externe doit rester au-dessus
            # du cumul : à 960 s il tuait le fixture en plein contrôle, avant qu'il ait pu nommer la phase lente.
            # Les commentaires restent au-dessus de l'appel : placés entre l'accent grave de continuation et sa
            # ligne suivante, ils coupaient l'appel et -LogName devenait un paramètre obligatoire manquant.
            Invoke-CheckedProcess -Executable (Join-Path $harness 'WindowsUpdateChecks.exe') -Arguments @($pack.FullName, (Join-Path $logs 'windows-update'), (Join-Path $OutputDirectory 'deployment-native-fixture/GLPI-Pilot.deployment.json')) `
                -LogName 'windows-update-checks.log' -TimeoutSeconds 4800 -ExpectedSummary '4/4 Windows update checks passed.'
        }
    }
}
finally { Pop-Location }
