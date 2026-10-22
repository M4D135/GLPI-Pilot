using System.Data;
using System.Globalization;

namespace GlpiPilot.Core;

/// <summary>The displayed indices always refer to the original extraction, never to a sorted view.</summary>
public sealed record PreviewQueryResult(int[] SourceIndices, int MatchCount, int ScopeCount, int SourceCount)
{
    public int DisplayedCount => SourceIndices.Length;
    public bool IsLimited => DisplayedCount < MatchCount;
}

/// <summary>A bounded view with stable DataRow identity across DataView sorting. The source remains unchanged.</summary>
public sealed class PreviewQueryView : IDisposable
{
    private readonly Dictionary<DataRow, (int Index, string[]? Values)> _sourceIndices;
    private readonly List<string[]> _sourceRows;
    private readonly int _sourceRowCount;
    private readonly string[] _headers;
    private bool _disposed;
    public ExtractionResult Source { get; }
    public DataTable Table { get; }
    public PreviewQueryResult Query { get; }

    internal PreviewQueryView(ExtractionResult source, DataTable table, Dictionary<DataRow, (int Index, string[]? Values)> sourceIndices, PreviewQueryResult query)
    {
        Source = source; Table = table; _sourceIndices = sourceIndices; Query = query;
        _sourceRows = source.Rows; _sourceRowCount = source.Rows.Count; _headers = source.Headers.ToArray();
    }

    public int? GetSourceIndex(DataRow row, ExtractionResult? currentSource)
    {
        if (_disposed || !ReferenceEquals(currentSource, Source) || !ReferenceEquals(row.Table, Table)
            || row.RowState is DataRowState.Detached or DataRowState.Deleted) return null;
        if (!ReferenceEquals(Source.Rows, _sourceRows) || Source.Rows.Count != _sourceRowCount
            || !Source.Headers.SequenceEqual(_headers, StringComparer.Ordinal)
            || !_sourceIndices.TryGetValue(row, out var reference) || reference.Index < 0 || reference.Index >= Source.Rows.Count
            || !ReferenceEquals(Source.Rows[reference.Index], reference.Values)) return null;
        // Fail closed if a future caller mutates an extraction instead of replacing it with a new instance.
        for (var column = 0; column < _headers.Length; column++)
        {
            var original = reference.Values != null && column < reference.Values.Length ? reference.Values[column] ?? "" : "";
            if (!string.Equals(row[column] as string, original, StringComparison.Ordinal)) return null;
        }
        return reference.Index;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sourceIndices.Clear();
        Table.Dispose();
    }
}

/// <summary>Read-only literal search over the full extraction or an explicit dashboard scope.</summary>
public static class PreviewQueryService
{
    public static PreviewQueryView CreateView(ExtractionResult source, string? search, int displayLimit = 10000,
        IReadOnlyList<int>? sourceIndices = null, CultureInfo? culture = null, CancellationToken ct = default, PreviewTransform? transform = null)
    {
        var query = Query(source, search, displayLimit, sourceIndices, ct, transform, culture);
        var table = new DataTable { Locale = culture ?? CultureInfo.CurrentCulture };
        var indices = new Dictionary<DataRow, (int Index, string[]? Values)>(query.DisplayedCount);
        try
        {
            for (var column = 0; column < source.Headers.Count; column++)
            {
                if ((column & 127) == 0) ct.ThrowIfCancellationRequested();
                table.Columns.Add(new DataColumn("C" + column.ToString(CultureInfo.InvariantCulture), typeof(string)) { Caption = source.Headers[column] ?? "" });
            }
            table.BeginLoadData();
            for (var position = 0; position < query.SourceIndices.Length; position++)
            {
                if ((position & 127) == 0) ct.ThrowIfCancellationRequested();
                var sourceIndex = query.SourceIndices[position];
                var values = source.Rows[sourceIndex];
                var row = table.NewRow();
                for (var column = 0; column < source.Headers.Count; column++)
                {
                    if ((column & 127) == 0) ct.ThrowIfCancellationRequested();
                    row[column] = values != null && column < values.Length ? values[column] ?? "" : "";
                }
                table.Rows.Add(row);
                indices.Add(row, (sourceIndex, values));
            }
            table.EndLoadData();
            ct.ThrowIfCancellationRequested();
            return new(source, table, indices, query);
        }
        catch { table.Dispose(); throw; }
    }

