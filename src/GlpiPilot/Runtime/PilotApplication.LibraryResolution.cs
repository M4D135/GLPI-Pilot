using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private void RegisterLibraryResolutionOperations()
    {
        Register("library.reference", async (args, ct) =>
        {
            var reference = new TicketReference(Text(args, "id", 20), Text(args, "url", 8192));
            var key = TicketLibraryRules.Key(reference);
            var document = await TicketLibrary.GetAsync(key, ct).ConfigureAwait(false);
            return new { found = document is not null, key = document?.Key, ticket = document?.Seed.Ticket ?? reference,
                installation = ObjectIdentity.Installation(reference.Url) };
        });
        Register("library.resolution", async (args, ct) => await TicketLibraryResolutionService.FindAsync(
            TicketLibrary, Text(args, "key", 200), args.TryGetProperty("text", out _) ? Text(args, "text", 300) : null, ct).ConfigureAwait(false));
    }
}
