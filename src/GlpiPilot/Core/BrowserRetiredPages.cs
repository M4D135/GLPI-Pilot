using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    private sealed class CollectionPageLineage
    {
        public IPage? Opener;
        public bool Known;
        public bool Retired;
    }

    private readonly object _collectionPagesGate = new();
    private readonly Dictionary<IPage, CollectionPageLineage> _collectionPages = new(ReferenceEqualityComparer.Instance);

    private void TrackCollectionPage(IPage page)
    {
        CollectionPageLineage lineage;
        lock (_collectionPagesGate)
        {
            if (!_collectionPages.TryGetValue(page, out lineage!))
                _collectionPages.Add(page, lineage = new());
        }
        _ = ObserveCollectionOpenerAsync(page, lineage);
    }

    private async Task ObserveCollectionOpenerAsync(IPage page, CollectionPageLineage lineage)
    {
        try
        {
            var opener = await page.OpenerAsync();
            lock (_collectionPagesGate)
            {
                if (!_collectionPages.TryGetValue(page, out var current) || !ReferenceEquals(current, lineage) || current.Known) return;
                current.Opener = opener; current.Known = true;
                IsRetiredCollectionPageLocked(page);
            }
        }
        catch (PlaywrightException) { /* An unresolved family is not reused after a retirement. */ }
    }

    private void RecordCollectionOpener(IPage page, IPage? opener)
    {
        lock (_collectionPagesGate)
        {
            if (!_collectionPages.TryGetValue(page, out var lineage)) _collectionPages.Add(page, lineage = new());
            // Popup provides the actual parent even if OpenerAsync has already
            // returned null because that parent closed in the meantime.
            lineage.Opener = opener; lineage.Known = true;
            IsRetiredCollectionPageLocked(page);
        }
    }

    private bool IsRetiredCollectionPageLocked(IPage page)
    {
        var visited = new HashSet<IPage>(ReferenceEqualityComparer.Instance);
        for (IPage? ancestor = page; ancestor is not null && visited.Add(ancestor);)
        {
            if (!_collectionPages.TryGetValue(ancestor, out var lineage)) return false;
            if (lineage.Retired)
            {
                if (_collectionPages.TryGetValue(page, out var child)) child.Retired = true;
                return true;
            }
            if (!lineage.Known) return false;
            ancestor = lineage.Opener;
        }
        return false;
    }

    private bool CanUseCollectionPage(IPage page)
    {
        if (page.IsClosed || IsTicketDetailPage(page)) return false;
        lock (_collectionPagesGate)
        {
            if (IsRetiredCollectionPageLocked(page)) return false;
            if (!_collectionPages.Values.Any(lineage => lineage.Retired)) return true;
            // While an opener is unresolved, do not let a late popup compete
            // with the fresh search. An unrelated proved root remains usable.
            var visited = new HashSet<IPage>(ReferenceEqualityComparer.Instance);
            for (IPage? ancestor = page; ancestor is not null;)
            {
                if (!visited.Add(ancestor) || !_collectionPages.TryGetValue(ancestor, out var lineage) || !lineage.Known) return false;
                if (lineage.Retired) return false;
                ancestor = lineage.Opener;
            }
            return true;
        }
    }

    internal void RetireNativeExportPageFamily(IPage source)
    {
        _freshCollectionPageRequired = true;
        lock (_collectionPagesGate)
        {
            if (!_collectionPages.TryGetValue(source, out var lineage)) _collectionPages.Add(source, lineage = new());
            lineage.Retired = true;
            foreach (var page in _collectionPages.Keys.ToArray()) IsRetiredCollectionPageLocked(page);
        }
        if (ReferenceEquals(_activePage, source)) _activePage = null;
    }

    private async Task<IPage> NewCollectionPageAsync(IBrowserContext context)
    {
        var page = await context.NewPageAsync();
        RecordCollectionOpener(page, null); // Context.NewPage creates an independent root.
        return page;
    }

    private void ForgetCollectionContext(IBrowserContext context)
    {
        lock (_collectionPagesGate)
        {
            // Keep retired ancestors through Page.Close: a late Popup event can
            // still name them after OpenerAsync would have lost that relation.
            foreach (var page in _collectionPages.Keys.Where(page => ReferenceEquals(page.Context, context)).ToArray())
                _collectionPages.Remove(page);
        }
    }
}
