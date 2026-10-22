using System.IO;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    public async Task DownloadTicketDocumentAsync(TicketReference ticket, TicketObservedItem document, AppSettings settings, string path, CancellationToken ct)
    {
        _ = ValidateTicketReference(ticket, settings);
        var address = TicketLinkPolicy.Validate(ticket, document);
        if (document.Kind != "document") throw new InvalidOperationException("Sélectionnez un document lu dans la fiche.");
        await _ticketReadGate.WaitAsync(ct);
        var temporary = Path.GetFullPath(path) + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            if (_context == null) await OpenAsync(settings, ct, background: true);
            if (_context == null) throw new InvalidOperationException("Session navigateur indisponible.");
            for (var redirect = 0; redirect <= 3; redirect++)
            {
                var response = await _context.APIRequest.GetAsync(address.AbsoluteUri, new() { MaxRedirects = 0, Timeout = Math.Clamp(settings.DownloadTimeoutSeconds, 10, 3600) * 1000 }).WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (response.Status is >= 300 and < 400)
                    {
                        if (!response.Headers.TryGetValue("location", out var location) || redirect == 3) throw new InvalidOperationException("Le téléchargement redirige sans destination exploitable.");
                        var next = new Uri(address, location);
                        address = TicketLinkPolicy.Validate(ticket, new TicketObservedItem { Kind = document.Kind, Identity = document.Identity, Url = next.AbsoluteUri });
                        continue;
                    }
                    if (!response.Ok) throw new InvalidOperationException("Le document n’est pas accessible dans la session courante (HTTP " + response.Status + ").");
                    if (response.Headers.TryGetValue("content-type", out var media) && media.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Le serveur renvoie une page HTML ; vérifiez l’authentification avant de télécharger ce document.");
                    const int limit = 50 * 1024 * 1024;
                    if (response.Headers.TryGetValue("content-length", out var size) && long.TryParse(size, out var length) && length > limit)
                        throw new InvalidOperationException("Ce document dépasse 50 Mo ; ouvrez-le directement dans GLPI.");
                    var bytes = await response.BodyAsync().WaitAsync(ct);
                    if (bytes.Length > limit) throw new InvalidOperationException("Ce document dépasse 50 Mo ; ouvrez-le directement dans GLPI.");
                    await File.WriteAllBytesAsync(temporary, bytes, ct); ct.ThrowIfCancellationRequested();
                    File.Move(temporary, path, overwrite: true); return;
                }
                finally { await response.DisposeAsync(); }
            }
        }
        catch (PlaywrightException) { throw new InvalidOperationException("Le téléchargement du document a échoué. Vérifiez la session GLPI ; aucun fichier final n’a été conservé."); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _ticketReadGate.Release();
        }
    }

    public async Task OpenRelatedObjectAsync(TicketReference ticket, TicketObservedItem relation, AppSettings settings, CancellationToken ct)
    {
        _ = ValidateTicketReference(ticket, settings); var target = TicketLinkPolicy.Validate(ticket, relation);
        if (relation.Kind != "relation") throw new InvalidOperationException("Sélectionnez une relation lue dans la fiche.");
        if (_context == null) await OpenAsync(settings, ct, background: true);
        if (_context == null) throw new InvalidOperationException("Session navigateur indisponible.");
        var page = await _context.NewPageAsync().WaitAsync(ct); OwnTicketPage(page);
        try
        {
            await page.GotoAsync(target.AbsoluteUri, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = Math.Clamp(settings.DetailTimeoutSeconds, 5, 600) * 1000 }).WaitAsync(ct);
            await ShowBrowserAsync(page, ct);
        }
        catch { if (!page.IsClosed) await page.CloseAsync(); throw; }
    }
}
