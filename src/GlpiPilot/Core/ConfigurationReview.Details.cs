using System.IO;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed record ConfigurationFieldDifference(string Path, string Change, string Current, string Incoming);
public sealed class ConfigurationConflict
{
    public string Key { get; set; } = "";
    public string Section { get; set; } = "";
    public string Name { get; set; } = "";
    public string Choice { get; set; } = "current";
}

public static partial class ConfigurationReview
{
    public static IReadOnlyList<ConfigurationFieldDifference> DescribeFields(AppSettings current, AppSettings incoming)
    {
        var output = new List<ConfigurationFieldDifference>();
        void Walk(JsonElement? a, JsonElement? b, string path)
        {
            if (output.Count > 100000) throw new InvalidDataException("Le comparatif dépasse 100 000 champs ; réduisez les sections sélectionnées.");
            if (a?.GetRawText() == b?.GetRawText()) return;
            if (a is { ValueKind: JsonValueKind.Object } left && b is { ValueKind: JsonValueKind.Object } right)
            {
                var aa = left.EnumerateObject().ToDictionary(p => p.Name, p => p.Value); var bb = right.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                foreach (var key in aa.Keys.Union(bb.Keys).Order(StringComparer.Ordinal)) Walk(aa.TryGetValue(key, out var av) ? av : null, bb.TryGetValue(key, out var bv) ? bv : null, path.Length > 0 ? path + "." + key : key); return;
            }
            if (a is { ValueKind: JsonValueKind.Array } al && b is { ValueKind: JsonValueKind.Array } bl)
            {
                var aa = al.EnumerateArray().ToArray(); var bb = bl.EnumerateArray().ToArray();
                string Key(JsonElement item, int index) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString()! : index.ToString();
                var orderA = aa.Select(Key).ToArray(); var orderB = bb.Select(Key).ToArray();
                if (orderA.Distinct(StringComparer.Ordinal).Count() != orderA.Length || orderB.Distinct(StringComparer.Ordinal).Count() != orderB.Length) throw new InvalidDataException("Des identifiants dupliqués empêchent le comparatif de " + path + ".");
                if (orderA.Length > 1 && orderA.Length == orderB.Length && orderA.ToHashSet(StringComparer.Ordinal).SetEquals(orderB) && !orderA.SequenceEqual(orderB))
                    output.Add(new(path + ".$ordre", "Réorganisation", string.Join(" → ", orderA), string.Join(" → ", orderB)));
                var da = aa.Select((item, index) => (Key: Key(item, index), Item: item)).ToDictionary(p => p.Key, p => p.Item); var db = bb.Select((item, index) => (Key: Key(item, index), Item: item)).ToDictionary(p => p.Key, p => p.Item);
                foreach (var key in da.Keys.Union(db.Keys).Order(StringComparer.Ordinal)) Walk(da.TryGetValue(key, out var av) ? av : null, db.TryGetValue(key, out var bv) ? bv : null, path + "[" + key + "]"); return;
            }
            string Label(JsonElement? value) => value == null ? "∅" : value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() ?? "" : value.Value.GetRawText();
            output.Add(new(path, a == null ? "Ajout" : b == null ? "Suppression" : "Modification", Label(a), Label(b)));
        }
        Walk(JsonSerializer.SerializeToElement(current), JsonSerializer.SerializeToElement(incoming), ""); return output;
    }
    public static IReadOnlyList<ConfigurationConflict> Conflicts(AppSettings current, AppSettings incoming)
    {
        var list = new List<ConfigurationConflict>();
        void Add<T>(string section, IEnumerable<T> before, IEnumerable<T> after, Func<T, string> id, Func<T, string> name)
        {
            var existing = before.ToDictionary(id, StringComparer.Ordinal);
            foreach (var item in after) if (existing.TryGetValue(id(item), out var old) && JsonSerializer.Serialize(old) != JsonSerializer.Serialize(item)) list.Add(new() { Key = section + "/" + id(item), Section = section, Name = name(item) });
        }
        Add("sources", current.Sources, incoming.Sources, x => x.Id, x => x.Name);
        Add("queries", current.Queries.Queries, incoming.Queries.Queries, x => x.Id, x => x.Name);
        Add("boards", current.Dashboard.Layout.Boards, incoming.Dashboard.Layout.Boards, x => x.Id, x => x.Name);
        Add("personal-boards", current.Dashboard.PersonalLayout.Boards, incoming.Dashboard.PersonalLayout.Boards, x => x.Id, x => x.Name);
        Add("presets", current.Dashboard.Layout.FilterPresets, incoming.Dashboard.Layout.FilterPresets, x => x.Name, x => x.Name);
        Add("personal-presets", current.Dashboard.PersonalLayout.FilterPresets, incoming.Dashboard.PersonalLayout.FilterPresets, x => x.Name, x => x.Name);
        Add("cities", current.Cities.Cities, incoming.Cities.Cities, x => x.Id, x => x.Name);
        Add("sites", current.Cities.Sites, incoming.Cities.Sites, x => x.Id, x => x.Name);
        Add("buildings", current.Cities.Buildings, incoming.Cities.Buildings, x => x.Id, x => x.Name); return list;
    }
    public static AppSettings Apply(AppSettings current, AppSettings incoming, ConfigurationParts parts, bool mergeCollections, IReadOnlyDictionary<string, string> choices)
    {
        if (!mergeCollections) return Apply(current, incoming, parts, false);
        if (choices.Values.Any(value => value is not ("current" or "incoming" or "both"))) throw new InvalidDataException("Arbitrage de conflit inconnu.");
        var source = incoming.Copy(); var result = Apply(current, source, parts & ~(ConfigurationParts.Source | ConfigurationParts.Queries | ConfigurationParts.Dashboards | ConfigurationParts.Cities), false);
        var maps = new Dictionary<string, string>(StringComparer.Ordinal);
        List<T> MergeChosen<T>(string section, IEnumerable<T> before, IEnumerable<T> after, Func<T, string> id, Func<T, T> copy, Action<T> rekey, int maximum)
        {
            var items = before.Select(copy).ToList();
            foreach (var item in after)
            {
                var index = items.FindIndex(old => id(old) == id(item));
                if (index < 0) { items.Add(copy(item)); continue; }
                if (JsonSerializer.Serialize(items[index]) == JsonSerializer.Serialize(item)) continue;
                var decision = choices.GetValueOrDefault(section + "/" + id(item), "current");
                if (decision == "incoming") items[index] = copy(item);
                else if (decision == "both") { var added = copy(item); rekey(added); if (typeof(T).GetProperty("Id") is { CanWrite: true } identity) identity.SetValue(added, new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(section + "/" + id(item) + "/" + JsonSerializer.Serialize(item))).AsSpan(0, 16)).ToString("N")); maps[section + "/" + id(item)] = id(added); if (items.Any(old => id(old) == id(added))) throw new InvalidDataException("Le nom de la variante existe déjà. Renommez le contexte avant la fusion."); items.Add(added); }
            }
            if (items.Count > maximum) throw new InvalidDataException($"La fusion dépasse la limite de {maximum} éléments de {section}."); return items;
        }
        static string Variant(string name) => (name.Length > 105 ? name[..105] : name) + " (importé)";
        if (parts.HasFlag(ConfigurationParts.Source))
        {
            result.PortalUrl = source.PortalUrl; result.BrowserChannel = source.BrowserChannel; result.ActiveSourceId = "";
            result.Sources = MergeChosen("sources", current.Sources, source.Sources, x => x.Id, x => x.Copy(), x => { x.Id = Guid.NewGuid().ToString("N"); x.Name = Variant(x.Name); }, 100);
            foreach (var profile in result.Sources) { profile.ExportFolder = current.Sources.FirstOrDefault(old => old.Id == profile.Id)?.ExportFolder ?? ""; if (profile.Interpretation != null) { profile.Interpretation.PersonalTechnician = current.Dashboard.PersonalTechnician; profile.Interpretation.OpenPersonalAfterExtraction = current.Dashboard.OpenPersonalAfterExtraction; } }
        }
        if (parts.HasFlag(ConfigurationParts.Queries)) result.Queries.Queries = MergeChosen("queries", current.Queries.Queries, source.Queries.Queries, x => x.Id, x => x.Copy(), x => { x.Id = Guid.NewGuid().ToString("N"); x.Name = Variant(x.Name); }, 128);
        string RemapQuery(string key)
        {
            foreach (var prefix in new[] { "query:", "query-group:" }) if (key.StartsWith(prefix, StringComparison.Ordinal) && maps.TryGetValue("queries/" + key[prefix.Length..], out var id)) return prefix + id;
            return key;
        }
        void MergeBoards(DashboardLayoutSettings destination, DashboardLayoutSettings before, DashboardLayoutSettings after, string category, string presets)
        {
            foreach (var board in after.Boards)
            {
                foreach (var widget in board.Widgets) { widget.SourceKey = RemapQuery(widget.SourceKey); foreach (var filter in widget.Filters) filter.SourceKey = RemapQuery(filter.SourceKey); }
                foreach (var filter in board.Filters.Concat(board.ComparisonFilters)) filter.SourceKey = RemapQuery(filter.SourceKey);
            }
            foreach (var preset in after.FilterPresets) foreach (var filter in preset.Filters) filter.SourceKey = RemapQuery(filter.SourceKey);
            destination.Boards = MergeChosen(category, before.Boards, after.Boards, x => x.Id, x => x.Copy(), x => { x.Id = Guid.NewGuid().ToString("N"); x.Name = Variant(x.Name); }, 12);
            destination.FilterPresets = MergeChosen(presets, before.FilterPresets, after.FilterPresets, x => x.Name, x => x.Copy(), x => x.Name = Variant(x.Name), 30);
            destination.CatalogFavorites = before.CatalogFavorites.Concat(after.CatalogFavorites.Select(RemapQuery)).Distinct().Take(512).ToList(); destination.CatalogRecent = before.CatalogRecent.Concat(after.CatalogRecent.Select(RemapQuery)).Distinct().Take(30).ToList();
        }
        if (parts.HasFlag(ConfigurationParts.Dashboards))
        { MergeBoards(result.Dashboard.Layout, current.Dashboard.Layout, source.Dashboard.Layout, "boards", "presets"); MergeBoards(result.Dashboard.PersonalLayout, current.Dashboard.PersonalLayout, source.Dashboard.PersonalLayout, "personal-boards", "personal-presets"); }
        if (parts.HasFlag(ConfigurationParts.Cities))
        {
            result.Cities.Enabled = source.Cities.Enabled; result.Cities.TitleColumn = source.Cities.TitleColumn; result.Cities.LocationColumn = source.Cities.LocationColumn; result.Cities.SourcePriority = source.Cities.SourcePriority.ToList();
            result.Cities.Cities = MergeChosen("cities", current.Cities.Cities, source.Cities.Cities, x => x.Id, x => x.Copy(), x => { x.Id = Guid.NewGuid().ToString("N"); x.Name = Variant(x.Name); }, 1000);
            foreach (var site in source.Cities.Sites) if (maps.TryGetValue("cities/" + site.CityId, out var city)) site.CityId = city;
            result.Cities.Sites = MergeChosen("sites", current.Cities.Sites, source.Cities.Sites, x => x.Id, x => x.Copy(), x => { x.Id = Guid.NewGuid().ToString("N"); x.Name = Variant(x.Name); }, 2000);
            foreach (var building in source.Cities.Buildings) if (maps.TryGetValue("sites/" + building.SiteId, out var site)) building.SiteId = site;
            result.Cities.Buildings = MergeChosen("buildings", current.Cities.Buildings, source.Cities.Buildings, x => x.Id, x => x.Copy(), x => { x.Id = Guid.NewGuid().ToString("N"); x.Name = Variant(x.Name); }, 5000);
        }
        result.Cities.Validate(); result.Queries.Validate(); result.Dashboard.Layout.Validate(); result.Dashboard.PersonalLayout.Validate(); result.Normalize(); return result;
    }
}
