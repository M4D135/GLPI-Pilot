using System.IO;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed class NamedPreview
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Ma vue";
    public string HeaderFingerprint { get; set; } = "";
    public string Search { get; set; } = "";
    public int ColumnsCount { get; set; }
    public PreviewTransform Transform { get; set; } = new();
    public PreviewColumnPreference[] Columns { get; set; } = Array.Empty<PreviewColumnPreference>();
    public NamedPreview Copy() => new() { Id = Id, Name = Name, HeaderFingerprint = HeaderFingerprint, Search = Search, ColumnsCount = ColumnsCount, Transform = Transform.Copy(), Columns = Columns.Select(column => column.Copy()).ToArray() };
}
public sealed class NamedPreviewStore
{
    private readonly string _path;
    public NamedPreviewStore(string? path = null) => _path = path ?? Path.Combine(AppPaths.DataRoot, "table-views.json");
    public IReadOnlyList<NamedPreview> List(string? fingerprint = null)
    {
        if (!File.Exists(_path)) return Array.Empty<NamedPreview>();
        if (new FileInfo(_path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Le fichier des vues dépasse 2 Mio.");
        var all = JsonSerializer.Deserialize<List<NamedPreview>>(File.ReadAllText(_path)) ?? new();
        if (all.Count > 64) throw new InvalidDataException("Le fichier des vues contient trop d’éléments.");
        foreach (var item in all) Validate(item);
        return all.Where(item => fingerprint == null || item.HeaderFingerprint == fingerprint).OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    public void Save(NamedPreview item)
    {
        Validate(item); var all = List().Select(value => value.Copy()).ToList(); var existing = all.FindIndex(value => value.Id == item.Id);
        if (existing >= 0) all[existing] = item.Copy(); else all.Add(item.Copy());
        if (all.Count > 64) throw new InvalidDataException("Conservez au maximum 64 vues nommées sur ce poste.");
        Write(all);
    }
    public void Remove(string id) => Write(List().Where(item => item.Id != id).ToArray());
    private static void Validate(NamedPreview item)
    {
        if (item == null || !Guid.TryParseExact(item.Id, "N", out _) || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 120
            || item.Search == null || item.Search.Length > 2000 || item.HeaderFingerprint == null || item.HeaderFingerprint.Length != 64
            || item.ColumnsCount is < 1 or > 16384 || item.Columns == null || item.Columns.Length > item.ColumnsCount || item.Transform == null)
            throw new InvalidDataException("Une vue nommée est invalide.");
        item.Transform.Validate(item.ColumnsCount);
    }
    private void Write(IEnumerable<NamedPreview> values)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(values); if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("Les vues dépassent 2 Mio.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!); var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, _path, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
