using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed class AlertRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Nouvelle alerte";
    public bool Enabled { get; set; } = true;
    public string MetricKey { get; set; } = "overdue";
    public string Operator { get; set; } = "gt";
    public string EvaluationMode { get; set; } = "value";
    public double Threshold { get; set; }
    public int MinimumSample { get; set; } = 1;
    public bool RequireComplete { get; set; } = true;
    public AlertRule Copy() => (AlertRule)MemberwiseClone();
    public void Validate()
    {
        if (!Guid.TryParseExact(Id, "N", out _) || string.IsNullOrWhiteSpace(Name) || Name.Length > 120 || string.IsNullOrWhiteSpace(MetricKey) || MetricKey.Length > 120
            || EvaluationMode is not ("value" or "delta" or "percent") || Operator is not ("gt" or "gte" or "lt" or "lte") || !double.IsFinite(Threshold) || Math.Abs(Threshold) > 1e12 || MinimumSample is < 0 or > 1000000)
            throw new InvalidDataException("Une règle d’alerte possède un nom, une mesure, un seuil ou un échantillon invalide.");
    }
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { MetricKey, Operator, Threshold, MinimumSample, RequireComplete, EvaluationMode }))));
}
public sealed class AlertIncident
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RuleId { get; set; } = "";
    public string RuleFingerprint { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public string RunId { get; set; } = "";
    public DateTimeOffset FirstObserved { get; set; }
    public DateTimeOffset LastObserved { get; set; }
    public bool Active { get; set; } = true;
    public bool Acknowledged { get; set; }
    public DateTimeOffset? SnoozedUntil { get; set; }
    public double? Value { get; set; }
    public string Unit { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Evaluation { get; set; } = "Déclenchée";
    public int Occurrences { get; set; } = 1;
    public List<string> ContributorIds { get; set; } = new();
    public List<string> NewContributorIds { get; set; } = new();
    public bool ContributorsTruncated { get; set; }
    public int? NewContributorCount { get; set; }
    public string ContributorsLabel => NewContributorCount is int count ? $"{count} nouveau(x)" : "Comparaison indisponible";
    public List<AlertLifecycleEvent> History { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public int[] Rows { get; set; } = Array.Empty<int>();
    public string State => !Active ? Evaluation : Evaluation == "Indéterminée" ? Evaluation : SnoozedUntil > DateTimeOffset.Now ? "Reportée" : Acknowledged ? "Prise en compte" : "À traiter";
    public string DisplayValue => Value.HasValue ? $"{Value:N2} {Unit}" : "Indisponible";
}
public sealed record AlertLifecycleEvent(DateTimeOffset At, string Action, string Reason);
public sealed class AlertObservation
{
    public string RuleId { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public string RunId { get; set; } = "";
    public string ComparisonKey { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public double Value { get; set; }
}
public sealed class AlertDocument
{
    public int Version { get; set; } = 1;
    public List<AlertRule> Rules { get; set; } = new();
    public List<AlertIncident> Incidents { get; set; } = new();
    public List<AlertObservation> Observations { get; set; } = new();
}
public sealed class AlertService
{
    private readonly string _path;
    public AlertDocument Document { get; private set; } = new();
    public AlertService(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.DataRoot, "alerts.json");
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length <= 32 * 1024 * 1024)
            {
                var doc = JsonSerializer.Deserialize<AlertDocument>(File.ReadAllText(_path));
                if (doc is { Version: 1 } && doc.Rules is { Count: <= 50 } && doc.Incidents is { Count: <= 1000 })
                { foreach (var rule in doc.Rules) { if (rule == null) throw new InvalidDataException("Règle absente."); rule.Validate(); } Document = doc; Document.Incidents.RemoveAll(incident => incident == null); Document.Observations ??= new(); Document.Observations = Document.Observations.Where(o => o != null).TakeLast(5000).ToList(); foreach (var incident in Document.Incidents) { incident.History ??= new(); incident.ContributorIds ??= new(); incident.NewContributorIds ??= new(); } }
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        { AppPaths.AppendLog("ALERTES · fichier local indisponible : " + error.GetType().Name); }
    }
    public void SaveRules(IEnumerable<AlertRule> rules)
    {
        var list = rules.Select(rule => rule.Copy()).ToList(); if (list.Count > 50) throw new InvalidDataException("Conservez au maximum 50 règles d’alerte.");
        foreach (var rule in list) rule.Validate();
        if (list.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count() != list.Count) throw new InvalidDataException("Les identifiants d’alertes doivent être uniques.");
        Document.Rules = list;
        foreach (var incident in Document.Incidents.Where(incident => incident.Active))
        {
            var rule = list.FirstOrDefault(rule => rule.Id == incident.RuleId);
            if (rule == null || !rule.Enabled || rule.Fingerprint() != incident.RuleFingerprint)
            { incident.Active = false; incident.Evaluation = "Règle retirée ou modifiée"; }
        }
        Save();
    }
    public int Evaluate(ExtractionResult source, DashboardSnapshot snapshot, CancellationToken ct = default, AppSettings? settings = null)
    {
        ct.ThrowIfCancellationRequested();
        if (snapshot.IsFiltered || source.Provenance.SourceKind == "demo" || Document.Rules.Count == 0) return 0;
        var document = JsonSerializer.Deserialize<AlertDocument>(JsonSerializer.Serialize(Document))!;
        for (int index = 0; index < document.Incidents.Count; index++) document.Incidents[index].Rows = Document.Incidents[index].Rows.ToArray();
        var sourceKey = SnapshotStore.SourceKey(source);
        if (sourceKey.Length == 0) sourceKey = "local:" + AppPaths.SafeUrl(source.SourceUrl) + "|" + source.Provenance.OriginalFileName;
        var interpretation = settings?.Dashboard ?? new DashboardSettings();
        var idColumn = DashboardService.ResolveColumns(source.Headers, interpretation).First(c => c.Role == "id").Index;
        var comparisonKey = source.Complete && source.Provenance.SearchVerified && source.Provenance.ScopeVerified
            ? source.Provenance.ScopeFingerprint + ":" + SnapshotStore.SchemaFingerprint(source.Headers) + ":" + (settings == null ? "" : SnapshotStore.RulesFingerprint(settings)) : "";
        int created = 0;
        foreach (var rule in document.Rules.Where(rule => rule.Enabled))
        {
            ct.ThrowIfCancellationRequested(); var fingerprint = rule.Fingerprint();
            var previous = document.Incidents.LastOrDefault(incident => incident.Active && incident.SourceKey == sourceKey && incident.RuleId == rule.Id && incident.RuleFingerprint == fingerprint);
            if (document.Incidents.Any(i => i.SourceKey == sourceKey && i.RuleId == rule.Id && i.RuleFingerprint == fingerprint && i.RunId == source.Provenance.RunId)) continue;
            var metric = snapshot.Metric(rule.MetricKey); var sample = metric?.Sample ?? snapshot.RowCount;
            var baseline = document.Observations.LastOrDefault(o => o.RuleId == rule.Id && o.SourceKey == sourceKey && o.ComparisonKey == comparisonKey + ":" + fingerprint && o.RunId != source.Provenance.RunId && o.At < source.ExtractedAt);
            var measured = metric?.Value;
            if (rule.EvaluationMode != "value")
            {
                measured = baseline == null || comparisonKey.Length == 0 ? null : rule.EvaluationMode == "delta" ? metric?.Value - baseline.Value : baseline.Value == 0 ? null : 100 * (metric?.Value - baseline.Value) / Math.Abs(baseline.Value);
            }
            string? unavailable = rule.RequireComplete && (!source.Complete || (source.ExpectedTotal.HasValue && source.ExpectedTotal != source.Rows.Count)
                    || !source.Provenance.SearchVerified || !source.Provenance.ScopeVerified)
                ? "Relevé incomplet ou périmètre non confirmé : état précédent conservé."
                : metric?.Value is not double value || !double.IsFinite(value) ? "Mesure indisponible : état précédent conservé."
                : sample < rule.MinimumSample ? "Échantillon inférieur au minimum : état précédent conservé." : null;
            if (unavailable == null && metric?.Value is double observed && comparisonKey.Length > 0 && !document.Observations.Any(o => o.RuleId == rule.Id && o.SourceKey == sourceKey && o.RunId == source.Provenance.RunId))
                document.Observations.Add(new() { RuleId = rule.Id, SourceKey = sourceKey, RunId = source.Provenance.RunId, ComparisonKey = comparisonKey + ":" + fingerprint, At = source.ExtractedAt, Value = observed });
            if (unavailable == null && (measured is not double evaluated || !double.IsFinite(evaluated))) unavailable = "Variation indisponible : aucun relevé antérieur comparable, ou base de pourcentage nulle.";
            if (unavailable != null)
            {
                if (previous != null) { previous.Evaluation = "Indéterminée"; previous.Detail = unavailable; previous.LastObserved = source.ExtractedAt; previous.RunId = source.Provenance.RunId; previous.Rows = Array.Empty<int>(); }
                continue;
            }
            var triggered = rule.Operator switch { "gt" => measured > rule.Threshold, "gte" => measured >= rule.Threshold, "lt" => measured < rule.Threshold, "lte" => measured <= rule.Threshold, _ => false };
            if (!triggered)
            {
                if (previous != null) { previous.Active = false; previous.Evaluation = "Retour sous le seuil"; previous.Value = measured; previous.History.Add(new(source.ExtractedAt, "Résolution automatique", "Le relevé exploitable ne satisfait plus la condition.")); previous.LastObserved = source.ExtractedAt; previous.RunId = source.Provenance.RunId; previous.Rows = metric!.Rows; previous.Detail = "Le dernier relevé exploitable ne satisfait plus la condition d’alerte."; }
                continue;
            }
            if (previous == null)
            {
                previous = new() { RuleId = rule.Id, RuleFingerprint = fingerprint, Name = rule.Name, SourceKey = sourceKey, SourceLabel = AppPaths.SafeUrl(source.SourceUrl), FirstObserved = source.ExtractedAt };
                previous.History.Add(new(source.ExtractedAt, "Ouverture", "Condition vérifiée sur le relevé.")); document.Incidents.Add(previous); created++;
            }
            else previous.Occurrences++;
            previous.LastObserved = source.ExtractedAt; previous.RunId = source.Provenance.RunId; previous.Evaluation = "Déclenchée";
            var contributors = idColumn is int idIndex ? metric!.Rows.Where(row => row >= 0 && row < source.Rows.Count && idIndex < source.Rows[row].Length).Select(row => source.Rows[row][idIndex].Trim()).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToArray() : Array.Empty<string>();
            var additions = contributors.Except(previous.ContributorIds, StringComparer.Ordinal).ToArray();
            previous.NewContributorCount = previous.ContributorsTruncated || idColumn == null ? null : additions.Length;
            previous.NewContributorIds = additions.Take(500).ToList(); previous.ContributorIds = contributors.Take(20000).ToList(); previous.ContributorsTruncated = contributors.Length > 20000;
            previous.Value = measured; previous.Unit = rule.EvaluationMode == "percent" ? "% de variation" : metric!.Unit; previous.Rows = metric!.Rows;
            previous.Detail = metric!.Label + " · " + rule.EvaluationMode + " · seuil " + rule.Operator + " " + rule.Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture) + " · échantillon " + sample;
        }
        document.Observations = document.Observations.GroupBy(o => (o.RuleId, o.SourceKey, o.ComparisonKey)).SelectMany(group => group.OrderByDescending(o => o.At).Take(2)).OrderBy(o => o.At).TakeLast(5000).ToList();
        foreach (var incident in document.Incidents) incident.History = incident.History.TakeLast(100).ToList();
        document.Incidents = document.Incidents.OrderByDescending(incident => incident.Active).ThenByDescending(incident => incident.LastObserved).Take(1000).OrderBy(incident => incident.FirstObserved).ToList();
        ct.ThrowIfCancellationRequested();
        var previousDocument = Document; Document = document;
        try { Save(); } catch { Document = previousDocument; throw; }
        return created;
    }
    public void Acknowledge(string id) => MutateIncident(id, incident => { incident.Acknowledged = true; incident.History.Add(new(DateTimeOffset.Now, "Prise en compte", "Action locale")); });
    public void Snooze(string id, TimeSpan duration) => MutateIncident(id, incident => { incident.SnoozedUntil = DateTimeOffset.Now + duration; incident.History.Add(new(DateTimeOffset.Now, "Report", incident.SnoozedUntil.Value.ToString("O"))); });
    public void Resolve(string id, string reason)
    {
        reason = (reason ?? "").Trim(); if (reason.Length is < 3 or > 1000) throw new InvalidDataException("Précisez un motif de résolution entre 3 et 1 000 caractères.");
        MutateIncident(id, incident => { incident.Active = false; incident.Evaluation = "Résolue localement"; incident.History.Add(new(DateTimeOffset.Now, "Résolution manuelle", reason)); });
    }
    private void MutateIncident(string id, Action<AlertIncident> mutate)
    {
        if (Document.Incidents.All(item => item.Id != id)) return;
        var previous = Document;
        var draft = JsonSerializer.Deserialize<AlertDocument>(JsonSerializer.SerializeToUtf8Bytes(Document))!;
        mutate(draft.Incidents.First(item => item.Id == id)); Document = draft;
        try { Save(); } catch { Document = previous; throw; }
    }
    private void Save()
    {
        foreach (var incident in Document.Incidents) if (incident.History.Count > 100) incident.History = incident.History.TakeLast(100).ToList();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Document); if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("Le journal d’alertes dépasse 32 Mio.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!); var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, _path, true); } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
