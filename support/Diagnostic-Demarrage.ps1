#requires -Version 5.1
<#
Diagnostic de démarrage de GLPI Pilot, pour Windows PowerShell 5.1.
Lancer DIAGNOSTIC_DEMARRAGE.cmd depuis le dossier wad\support du pack extrait.
Le script lance seulement l'EXE de ce pack et observe ses 20 premières secondes.
Il ne modifie ni les réglages, ni la session SSO, ni la politique de sécurité.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$utf8Bom = New-Object System.Text.UTF8Encoding -ArgumentList $true
$report = New-Object System.Text.StringBuilder
$packRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$exePath = Join-Path $packRoot 'GLPI-Pilot.exe'
$reportPath = $null
$launchedProcess = $null
$errorRead = $null
$didExit = $false
$startedUtc = $null

function Protect-ReportText([string] $text) {
    if ($null -eq $text) { return '' }
    if ($packRoot) { $text = $text.Replace($packRoot, '[dossier du pack]') }
    if ($env:USERPROFILE) { $text = $text.Replace($env:USERPROFILE, '[profil utilisateur]') }
    return $text
}

function Add-Report([string] $text = '') {
    [void] $report.AppendLine((Protect-ReportText $text))
}

function Add-Exception([System.Exception] $exception) {
    for ($depth = 0; $null -ne $exception -and $depth -lt 6; $depth++) {
        Add-Report ('Exception : {0}' -f $exception.GetType().FullName)
        Add-Report ('Message : {0}' -f $exception.Message)
        $unsigned = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int] $exception.HResult), 0)
        Add-Report ('HRESULT : 0x{0:X8}' -f $unsigned)
        if ($exception -is [System.ComponentModel.Win32Exception]) {
            Add-Report ('Code Windows natif : {0}' -f $exception.NativeErrorCode)
        }
        $exception = $exception.InnerException
    }
}

function Add-FileCheck([string] $relativePath) {
    $path = Join-Path $packRoot $relativePath
    if ([System.IO.File]::Exists($path)) {
        $info = Get-Item -LiteralPath $path -ErrorAction Stop
        Add-Report ('PRÉSENT : {0} ({1:N0} octets)' -f $relativePath, $info.Length)
        return $true
    }
    Add-Report ('ABSENT : {0}' -f $relativePath)
    return $false
}