    public static PreviewQueryResult Query(ExtractionResult source, string? search, int displayLimit = 10000,
        IReadOnlyList<int>? sourceIndices = null, CancellationToken ct = default, PreviewTransform? transform = null, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(displayLimit);
        ct.ThrowIfCancellationRequested();

        transform ??= new(); transform.Validate(source.Headers.Count);
        culture ??= CultureInfo.CurrentCulture;
        var orderedMatches = transform.Sorts.Count > 0 ? new List<int>() : null;
        var term = (search ?? "").Trim();
        var sourceCount = source.Rows.Count;
        var displayed = new List<int>(Math.Min(displayLimit, sourceCount));
        var visited = sourceIndices == null ? null : new HashSet<int>();
        var candidates = sourceIndices?.Count ?? sourceCount;
        var scopeCount = 0;
        var matches = 0;
        for (var position = 0; position < candidates; position++)
        {
            if ((position & 255) == 0) ct.ThrowIfCancellationRequested();
            var index = sourceIndices == null ? position : sourceIndices[position];
            // A dashboard can contain an empty scope; invalid or repeated indices never inflate counts.
            if (index < 0 || index >= sourceCount || (visited != null && !visited.Add(index))) continue;
            scopeCount++;
            if (!Matches(source.Rows[index], term, ct) || !transform.Matches(source.Rows[index], culture)) continue;
            matches++;
            if (orderedMatches != null) orderedMatches.Add(index);
            else if (displayed.Count < displayLimit) displayed.Add(index);
        }
        ct.ThrowIfCancellationRequested();
        if (orderedMatches != null)
        {
            transform.Sort(orderedMatches, source, culture, ct);
            displayed.AddRange(orderedMatches.Take(displayLimit));
        }
        ct.ThrowIfCancellationRequested();
        return new(displayed.ToArray(), matches, scopeCount, sourceCount);
    }

    private static bool Matches(string[]? values, string term, CancellationToken ct)
    {
        if (term.Length == 0) return true;
        if (values == null) return false;
        for (var column = 0; column < values.Length; column++)
        {
            if ((column & 31) == 0) ct.ThrowIfCancellationRequested();
            // SQL/DataView operators, quotes, brackets and wildcard characters are ordinary text.
            if (values[column]?.Contains(term, StringComparison.OrdinalIgnoreCase) == true) return true;
        }
        return false;
    }
}


