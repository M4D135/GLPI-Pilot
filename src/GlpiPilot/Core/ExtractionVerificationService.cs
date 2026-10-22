using System.IO;

namespace GlpiPilot.Core;

public sealed record NativeSampleVerification(bool Available, bool Matches, int Sample, int Missing, string Explanation);

/// <summary>Local acquisition checks. No credentials or downloaded values are logged.</summary>
public static class ExtractionVerificationService
{
    public static void Preflight(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings); ct.ThrowIfCancellationRequested();
        TargetUrlPolicy.Validate(settings.PortalUrl);
        if (settings.BrowserChannel is not ("msedge" or "chrome")) throw new InvalidOperationException("Choisissez Edge ou Chrome dans les réglages du navigateur.");
        if (settings.AutoMode is not ("native" or "pagination")) throw new InvalidOperationException("La méthode d’extraction configurée est inconnue.");
        if (settings.TimeoutSeconds is < 1 or > 1800 || settings.DownloadTimeoutSeconds is < 1 or > 7200)
            throw new InvalidOperationException("Les délais configurés doivent être compris entre 1 et 1 800 s pour le SSO, et entre 1 et 7 200 s pour le téléchargement.");
        if (settings.MaxPages is < 1 or > 10000 || settings.MaxRows is < 1 or > 1000000 || settings.ExpectedTotal is < 0 or > 10000000)
            throw new InvalidOperationException("Vérifiez les limites de pages, de lignes et le total attendu dans les réglages.");
        if (settings.ExpectedTotal > settings.MaxRows)
            throw new InvalidOperationException("Le total attendu dépasse la limite de lignes. Augmentez la limite ou réduisez la recherche avant de commencer.");
        if (settings.AutoMode == "native" && settings.NativeSubmitSelector.Length == 0
            && (settings.NativeMenuSelector.Length > 0 || settings.NativeFormatSelector.Length > 0 || settings.NativeScopeSelector.Length > 0))
            throw new InvalidOperationException("Les repères d’export personnalisés sont incomplets : renseignez le bouton d’export ou effacez les repères pour revenir à la détection automatique.");
        if (string.IsNullOrWhiteSpace(settings.ExportFolder)) throw new InvalidOperationException("Choisissez un dossier d’export avant de lancer l’extraction.");
        try
        {
            Directory.CreateDirectory(settings.ExportFolder);
            var probe = Path.Combine(settings.ExportFolder, ".glpi-write-check-" + Guid.NewGuid().ToString("N"));
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            stream.WriteByte(0); stream.Flush();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new InvalidOperationException("Le dossier d’export n’est pas accessible en écriture. Choisissez un dossier disponible dans Réglages. " + AppPaths.SafeError(error)); }
        ct.ThrowIfCancellationRequested();
    }

    public static NativeSampleVerification VerifyNativeSample(TableSnapshot visible, ExtractionResult downloaded,
        DashboardSettings settings, CancellationToken ct = default)
    {
        var shownColumn = DashboardService.ResolveColumns(visible.Headers, settings).FirstOrDefault(x => x.Role == "id")?.Index;
        var nativeColumn = DashboardService.ResolveColumns(downloaded.Headers, settings).FirstOrDefault(x => x.Role == "id")?.Index;
        if (shownColumn is not int left || nativeColumn is not int right)
            return new(false, false, 0, 0, "Rapprochement d’identifiants indisponible : la colonne ID doit être présente et non ambiguë dans la liste et dans l’export.");
        var sample = new HashSet<string>(StringComparer.Ordinal);
        var stride = Math.Max(1, visible.Rows.Count / 25);
        for (var row = 0; row < visible.Rows.Count && sample.Count < 25; row += stride)
        {
            ct.ThrowIfCancellationRequested();
            if (left >= visible.Rows[row].Length || NormalizeId(visible.Rows[row][left]) is not { Length: > 0 } id)
                return new(false, false, sample.Count, 0, "Rapprochement indisponible : l’échantillon visible contient un identifiant illisible.");
            sample.Add(id);
        }
        if (sample.Count == 0) return new(false, false, 0, 0, "La liste affichée ne fournit aucun identifiant à rapprocher.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in downloaded.Rows)
        {
            ct.ThrowIfCancellationRequested();
            if (right < row.Length)
            {
                var id = NormalizeId(row[right]);
                if (sample.Contains(id)) found.Add(id);
            }
            if (found.Count == sample.Count) break;
        }
        var missing = sample.Count - found.Count;
        return new(true, missing == 0, sample.Count, missing, missing == 0
            ? $"Les {sample.Count} identifiants de l’échantillon visible sont présents dans l’export. Ce contrôle complète le comptage ; il ne prouve pas à lui seul l’identité de toute la recherche."
            : $"{missing} identifiant(s) de la liste affichée manquent dans l’export sur un échantillon de {sample.Count}. Le fichier reçu peut correspondre à un autre périmètre ou à une recherche ayant évolué.");
    }

    private static string NormalizeId(string value)
    {
        try { return TicketReferenceResolver.NormalizeId(value); }
        catch (InvalidOperationException) { return ""; }
    }
}

/// <summary>A held OS file handle protects the dedicated profile; stale files alone never block recovery.</summary>
internal sealed class BrowserProfileLease : IDisposable
{
    private readonly FileStream _stream;
    private BrowserProfileLease(FileStream stream) => _stream = stream;
    public static BrowserProfileLease Acquire(string profile)
    {
        try
        {
            Directory.CreateDirectory(profile);
            return new(new FileStream(Path.Combine(profile, "pilot-session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (UnauthorizedAccessException)
        { throw new InvalidOperationException("Le profil navigateur réservé à GLPI Pilot n’est pas accessible en écriture. Vérifiez les droits de votre dossier utilisateur."); }
        catch (IOException)
        { throw new InvalidOperationException("Le profil navigateur réservé à GLPI Pilot est occupé ou indisponible. Fermez l’autre instance de GLPI Pilot et son navigateur, puis réessayez. Aucun profil n’a été supprimé."); }
    }
    public void Dispose() => _stream.Dispose();
}