function Read-StartupLog([string] $path) {
    $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $maximum = 262144
        $truncated = $stream.Length -gt $maximum
        if ($truncated) { [void] $stream.Seek(-$maximum, [System.IO.SeekOrigin]::End) }
        $reader = New-Object System.IO.StreamReader -ArgumentList $stream, ([System.Text.Encoding]::UTF8), $true
        try {
            $content = $reader.ReadToEnd()
            if ($truncated) { return "[Extrait : les 256 derniers Kio du journal.]`r`n" + $content }
            return $content
        }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

$logFolders = New-Object 'System.Collections.Generic.List[string]'
if ($env:LOCALAPPDATA) { $logFolders.Add((Join-Path $env:LOCALAPPDATA 'GLPI-Pilot\Logs')) }
if ($env:TEMP) { $logFolders.Add((Join-Path $env:TEMP 'GLPI-Pilot\Logs')) }
if ($logFolders.Count -eq 0) { $logFolders.Add((Join-Path ([System.IO.Path]::GetTempPath()) 'GLPI-Pilot\Logs')) }
$logFolders = @($logFolders | Select-Object -Unique)

foreach ($folder in $logFolders) {
    try {
        [void] [System.IO.Directory]::CreateDirectory($folder)
        $name = 'diagnostic-demarrage-{0}-{1}.txt' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([Guid]::NewGuid().ToString('N').Substring(0, 6))
        $candidate = Join-Path $folder $name
        [System.IO.File]::WriteAllText($candidate, '', $utf8Bom)
        $reportPath = $candidate
        break
    }
    catch { Write-Warning ('Dossier de rapport indisponible : ' + (Protect-ReportText $_.Exception.Message)) }
}
if (-not $reportPath) {
    Write-Error 'Impossible de créer le rapport dans LOCALAPPDATA ou TEMP. Aucun lancement effectué.' -ErrorAction Continue
    exit 1
}

$startupSources = @()
foreach ($folder in $logFolders) {
    $source = Join-Path $folder 'demarrage-dernier.log'
    $beforeTicks = [long] 0
    $beforeLength = [long] -1
    try {
        if ([System.IO.File]::Exists($source)) {
            $info = Get-Item -LiteralPath $source
            $beforeTicks = $info.LastWriteTimeUtc.Ticks
            $beforeLength = $info.Length
        }
    }
    catch { }
    $startupSources += [pscustomobject] @{ Path = $source; BeforeTicks = $beforeTicks; BeforeLength = $beforeLength }
}

try {
    Add-Report 'GLPI PILOT — DIAGNOSTIC DE DÉMARRAGE'
    Add-Report ('Date locale : {0:yyyy-MM-dd HH:mm:ss zzz}' -f [DateTimeOffset]::Now)
    Add-Report ('Windows indiqué par PowerShell : {0}' -f [Environment]::OSVersion.VersionString)
    Add-Report ('Système 64 bits : {0} ; PowerShell : {1}' -f [Environment]::Is64BitOperatingSystem, $PSVersionTable.PSVersion)
    Add-Report 'Portée : ce pack, son processus lancé et ses journaux de démarrage uniquement.'
    Add-Report 'Aucune installation, modification de configuration ou élévation demandée.'
    Add-Report
    Add-Report 'COMPOSITION DU PACK'
    $exePresent = Add-FileCheck 'GLPI-Pilot.exe'
    $nodePresent = Add-FileCheck 'wad\.playwright\node\win32_x64\node.exe'
    $cliPresent = Add-FileCheck 'wad\.playwright\package\cli.js'
    Add-Report 'Le runtime .NET du pack autonome est intégré à l''EXE. Des DLL coreclr/hostfxr séparées ne sont pas requises.'
    $versionPath = Join-Path $packRoot 'wad\version.json'
    if ([System.IO.File]::Exists($versionPath)) {
        try {
            if ((Get-Item -LiteralPath $versionPath).Length -gt 65536) { throw 'Le fichier version.json dépasse la taille attendue.' }
            $version = [System.IO.File]::ReadAllText($versionPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
            foreach ($field in @('version', 'target', 'runtime', 'self_contained', 'playwright')) {
                if ($null -ne $version.PSObject.Properties[$field]) {
                    Add-Report ('Manifeste du pack — {0} : {1}' -f $field, $version.$field)
                }
            }
        }
        catch { Add-Report ('Manifeste non lisible : ' + $_.Exception.Message) }
    }
    else { Add-Report 'Manifeste wad\version.json absent ; la version du runtime ne peut pas être lue.' }
    if ($exePresent) {
        $exeVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
        Add-Report ('Version de l''EXE : {0}' -f $exeVersion.FileVersion)
    }
    if (-not $nodePresent -or -not $cliPresent) {
        Add-Report 'Le pilote est incomplet : l''extraction ne pourra pas fonctionner. Le démarrage de l''interface reste testé ci-dessous.'
    }
    Add-Report
    Add-Report 'LANCEMENT'
    if (-not $exePresent) {
        Add-Report 'NON LANCÉ : GLPI-Pilot.exe est absent deux dossiers au-dessus de ce script.'
        Add-Report 'Extraire le pack complet en conservant GLPI-Pilot.exe et son dossier wad côte à côte.'
    }
    elseif (-not [Environment]::Is64BitOperatingSystem) {
        Add-Report 'NON LANCÉ : ce pack exige un système Windows 64 bits.'
    }
    else {
        Write-Host 'Lancement de GLPI Pilot et observation pendant 20 secondes maximum…'
        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = $exePath
        $startInfo.WorkingDirectory = $packRoot
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $false
        $startInfo.RedirectStandardError = $true
        $launchedProcess = New-Object System.Diagnostics.Process
        $launchedProcess.StartInfo = $startInfo
        $startedUtc = [DateTime]::UtcNow
        if (-not $launchedProcess.Start()) { throw 'Windows n''a pas créé le processus demandé.' }
        Add-Report ('Processus créé : PID {0}. Aucun autre processus n''est consulté.' -f $launchedProcess.Id)
        $errorRead = $launchedProcess.StandardError.ReadToEndAsync()
        $didExit = $launchedProcess.WaitForExit(20000)
        if ($didExit) {
            $exitCode = $launchedProcess.ExitCode
            $unsignedExit = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int] $exitCode), 0)
            Add-Report ('FERMÉ dans la période d''observation. Code de sortie : {0} (0x{1:X8}).' -f $exitCode, $unsignedExit)
            Add-Report 'Une fermeture manuelle pendant cette période produit également ce résultat.'
        }
        else {
            Add-Report 'TOUJOURS ACTIF après 20 secondes. Le lanceur laisse GLPI Pilot ouvert.'
            Add-Report 'Cela confirme seulement que le processus existe encore ; vérifier si sa fenêtre est visible.'
        }
    }
}
catch {
    Add-Report 'ERREUR pendant la vérification ou le lancement.'
    Add-Exception $_.Exception
}
finally {
    if ($null -ne $errorRead -and $errorRead.IsCompleted) {
        try {
            $launchError = $errorRead.GetAwaiter().GetResult()
            if (-not [string]::IsNullOrWhiteSpace($launchError)) {
                Add-Report
                Add-Report 'ERREUR STANDARD DU SEUL PROCESSUS LANCÉ'
                if ($launchError.Length -gt 65536) {
                    Add-Report $launchError.Substring(0, 65536)
                    Add-Report '[Erreur standard tronquée à 65 536 caractères.]'
                }
                else { Add-Report $launchError }
            }
        }
        catch { Add-Report ('Erreur standard non disponible : ' + $_.Exception.Message) }
    }
    Add-Report
    Add-Report 'JOURNAL DE DÉMARRAGE DE L''APPLICATION'
    $copied = $false
    foreach ($source in $startupSources) {
        try {
            if (-not [System.IO.File]::Exists($source.Path)) { continue }
            $info = Get-Item -LiteralPath $source.Path
            $changed = $null -ne $startedUtc -and ($info.LastWriteTimeUtc.Ticks -ne $source.BeforeTicks -or $info.Length -ne $source.BeforeLength)
            if (-not $changed) {
                Add-Report 'Un ancien demarrage-dernier.log existe, sans mise à jour pendant cet essai ; son contenu n''est pas repris.'
                continue
            }
            Add-Report ('demarrage-dernier.log — dernière écriture UTC : {0:yyyy-MM-dd HH:mm:ss}' -f $info.LastWriteTimeUtc)
            Add-Report (Read-StartupLog $source.Path)
            $copied = $true
        }
        catch { Add-Report ('Journal de démarrage non lisible : ' + $_.Exception.Message) }
    }
    if (-not $copied) {
        Add-Report 'Aucun nouveau journal de démarrage disponible pour cet essai.'
        Add-Report 'L''absence de journal ne permet pas à elle seule de déterminer la cause.'
    }
    Add-Report
    Add-Report 'Ce rapport ne contient aucune lecture de ticket, de configuration, de profil SSO ou de journal système Windows.'
    try {
        [System.IO.File]::WriteAllText($reportPath, $report.ToString(), $utf8Bom)
        Write-Host ('Rapport enregistré : ' + $reportPath)
        Write-Host 'Le Bloc-notes va ouvrir ce rapport. Vous pouvez copier son contenu pour le diagnostic.'
        try {
            $notepad = Join-Path $env:SystemRoot 'System32\notepad.exe'
            $notepadInfo = New-Object System.Diagnostics.ProcessStartInfo
            $notepadInfo.FileName = $notepad
            $notepadInfo.Arguments = '"' + $reportPath + '"'
            $notepadInfo.UseShellExecute = $false
            [void] [System.Diagnostics.Process]::Start($notepadInfo)
        }
        catch { Write-Warning ('Ouverture du Bloc-notes impossible. Ouvrir le rapport indiqué ci-dessus. ' + $_.Exception.Message) }
    }
    catch {
        Write-Host $report.ToString()
        Write-Error ('Enregistrement du rapport impossible : ' + $_.Exception.Message) -ErrorAction Continue
        exit 1
    }
    if ($null -ne $launchedProcess -and $didExit) { $launchedProcess.Dispose() }
}
exit 0
