using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private void RegisterLibraryCaptureJournalOperations()
    {
        Register("library.capture.latest", async (_, ct) => await TicketLibrary.LatestCaptureAsync(ct).ConfigureAwait(false));
        Register("library.capture.entries", async (a, ct) => await TicketLibrary.CaptureEntriesAsync(
            Text(a, "runId", 32), Number(a, "offset", 0, 0, TicketLibraryStore.CaptureTicketLimit), Number(a, "limit", 50, 1, 100), Flag(a, "retryOnly"), ct).ConfigureAwait(false));
    }

    private static TicketLibraryCaptureContext LibraryCaptureContext(AppSettings settings, LibraryPreferences preferences)
        => TicketLibraryCaptureContext.FromSettings(settings, preferences);
}
