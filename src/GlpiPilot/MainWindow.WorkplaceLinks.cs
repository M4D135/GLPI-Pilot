using System.IO;
using GlpiPilot.Core;
using Microsoft.Win32;

namespace GlpiPilot;

public partial class MainWindow
{
    private void InitializeTicketLinks()
    {
        _ticketWorkplaceView!.Insights.RelationRequested += async item =>
        {
            if (_busy || _closing || _ticketDetail == null) return;
            var detail = _ticketDetail;
            try
            {
                _ = TicketLinkPolicy.Validate(detail.Ticket, item);
                if (item.RelatedType == "ticket") { QueuePersonalTicketReference(new(item.Identity, item.Url), false); return; }
                _presentingBrowser = true; Topmost = false;
                await RunAsync("Ouverture de la relation GLPI…", async ct => await _browser.OpenRelatedObjectAsync(detail.Ticket, item, ReadSettings(), ct));
            }
            catch (Exception error) { ReportTicketError(error); }
            finally { _presentingBrowser = false; if (!_closing) UpdateBrowserButton(); }
        };
        _ticketWorkplaceView.Insights.DocumentRequested += async item =>
        {
            if (_busy || _closing || _ticketDetail == null) return;
            var detail = _ticketDetail;
            try
            {
                _ = TicketLinkPolicy.Validate(detail.Ticket, item);
                var filename = new string(Path.GetFileName(item.Title).Where(c => !Path.GetInvalidFileNameChars().Contains(c)).Take(180).ToArray());
                var dialog = new SaveFileDialog { Title = "Enregistrer le document GLPI sélectionné", FileName = filename.Length > 0 ? filename : "document-" + item.Identity, Filter = "Tous les fichiers|*.*", OverwritePrompt = true };
                if (dialog.ShowDialog(this) != true) return;
                await RunAsync("Téléchargement du document sélectionné…", async ct =>
                {
                    await _browser.DownloadTicketDocumentAsync(detail.Ticket, item, ReadSettings(), dialog.FileName, ct);
                    SetStatus("Document sélectionné enregistré : " + dialog.FileName);
                });
            }
            catch (Exception error) { ReportTicketError(error); }
        };
    }
}
