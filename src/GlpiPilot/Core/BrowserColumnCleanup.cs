namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    // This is an independent allowance between verified restoration steps,
    // not a deadline for the whole sequence of native removals. An abandoned
    // cleanup still stops; a long sequence of confirmed steps can finish.
    public static int ColumnCleanupBudgetSeconds(int listTimeoutSeconds) =>
        Math.Clamp(Math.Max(180, listTimeoutSeconds), 180, 600);

    // A cancelled collection must still get one bounded opportunity to restore
    // its own temporary preferences. The durable journal survives any failure.
    public async Task<ColumnRestorationResult> CompleteTemporaryColumnCollectionAsync(
        AppSettings settings, IProgress<ExtractionProgress>? progress)
    {
        try
        {
            using var cleanup = new TemporaryColumnCleanupWindow(
                TimeSpan.FromSeconds(ColumnCleanupBudgetSeconds(settings.ListTimeoutSeconds) + 30));
            return await RestoreTemporaryColumnsCoreAsync(settings, progress, cleanup.Token, cleanup.ConfirmProgress);
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

// Renew only after a native document has been validated or a command's exact
// effect has been persisted and journaled. UI notices, polls and unconfirmed
// commands cannot extend this independent cancellation-cleanup allowance.
internal sealed class TemporaryColumnCleanupWindow : IDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private readonly TimeSpan _allowance;

    public TemporaryColumnCleanupWindow(TimeSpan allowance, TimeProvider? timeProvider = null)
    {
        if (allowance <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(allowance));
        _allowance = allowance;
        _cancellation = new(allowance, timeProvider ?? TimeProvider.System);
    }

    public CancellationToken Token => _cancellation.Token;

    public void ConfirmProgress()
    {
        // CancelAfter never resurrects an expired source. Retain the explicit
        // guard so a late response cannot be mistaken for a new cleanup run.
        if (!_cancellation.IsCancellationRequested) _cancellation.CancelAfter(_allowance);
    }

    public void Dispose() => _cancellation.Dispose();
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