/// <summary>Literal, bounded transforms over source indices. No DataView expression or arbitrary code.</summary>
public sealed class PreviewTransform
{
    public List<PreviewColumnFilter> Filters { get; set; } = new();
    public List<PreviewSortRule> Sorts { get; set; } = new();
    public PreviewTransform Copy() => new() { Filters = Filters.Select(rule => rule.Copy()).ToList(), Sorts = Sorts.Select(rule => rule.Copy()).ToList() };
    public void Validate(int columns)
    {
        if (Filters is null || Sorts is null || Filters.Count > 12 || Sorts.Count > 4) throw new System.IO.InvalidDataException("La vue accepte 12 filtres et 4 niveaux de tri.");
        if (Filters.Any(rule => rule == null || rule.Column < 0 || rule.Column >= columns || rule.Value is null || rule.Value.Length > 2000
            || rule.Operator is not ("contains" or "not-contains" or "eq" or "ne" or "empty" or "not-empty" or "gt" or "lt" or "before" or "after")))
            throw new System.IO.InvalidDataException("Un filtre de colonne est invalide.");
        if (Sorts.Any(rule => rule == null || rule.Column < 0 || rule.Column >= columns || rule.Type is not ("text" or "number" or "date")) || Sorts.Select(rule => rule.Column).Distinct().Count() != Sorts.Count)
            throw new System.IO.InvalidDataException("Un niveau de tri est invalide ou répété.");
    }
    internal bool Matches(string[]? row, CultureInfo culture)
    {
        foreach (var filter in Filters)
        {
            var value = Cell(row, filter.Column); var expected = filter.Value;
            var match = filter.Operator switch
            {
                "contains" => culture.CompareInfo.IndexOf(value, expected, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0,
                "not-contains" => culture.CompareInfo.IndexOf(value, expected, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) < 0,
                "eq" => culture.CompareInfo.Compare(value.Trim(), expected.Trim(), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0,
                "ne" => culture.CompareInfo.Compare(value.Trim(), expected.Trim(), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) != 0,
                "empty" => string.IsNullOrWhiteSpace(value), "not-empty" => !string.IsNullOrWhiteSpace(value),
                "gt" => Numeric(value, culture, out var a) && Numeric(expected, culture, out var b) && a > b,
                "lt" => Numeric(value, culture, out var a) && Numeric(expected, culture, out var b) && a < b,
                "before" => DateTime.TryParse(value, culture, DateTimeStyles.AllowWhiteSpaces, out var a) && DateTime.TryParse(expected, culture, DateTimeStyles.AllowWhiteSpaces, out var b) && a < b,
                "after" => DateTime.TryParse(value, culture, DateTimeStyles.AllowWhiteSpaces, out var a) && DateTime.TryParse(expected, culture, DateTimeStyles.AllowWhiteSpaces, out var b) && a > b,
                _ => false
            };
            if (!match) return false;
        }
        return true;
    }
    internal void Sort(List<int> indices, ExtractionResult source, CultureInfo culture, CancellationToken ct)
    {
        int comparisons = 0;
        try
        {
            indices.Sort((left, right) =>
            {
                if ((comparisons++ & 1023) == 0) ct.ThrowIfCancellationRequested();
                foreach (var sort in Sorts)
                {
                    var a = Cell(source.Rows[left], sort.Column); var b = Cell(source.Rows[right], sort.Column);
                    bool aValid = !string.IsNullOrWhiteSpace(a), bValid = !string.IsNullOrWhiteSpace(b); int compared;
                    if (sort.Type == "number")
                    {
                        aValid = Numeric(a, culture, out var aNumber); bValid = Numeric(b, culture, out var bNumber);
                        compared = aNumber.CompareTo(bNumber);
                    }
                    else if (sort.Type == "date")
                    {
                        aValid = DateTime.TryParse(a, culture, DateTimeStyles.AllowWhiteSpaces, out var aDate); bValid = DateTime.TryParse(b, culture, DateTimeStyles.AllowWhiteSpaces, out var bDate);
                        compared = aDate.CompareTo(bDate);
                    }
                    else compared = culture.CompareInfo.Compare(a, b, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
                    if (aValid != bValid) return aValid ? -1 : 1; // Missing / invalid always last, in either direction.
                    if (aValid && compared != 0) return sort.Descending ? -compared : compared;
                }
                return left.CompareTo(right); // Stable ties retain original row identity.
            });
        }
        catch (InvalidOperationException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
    }
    private static string Cell(string[]? row, int column) => row != null && column < row.Length ? row[column] ?? "" : "";
    private static bool Numeric(string value, CultureInfo culture, out decimal number) => decimal.TryParse(value, NumberStyles.Number, culture, out number);
}
public sealed class PreviewColumnFilter
{
    public int Column { get; set; }
    public string Operator { get; set; } = "contains";
    public string Value { get; set; } = "";
    public PreviewColumnFilter Copy() => (PreviewColumnFilter)MemberwiseClone();
}
public sealed class PreviewSortRule
{
    public int Column { get; set; }
    public bool Descending { get; set; }
    public string Type { get; set; } = "text";
    public PreviewSortRule Copy() => (PreviewSortRule)MemberwiseClone();
}
