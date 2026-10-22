using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed record HeaderRename(string Previous, string Current, double Similarity);
public sealed record SchemaChange(string[] Added, string[] Removed, IReadOnlyList<HeaderRename> ProposedRenames, bool Reordered = false)
{ public bool Changed => Added.Length > 0 || Removed.Length > 0 || Reordered; }
public sealed class SourceSchemaStore
{
    private readonly string _folder;
    public SourceSchemaStore(string? folder = null) => _folder = folder ?? Path.Combine(AppPaths.DataRoot, "Schemas");
    private string FileFor(string source) => Path.Combine(_folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))) + ".json");
    public SchemaChange Inspect(string source, IReadOnlyList<string> headers)
    {
        var path = FileFor(source); if (!File.Exists(path)) { Accept(source, headers); return new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<HeaderRename>()); }
        string[] previous;
        try { if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Référence de colonnes trop volumineuse."); previous = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? Array.Empty<string>(); }
        catch (JsonException) { return new(headers.ToArray(), Array.Empty<string>(), Array.Empty<HeaderRename>()); }
        if (previous.Length > 2048 || previous.Any(header => header == null || header.Length > 8192)) return new(headers.ToArray(), Array.Empty<string>(), Array.Empty<HeaderRename>());
        return Compare(previous, headers);
    }
    public void Accept(string source, IReadOnlyList<string> headers)
    {
        if (headers.Count > 2048 || headers.Any(h => h == null || h.Length > 8192)) throw new InvalidDataException("La structure dépasse les limites de colonnes.");
        Directory.CreateDirectory(_folder); var path = FileFor(source); var temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(headers)); File.Move(temp, path, true);
    }
    public static SchemaChange Compare(IReadOnlyList<string> previous, IReadOnlyList<string> current)
    {
        static string[] Difference(IReadOnlyList<string> values, IReadOnlyList<string> baseline)
        {
            var remaining = baseline.GroupBy(value => value, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var result = new List<string>();
            foreach (var value in values) { if (remaining.GetValueOrDefault(value) > 0) remaining[value]--; else result.Add(value); }
            return result.ToArray();
        }
        var added = Difference(current, previous); var removed = Difference(previous, current);
        var matches = (from old in removed from next in added let score = Similarity(old, next) where score >= .55 select new HeaderRename(old, next, score)).OrderByDescending(m => m.Similarity).ToArray();
        return new(added, removed, matches, added.Length == 0 && removed.Length == 0 && !previous.SequenceEqual(current));
    }
    private static double Similarity(string a, string b)
    {
        a = DashboardService.Normalize(a); b = DashboardService.Normalize(b); if (a == b) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        var tokensA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(); var tokensB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return Math.Max((double)tokensA.Intersect(tokensB).Count() / Math.Max(tokensA.Count, tokensB.Count), a.Contains(b) || b.Contains(a) ? (double)Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length) : 0);
    }
}

public static class DataObjectIdentity
{
    public static string TypeFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "local";
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        { var parts = pair.Split('=', 2); if (parts.Length == 2 && (parts[0].Equals("itemtype", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("item_type", StringComparison.OrdinalIgnoreCase))) { var type = Uri.UnescapeDataString(parts[1]); if (System.Text.RegularExpressions.Regex.IsMatch(type, @"^[A-Za-z][A-Za-z0-9_\\]{0,119}$")) return type.ToLowerInvariant(); } }
        var name = Path.GetFileName(uri.AbsolutePath).Split('.')[0].ToLowerInvariant();
        return name.Length > 0 && name is not ("search" or "allassets" or "reservationitem" or "central" or "report" or "report.dynamic") && System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-z][a-z0-9_]{0,119}$") ? name : "item";
    }
    public static string RowKey(ExtractionResult data, int rowIndex, DashboardSettings settings) => KeyResolver(data, settings)(rowIndex);
    public static Func<int, string> KeyResolver(ExtractionResult data, DashboardSettings settings)
    {
        var idColumn = DashboardService.ResolveColumns(data.Headers, settings).First(c => c.Role == "id").Index;
        var url = data.Provenance.RequestedUrl.Length > 0 ? data.Provenance.RequestedUrl : data.SourceUrl; var type = TypeFromUrl(url);
        if (type is "local" or "item" || data.Provenance.SourceKind == "demo") return _ => "";
        return rowIndex =>
        {
            if (rowIndex < 0 || rowIndex >= data.Rows.Count || idColumn is not int column || column >= data.Rows[rowIndex].Length) return "";
            var id = data.Rows[rowIndex][column].Replace(" ", "").Replace("\u00A0", "").Replace("\u202F", "");
            try { return ObjectIdentity.Key(url, type, id); } catch (InvalidDataException) { return ""; }
        };
    }
    public static string Family(string header)
    {
        var value = DashboardService.Normalize(header);
        if (new[] { "date", "echeance", "delai", "time", "created", "updated", "closed", "resolved" }.Any(value.Contains)) return "Dates et délais";
        if (new[] { "technicien", "technician", "attribu", "assign", "groupe", "demandeur", "requester" }.Any(value.Contains)) return "Affectation et acteurs";
        if (new[] { "ville", "city", "lieu", "location", "site", "batiment", "postal", "territoire" }.Any(value.Contains)) return "Localisation";
        if (value is "id" or "titre" or "title" or "statut" or "status" or "type" or "priorite" or "priority" or "categorie") return "Identité et classement";
        return "Autres champs";
    }
}

public sealed class SelectionBasket
{
    private readonly string _path;
    public HashSet<string> Keys { get; private set; } = new(StringComparer.Ordinal);
    public SelectionBasket(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.DataRoot, "selection-basket.json");
        try { if (File.Exists(_path) && new FileInfo(_path).Length < 4 * 1024 * 1024) Keys = (JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path)) ?? Array.Empty<string>()).Where(k => k != null && k.Length <= 1000).Take(10000).ToHashSet(StringComparer.Ordinal); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }
    public int Add(ExtractionResult source, IEnumerable<int> rows, DashboardSettings settings)
    {
        var resolve = DataObjectIdentity.KeyResolver(source, settings);
        var keys = rows.Select(resolve).Where(key => key.Length > 0).Distinct().ToArray();
        if (Keys.Union(keys).Count() > 10000) throw new InvalidDataException("Le panier accepte 10 000 objets. Retirez des éléments avant d’en ajouter.");
        var count = keys.Count(Keys.Add); Save(); return count;
    }
    public int[] Find(ExtractionResult source, DashboardSettings settings)
    {
        var resolve = DataObjectIdentity.KeyResolver(source, settings);
        var matches = Enumerable.Range(0, source.Rows.Count).Select(index => (Index: index, Key: resolve(index))).Where(item => item.Key.Length > 0 && Keys.Contains(item.Key)).ToArray();
        // Ambiguous duplicate identifiers must never silently select one object.
        return matches.GroupBy(item => item.Key).Where(group => group.Count() == 1).Select(group => group.Single().Index).ToArray();
    }
    public void Remove(IEnumerable<string> keys) { Keys.ExceptWith(keys); Save(); }
    public void Clear() { Keys.Clear(); Save(); }
    private void Save() { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!); var temp = _path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(Keys.Order())); File.Move(temp, _path, true); }
}
