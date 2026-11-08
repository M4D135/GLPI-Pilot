using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

/// <summary>Reuses ordered source-row indices while paging; source values are never copied into the cache.</summary>
public sealed class PreviewQueryCache
{
    private sealed record Entry(string Key, PreviewQueryResult Query);
    private readonly LinkedList<Entry> _entries = new();
    private ExtractionResult? _source;
    private long _revision = -1;
    private int _rowCount, _columnCount, _indexCount;
    private const int MaximumIndices = 2_000_000;
    public long Calculations { get; private set; }

    public PreviewQueryResult Query(ExtractionResult source, long revision, string search, int[]? indices,
        PreviewTransform transform, CultureInfo culture, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_source, source) || _revision != revision || _rowCount != source.Rows.Count || _columnCount != source.Headers.Count)
        {
            _entries.Clear(); _indexCount = 0; _source = source; _revision = revision;
            _rowCount = source.Rows.Count; _columnCount = source.Headers.Count;
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { search, transform, culture = culture.Name, scoped = indices is not null })));
        if (indices is not null)
        {
            Span<byte> buffer = stackalloc byte[4];
            for (var i = 0; i < indices.Length; i++)
            {
                if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer, indices[i]); hash.AppendData(buffer);
            }
        }
        var key = Convert.ToHexString(hash.GetHashAndReset());
        for (var node = _entries.First; node is not null; node = node.Next)
            if (node.Value.Key == key)
            {
                var found = node.Value; _entries.Remove(node); _entries.AddFirst(found); return found.Query;
            }
        var result = PreviewQueryService.Query(source, search, source.Rows.Count, indices, ct, transform, culture);
        ct.ThrowIfCancellationRequested(); Calculations++;
        if (result.SourceIndices.Length <= MaximumIndices)
        {
            while (_entries.Count >= 4 || _entries.Count > 0 && _indexCount + result.SourceIndices.Length > MaximumIndices)
            { _indexCount -= _entries.Last!.Value.Query.SourceIndices.Length; _entries.RemoveLast(); }
            _entries.AddFirst(new Entry(key, result)); _indexCount += result.SourceIndices.Length;
        }
        return result;
    }
}
