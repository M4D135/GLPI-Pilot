using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private void RegisterPresentationOperations()
    {
        Register("dashboard.presentation.get", (args, ct) =>
        {
            var run = RequireAnalyticsRun(args); var board = AnalyticsPresentationBoard(run, ct); ct.ThrowIfCancellationRequested();
            return Task.FromResult<object?>(new { deck = board.Presentation?.Copy(), fingerprint = AnalyticsPresentationFingerprint(board.Presentation), revision = Revision });
        });
        Register("dashboard.presentation.save", (args, ct) =>
        {
            var run = RequireAnalyticsRun(args); var board = AnalyticsPresentationBoard(run, ct);
            if (Text(args, "baseFingerprint", 64) != AnalyticsPresentationFingerprint(board.Presentation))
                throw new InvalidOperationException("La présentation a changé depuis son ouverture. Rechargez-la avant de l’enregistrer.");
            if (!args.TryGetProperty("deck", out var value)) throw new InvalidDataException("La composition à enregistrer est absente.");
            var deck = value.ValueKind == JsonValueKind.Null ? null : Read<DashboardPresentationSettings>(value);
            deck?.Validate(AnalyticsVisiblePresentationWidgets(run));
            var next = Settings.Copy(); board.Presentation = deck?.Copy();
            if (run.BusinessView.Length > 0)
            {
                next.Dashboard.Business.BoardOverrides.RemoveAll(item => item.Id == board.Id);
                next.Dashboard.Business.BoardOverrides.Add(board.Copy()); next.Dashboard.Business.Validate();
            }
            else
            {
                var layout = run.TechnicianKey.Length > 0 ? next.Dashboard.PersonalLayout : next.Dashboard.Layout;
                var index = layout.Boards.FindIndex(item => item.Id == board.Id);
                if (index < 0) throw new InvalidOperationException("Ce tableau n’existe plus.");
                layout.Boards[index] = board.Copy(); layout.Validate();
            }
            ct.ThrowIfCancellationRequested(); CommitSettings(next);
            // A slide placement does not change any indicator or source interpretation.
            run.StoredBoard.Presentation = deck?.Copy(); run.Board.Presentation = deck?.Copy();
            if (run.BusinessView.Length > 0) _businessLayoutJournals.Remove(run.BusinessView);
            else _analyticsLayoutJournals.Remove(run.TechnicianKey.Length > 0);
            return Task.FromResult<object?>(new { deck = deck?.Copy(), fingerprint = AnalyticsPresentationFingerprint(deck), revision = Revision, settings = Settings });
        });
    }

    private DashboardBoardSettings AnalyticsPresentationBoard(AnalyticsRun run, CancellationToken ct)
    {
        var layout = run.BusinessView.Length > 0 ? AnalyticsBusinessLayout(run.BusinessView, ct) : AnalyticsLayout(run.TechnicianKey.Length > 0).Copy();
        var board = layout.Boards.FirstOrDefault(item => item.Id == run.StoredBoard.Id)
            ?? throw new InvalidOperationException("Ce tableau n’existe plus.");
        var expected = run.StoredBoard.Copy(); var actual = board.Copy(); expected.Presentation = null; actual.Presentation = null;
        if (JsonSerializer.Serialize(expected, Json) != JsonSerializer.Serialize(actual, Json))
            throw new InvalidOperationException("Les widgets du tableau ont changé. Recalculez le tableau avant de préparer sa présentation.");
        return board;
    }

    private static string AnalyticsPresentationFingerprint(DashboardPresentationSettings? deck)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(deck, Json))));

    private static IEnumerable<string> AnalyticsVisiblePresentationWidgets(AnalyticsRun run)
        => run.Board.Widgets.Where(widget => !widget.Hidden && !run.Board.Sections.Any(section => section.Hidden && section.Key == widget.Section)
            && run.Widgets.ContainsKey(widget.Id)).Select(widget => widget.Id);

    private static DashboardPresentationSettings? AnalyticsReportPresentation(AnalyticsRun run, IReadOnlyList<DashboardWidgetSettings> selection, JsonElement args)
    {
        if (!args.TryGetProperty("presentation", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var deck = Read<DashboardPresentationSettings>(value); deck.Validate(AnalyticsVisiblePresentationWidgets(run));
        if (deck.Slides.Count == 0 || deck.Slides.Any(slide => slide.Items.Count == 0))
            throw new InvalidDataException("Ajoutez au moins un widget à chaque diapositive avant l’export.");
        if (!deck.WidgetIds().SequenceEqual(selection.Select(widget => widget.Id), StringComparer.Ordinal))
            throw new InvalidDataException("La sélection du rapport doit correspondre aux widgets de la présentation, dans leur ordre de première apparition.");
        return deck.Copy();
    }
}
