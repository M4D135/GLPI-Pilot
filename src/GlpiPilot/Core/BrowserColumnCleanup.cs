namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    // A cancelled collection must still get one bounded opportunity to restore
    // its own temporary preferences. The durable journal survives any failure.
    public async Task<ColumnRestorationResult> CompleteTemporaryColumnCollectionAsync(
        AppSettings settings, IProgress<ExtractionProgress>? progress)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(
                TimeSpan.FromSeconds(Math.Clamp(settings.ListTimeoutSeconds, 10, 600)));
            return await RestoreTemporaryColumnsAsync(settings, progress, cleanup.Token);
        }
        catch (Exception error)
        {
            var message = "La vue GLPI n’a pas pu être entièrement restaurée. La récupération sera reprise avant la prochaine collecte. "
                + AppPaths.SafeError(error);
            // Cleanup diagnostics cannot replace the collection's original
            // error if the UI was disposed while cancellation was finishing.
            try { Log?.Invoke("COLONNES · " + message); } catch { }
            try { progress?.Report(new(0, 0, null, message)); } catch { }
            return new("pending", message, Pending: true);
        }
    }
}

public static class ColumnRestorationReceipt
{
    public static void Apply(ExtractionResult data, ColumnRestorationResult restoration)
    {
        var receipt = data.Provenance.ColumnPreparation;
        if (receipt is null)
        {
            if (restoration.State == "not-needed" && !restoration.Pending) return;
            receipt = data.Provenance.ColumnPreparation = new();
        }
        receipt.TemporaryView = receipt.TemporaryView || receipt.AddedCount > 0 || restoration.RestoredCount > 0 || restoration.Pending
            || restoration.State is "restored" or "restored-preserving-changes";
        receipt.RestorationState = restoration.State;
        receipt.RestorationMessage = restoration.Message;
        receipt.RestoredCount = restoration.RestoredCount;
        receipt.RecoveryPending = restoration.Pending;
        if (restoration.Pending && restoration.Message.Length > 0)
        {
            if (!receipt.Warnings.Contains(restoration.Message, StringComparer.Ordinal)) receipt.Warnings.Add(restoration.Message);
            if (!data.Warnings.Contains(restoration.Message, StringComparer.Ordinal)) data.Warnings.Add(restoration.Message);
        }
    }
}
