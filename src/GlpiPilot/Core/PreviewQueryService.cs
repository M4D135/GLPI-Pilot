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
        IReadOnlyList<int>? sourceIndices = null, CultureInfo? culture = null, CancellationToken ct = default)
    {
        var query = Query(source, search, displayLimit, sourceIndices, ct);
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
        IReadOnlyList<int>? sourceIndices = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(displayLimit);
        ct.ThrowIfCancellationRequested();

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
            if (!Matches(source.Rows[index], term, ct)) continue;
            matches++;
            if (displayed.Count < displayLimit) displayed.Add(index);
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
