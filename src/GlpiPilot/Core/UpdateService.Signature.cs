using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

public static partial class UpdateService
{
    /// <summary>Uses Windows Authenticode trust evaluation, never treats the certificate embedded in a file as trust by itself.</summary>
    public static async Task<UpdateSignature> CheckSignatureAsync(string executable, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return new("Unavailable", "", "");
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) return new("Unavailable", "", "");
        // The file path is passed as an environment value, never interpolated into shell code.
        // Use this PowerShell installation's modules: a pwsh -> .NET -> powershell.exe
        // process chain otherwise inherits incompatible PowerShell 7 module paths.
        // https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_psmodulepath
        const string command = """
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            try {
                Import-Module ([IO.Path]::Combine($PSHOME, 'Modules', 'Microsoft.PowerShell.Security', 'Microsoft.PowerShell.Security.psd1')) -ErrorAction Stop
                Import-Module ([IO.Path]::Combine($PSHOME, 'Modules', 'Microsoft.PowerShell.Utility', 'Microsoft.PowerShell.Utility.psd1')) -ErrorAction Stop
                $signature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $env:GLPI_UPDATE_VERIFY_FILE -ErrorAction Stop
                $hash = ''; $publisher = ''
                if ($null -ne $signature.SignerCertificate) {
                    $publisher = $signature.SignerCertificate.Subject
                    $algorithm = [System.Security.Cryptography.SHA256]::Create()
                    try { $hash = ([BitConverter]::ToString($algorithm.ComputeHash($signature.SignerCertificate.RawData))).Replace('-', '').ToLowerInvariant() }
                    finally { $algorithm.Dispose() }
                }
                $result = [pscustomobject]@{ Status = $signature.Status.ToString(); Publisher = $publisher; CertificateSha256 = $hash }
                [Console]::Out.WriteLine(($result | Microsoft.PowerShell.Utility\ConvertTo-Json -Compress))
                exit 0
            } catch {
                [Console]::Error.WriteLine($_.Exception.Message)
                exit 1
            }
            """;
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        start.Environment.Remove("PSModulePath");
        start.Environment["GLPI_UPDATE_VERIFY_FILE"] = Path.GetFullPath(executable);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct); budget.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(start) ?? throw new IOException("Le contrôle de signature Windows n’a pas démarré.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(budget.Token); var error = process.StandardError.ReadToEndAsync(budget.Token);
            await process.WaitForExitAsync(budget.Token);
            return ParseSignatureResponse(process.ExitCode, await output, await error);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await process.WaitForExitAsync(cleanup.Token); } }
    }

    internal static UpdateSignature ParseSignatureResponse(int exitCode, string output, string error)
    {
        if (exitCode != 0)
        {
            var detail = AppPaths.SafeError(new InvalidOperationException(error.Length == 0 ? "Aucun détail fourni par Windows PowerShell." : error));
            if (detail.Length > 1000) detail = detail[..1000] + "…";
            throw new InvalidDataException($"Windows n’a pas pu vérifier la signature du pack (code de sortie {exitCode}). {detail}");
        }
        if (output.Length > 16384) throw new InvalidDataException("Réponse de vérification de signature trop volumineuse.");
        UpdateSignature signature;
        try { signature = JsonSerializer.Deserialize<UpdateSignature>(output) ?? throw new InvalidDataException("Réponse de vérification de signature vide."); }
        catch (JsonException errorResponse) { throw new InvalidDataException("Réponse de vérification de signature invalide.", errorResponse); }
        if (signature.Status == null || signature.Publisher == null || signature.CertificateSha256 == null || signature.Status.Length > 80 || signature.Publisher.Length > 2000
            || signature.CertificateSha256.Length > 0 && !IsHash(signature.CertificateSha256) || signature.Status == "Valid" && !IsHash(signature.CertificateSha256)) throw new InvalidDataException("Réponse de signature invalide.");
        return signature;
    }
}
