using System.Diagnostics;
using System.IO;
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
        const string command = "$ErrorActionPreference='Stop'; $s=Get-AuthenticodeSignature -LiteralPath $env:GLPI_UPDATE_VERIFY_FILE; $h=''; $n=''; if($null -ne $s.SignerCertificate){$n=$s.SignerCertificate.Subject; $a=[System.Security.Cryptography.SHA256]::Create(); try{$h=([BitConverter]::ToString($a.ComputeHash($s.SignerCertificate.RawData))).Replace('-','').ToLowerInvariant()}finally{$a.Dispose()}}; [pscustomobject]@{Status=$s.Status.ToString(); Publisher=$n; CertificateSha256=$h}|ConvertTo-Json -Compress";
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command"); start.ArgumentList.Add(command); start.Environment["GLPI_UPDATE_VERIFY_FILE"] = Path.GetFullPath(executable);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct); budget.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(start) ?? throw new IOException("Le contrôle de signature Windows n’a pas démarré.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(budget.Token); var error = process.StandardError.ReadToEndAsync(budget.Token); await process.WaitForExitAsync(budget.Token); var text = await output; _ = await error;
            if (process.ExitCode != 0 || text.Length > 16384) throw new InvalidDataException("Windows n’a pas pu vérifier la signature du pack.");
            var signature = JsonSerializer.Deserialize<UpdateSignature>(text) ?? throw new InvalidDataException("Réponse de vérification de signature vide.");
            if (signature.Status == null || signature.Publisher == null || signature.CertificateSha256 == null || signature.Status.Length > 80 || signature.Publisher.Length > 2000
                || signature.CertificateSha256.Length > 0 && !IsHash(signature.CertificateSha256) || signature.Status == "Valid" && !IsHash(signature.CertificateSha256)) throw new InvalidDataException("Réponse de signature invalide.");
            return signature;
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await process.WaitForExitAsync(cleanup.Token); } }
    }
}
