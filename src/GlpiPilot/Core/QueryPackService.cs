using System.IO;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed class QueryPack
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<SavedQuery> Queries { get; set; } = new();
}
public sealed record QueryPackMerge(QueryLibrarySettings Library, int Added, int Identical, int Conflicts, IReadOnlyList<string> Names);

public static class QueryPackService
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 24, WriteIndented = true };
    public static void Validate(QueryPack pack)
    {
        if (pack is null || pack.Version != 1 || !QueryText.Valid(pack.Name, 120, false) || !QueryText.Valid(pack.Description, 2000, true, true)
            || pack.Queries is null || pack.Queries.Count == 0) throw new InvalidDataException("Pack métier au format 1, nommé et contenant au moins une requête attendu.");
        new QueryLibrarySettings { Queries = pack.Queries }.Validate();
    }
    public static QueryPack Read(string path)
    {
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("Le pack de requêtes dépasse 2 Mio.");
        var pack = JsonSerializer.Deserialize<QueryPack>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Pack vide."); Validate(pack); return pack;
    }
    public static void Write(QueryPack pack, string path)
    {
        Validate(pack); var bytes = JsonSerializer.SerializeToUtf8Bytes(pack, Json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Le pack de requêtes dépasse 2 Mio.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static QueryPackMerge PreviewMerge(QueryLibrarySettings current, QueryPack pack)
    {
        current.Validate(); Validate(pack); var merged = current.Copy(); var identical = 0; var conflicts = 0; var names = new List<string>();
        foreach (var incoming in pack.Queries)
        {
            var old = merged.Queries.FirstOrDefault(q => q.Id.Equals(incoming.Id, StringComparison.OrdinalIgnoreCase));
            if (old != null && JsonSerializer.Serialize(old, Json) == JsonSerializer.Serialize(incoming, Json)) { identical++; continue; }
            var copy = incoming.Copy();
            if (old != null) { conflicts++; copy.Id = Guid.NewGuid().ToString("N"); copy.Name = QueryText.CleanLabel(copy.Name + " · import", SavedQuery.MaximumNameLength); }
            if (merged.Queries.Count >= QueryLibrarySettings.MaximumQueries) throw new InvalidDataException("L’import dépasserait 128 requêtes. Aucun ajout n’a été appliqué ; libérez de la place ou importez un pack plus petit.");
            merged.Queries.Add(copy); names.Add(copy.Name);
        }
        merged.Validate(); return new(merged, names.Count, identical, conflicts, names);
    }
    public static IReadOnlyList<QueryPack> BuiltInPacks() => QueryLibrarySettings.CreateDefault().Queries.GroupBy(q => q.Category)
        .Select(group => new QueryPack { Name = group.Key, Description = "Modèles de requêtes du métier « " + group.Key + " ». Champs et échantillons contrôlés à chaque prévisualisation.", Queries = group.Select(q => q.Copy()).ToList() }).ToArray();
}
