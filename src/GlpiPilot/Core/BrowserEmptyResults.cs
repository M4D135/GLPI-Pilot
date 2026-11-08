using System.Text.Json.Serialization;

namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    private sealed class EmptySearchSnapshot
    {
        [JsonPropertyName("confirmed")] public bool Confirmed { get; set; }
        [JsonPropertyName("headers")] public List<string> Headers { get; set; } = new();
    }

    public async Task<ExtractionResult?> ReadEmptySearchAsync(TableChoice table, AppSettings settings, CancellationToken ct)
    {
        var frame = await ResolveFrameAsync(table, ct);
        var empty = await ReadJsonAsync<EmptySearchSnapshot>(frame, """
            selector => {
              const table = document.querySelector(selector);
              const form = table?.closest('form[data-search-itemtype][data-count]');
              if (!table?.matches('table.search-results') || form?.getAttribute('data-count')?.trim() !== '0') return {confirmed:false,headers:[]};
              const rows = [...table.querySelectorAll('thead tr')].filter(r => r.closest('table') === table);
              const row = rows.at(-1);
              // GLPI hides its header row for count=0. Read only its genuine labels,
              // excluding selection controls; never turn the 'No item found' row into data.
              const headers = row ? [...row.children].filter(c => /^(TH|TD)$/.test(c.tagName) && !c.hidden && c.style.display !== 'none' && c.getAttribute('aria-hidden') !== 'true' && !c.querySelector('input[type=checkbox]'))
                .map(c => (c.textContent || '').replace(/\s+/g,' ').trim()).filter(Boolean) : [];
              return {confirmed:true,headers};
            }
            """, table.Selector, ct);
        if (!empty.Confirmed) return null;
        var expected = settings.ExpectedTotal ?? 0;
        var result = new ExtractionResult
        {
            Headers = empty.Headers,
            SourceUrl = AppPaths.SafeUrl(frame.Url),
            Title = "Recherche GLPI vide",
            Scope = "Recherche vide confirmée par le total GLPI",
            ExpectedTotal = expected,
            Complete = expected == 0,
            Status = expected == 0 ? "Recherche vide confirmée : 0 résultat" : "Recherche vide : total attendu différent",
            Pages = 0
        };
        if (expected != 0) result.Warnings.Add($"GLPI annonce zéro résultat alors que le réglage Total attendu vaut {expected:N0}.");
        Log?.Invoke($"CONTRÔLE · recherche vide confirmée, {empty.Headers.Count} en-têtes disponibles ; aucune ligne fictive importée.");
        return result;
    }
}
