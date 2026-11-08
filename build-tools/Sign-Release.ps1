#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [string]$CertificateThumbprint = $env:GLPI_SIGN_CERT_THUMBPRINT,
    [string]$TimestampServer = $env:GLPI_SIGN_TIMESTAMP_URL,
    [switch]$Required
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { throw 'Executable a signer introuvable.' }
if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    if ($Required) { throw 'Signature exigee : configurez GLPI_SIGN_CERT_THUMBPRINT avec un certificat de signature de code approuve.' }
    Write-Host 'SIGNATURE=NotConfigured ; executable non signe, empreintes de provenance uniquement.'
    return
}
if ($CertificateThumbprint -notmatch '^[A-Fa-f0-9]{40}$') { throw 'Empreinte du certificat Windows invalide (40 caracteres hexadecimaux).' }
if ([string]::IsNullOrWhiteSpace($TimestampServer)) { throw 'Configurez un serveur d horodatage autorise dans GLPI_SIGN_TIMESTAMP_URL.' }
$uri = $null
if (-not [Uri]::TryCreate($TimestampServer, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -notin @('http', 'https') -or $uri.UserInfo -or $uri.Fragment) { throw 'Adresse de serveur d horodatage invalide.' }
$certificate = @(Get-ChildItem -Path Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Thumbprint -eq $CertificateThumbprint })
if ($certificate.Count -ne 1 -or -not $certificate[0].HasPrivateKey) { throw 'Le certificat de signature de code et sa cle privee doivent etre disponibles dans Cert:\CurrentUser\My.' }
if ($certificate[0].NotAfter -le [DateTime]::Now -or $certificate[0].NotBefore -gt [DateTime]::Now) { throw 'Le certificat de signature ne couvre pas la date courante.' }
# Microsoft Authenticode performs the actual signature; no self-signed fallback is generated.
# https://learn.microsoft.com/powershell/module/microsoft.powershell.security/set-authenticodesignature
$result = Set-AuthenticodeSignature -LiteralPath $ExecutablePath -Certificate $certificate[0] -HashAlgorithm SHA256 -IncludeChain NotRoot -TimestampServer $TimestampServer
if ($result.Status -ne 'Valid') { throw ('Signature non valide : ' + $result.Status + ' ; ' + $result.StatusMessage) }
$verified = Get-AuthenticodeSignature -LiteralPath $ExecutablePath
if ($verified.Status -ne 'Valid' -or $verified.SignerCertificate.Thumbprint -ne $CertificateThumbprint -or $null -eq $verified.TimeStamperCertificate) { throw 'La signature ou son horodatage ne peut pas etre confirme.' }
$algorithm = [Security.Cryptography.SHA256]::Create()
try { $publisher = ([BitConverter]::ToString($algorithm.ComputeHash($verified.SignerCertificate.RawData))).Replace('-', '').ToLowerInvariant() }
finally { $algorithm.Dispose() }
Write-Host ('SIGNATURE=Valid ; PUBLISHER_CERT_SHA256=' + $publisher)
Write-Host ('SIGNED_EXE_SHA256=' + (Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant())
