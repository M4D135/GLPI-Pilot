#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CacheDirectory,
    [string]$CabPath = '',
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'WebView2Runtime.ps1')
$manifest = Get-WebView2PinnedManifest
$CacheDirectory = [IO.Path]::GetFullPath($CacheDirectory); Assert-WebView2PlainPath $CacheDirectory
New-Item -ItemType Directory -Path $CacheDirectory -Force | Out-Null
$destination = Join-Path $CacheDirectory ('Microsoft.WebView2.FixedVersionRuntime.' + $manifest.version + '.x64')
if (Test-Path -LiteralPath $destination) {
    $verified = Test-WebView2Runtime $destination
    Write-Host ('Runtime en cache vérifié : ' + $verified.Version + ' ; ' + $verified.Files + ' fichiers.')
    Write-Output $verified.Path
    return
}
if (-not $IsWindows) { throw 'L''extraction officielle du CAB requiert expand.exe sous Windows. Un runtime déjà extrait peut être vérifié sur ce système.' }
$download = Join-Path $CacheDirectory ([IO.Path]::GetFileName(([Uri]$manifest.url).AbsolutePath))
$temporaryDownload = $null; $temporaryExtraction = $null
try {
    if ($CabPath) { $download = [IO.Path]::GetFullPath($CabPath); Assert-WebView2PlainPath $download }
    elseif (-not (Test-Path -LiteralPath $download -PathType Leaf)) {
        if ($Offline) { throw 'CAB absent du cache hors ligne. Fournissez -CabPath ou préparez ce cache sur une machine connectée.' }
        $temporaryDownload = $download + '.partial-' + [guid]::NewGuid().ToString('N')
        Write-Host ('Téléchargement explicite du runtime fixe ' + $manifest.version + ' depuis Microsoft. Conditions dans licenses/WebView2-FixedRuntime-LICENSE.html.')
        $handler = [Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect = $false
        $client = [Net.Http.HttpClient]::new($handler); $client.Timeout = [TimeSpan]::FromMinutes(10)
        $response = $null; $inputStream = $null; $outputStream = $null
        try {
            $response = $client.GetAsync($manifest.url, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            if (-not $response.IsSuccessStatusCode) { throw ('Téléchargement Microsoft refusé (HTTP ' + [int]$response.StatusCode + '). Aucun miroir ou redirection utilisé.') }
            if ($null -ne $response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -ne $manifest.bytes) { throw 'Taille annoncée du CAB inattendue.' }
            $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $outputStream = [IO.File]::Open($temporaryDownload, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $buffer = [byte[]]::new(1048576); [long]$total = 0
            $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(10))
            try {
                while (($read = $inputStream.ReadAsync($buffer, 0, $buffer.Length, $deadline.Token).GetAwaiter().GetResult()) -gt 0) {
                    $total += $read; if ($total -gt $manifest.bytes) { throw 'Le CAB dépasse la taille épinglée.' }
                    $outputStream.Write($buffer, 0, $read)
                }
            } finally { $deadline.Dispose() }
            if ($total -ne $manifest.bytes) { throw 'Le téléchargement du CAB est tronqué.' }
        } finally {
            if ($outputStream) { $outputStream.Dispose() }; if ($inputStream) { $inputStream.Dispose() }
            if ($response) { $response.Dispose() }; $client.Dispose(); $handler.Dispose()
        }
        if ((Get-FileHash -LiteralPath $temporaryDownload -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.sha256) { throw 'Empreinte du CAB Microsoft incorrecte.' }
        Move-Item -LiteralPath $temporaryDownload -Destination $download; $temporaryDownload = $null
    }
    if (-not (Test-Path -LiteralPath $download -PathType Leaf) -or (Get-Item -LiteralPath $download).Length -ne $manifest.bytes -or
        (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.sha256) { throw 'Le CAB local ne correspond pas au runtime Microsoft épinglé.' }
    $temporaryExtraction = Join-Path $CacheDirectory ('extract-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporaryExtraction | Out-Null
    $expand = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'expand.exe'
    & $expand $download '-F:*' $temporaryExtraction | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'expand.exe n''a pas pu extraire le CAB Microsoft.' }
    $verified = Test-WebView2Runtime $temporaryExtraction
    Move-Item -LiteralPath $verified.Path -Destination $destination
    Write-Host ('Runtime fixe préparé et vérifié : ' + $verified.Version + ' ; ' + $verified.Files + ' fichiers ; ' + $verified.Bytes + ' octets.')
    Write-Output $destination
} finally {
    if ($temporaryDownload -and (Test-Path -LiteralPath $temporaryDownload)) { Remove-Item -LiteralPath $temporaryDownload -Force }
    if ($temporaryExtraction -and (Test-Path -LiteralPath $temporaryExtraction)) { Remove-Item -LiteralPath $temporaryExtraction -Recurse -Force }
}
