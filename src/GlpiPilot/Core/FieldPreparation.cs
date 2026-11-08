using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

// Preparation quantities describe an intention, never an inventory balance.
public sealed record FieldMaterialCategory(string Id, string Name, bool Archived = false);
public sealed record FieldMaterialReference(string Id, string CategoryId, string Code, string Label,
    string Unit = "piece", string Note = "", bool Archived = false);
public sealed class FieldMaterialCatalogue
{
    public int SchemaVersion { get; set; } = 1;
    public List<FieldMaterialCategory> Categories { get; set; } = new();
    public List<FieldMaterialReference> References { get; set; } = new();
}

// Key is an existing stable ticket key or calendar intervention ID, never a row number.
// OccurrenceKey is empty for a ticket not yet scheduled; recurring appointments use their own occurrence.
public sealed record FieldPreparationOwner(string Kind, string Key, string OccurrenceKey = "");
public sealed class FieldMaterialLine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ReferenceId { get; set; } = "";
    public string Code { get; set; } = "";
    public string Label { get; set; } = "";
    public string Category { get; set; } = "";
    public string Unit { get; set; } = "piece";
    public decimal Quantity { get; set; } = 1;
    public decimal PreparedQuantity { get; set; }
    public string PreparedBy { get; set; } = "";
    public string Note { get; set; } = "";
}
public sealed class FieldPreparationPlan
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public FieldPreparationOwner Owner { get; set; } = new("ticket", "");
    public string Title { get; set; } = "";
    public string SiteId { get; set; } = "";
    public string SiteName { get; set; } = "";
    public string Address { get; set; } = "";
    public string Contact { get; set; } = "";
    public DateTimeOffset? PlannedStart { get; set; }
    public DateTimeOffset? PlannedEnd { get; set; }
    public string State { get; set; } = "planned";
    public List<string> ParticipantKeys { get; set; } = new();
    public List<string> TourIds { get; set; } = new();
    public bool NoMaterialsRequired { get; set; }
    public List<FieldMaterialLine> Lines { get; set; } = new();
    public string PreparationNote { get; set; } = "";
}
public sealed record FieldPreparationTotal(string ReferenceId, string Label, string Code, string Category,
    string Unit, decimal Quantity, decimal PreparedQuantity, decimal RemainingQuantity, IReadOnlyList<string> PlanIds);
public sealed record FieldPreparationSummary(int PlanCount, int UnspecifiedCount, int NoMaterialsCount,
    int ToPrepareCount, int PartlyPreparedCount, int ReadyCount, IReadOnlyList<FieldPreparationTotal> Materials,
    IReadOnlyList<string> Warnings);

/// <summary>Pure, source-independent preparation rules. Persistence and calendar bindings remain separate.</summary>
public static class FieldPreparationService
{
    public const int MaximumCategories = 100, MaximumReferences = 2000, MaximumLines = 100, MaximumPlans = 5000;
    public static readonly string[] Units = { "piece", "metre", "lot" };
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, MaxDepth = 24 };

    public static string IdFor(FieldPreparationOwner owner)
    {
        ValidateOwner(owner);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { "glpi-pilot.preparation.v1", owner.Kind, owner.Key, owner.OccurrenceKey });
        return Convert.ToHexString(SHA256.HashData(bytes))[..32].ToLowerInvariant();
    }
    public static FieldPreparationPlan NewPlan(FieldPreparationOwner owner, string title)
    {
        var plan = new FieldPreparationPlan { Id = IdFor(owner), Owner = owner, Title = title };
        Validate(plan); return plan;
    }
    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)
        ?? throw new InvalidDataException("Document de préparation vide.");
    public static void ValidateOwner(FieldPreparationOwner? owner)
    {
        if (owner is null || owner.Kind is not ("ticket" or "intervention")) throw new InvalidDataException("Origine de préparation inconnue.");
        Text(owner.Key, 300, true); Text(owner.OccurrenceKey, 300);
    }
    public static void Validate(FieldMaterialCatalogue catalogue)
    {
        if (catalogue is null || catalogue.SchemaVersion != 1 || catalogue.Categories is null || catalogue.References is null
            || catalogue.Categories.Count > MaximumCategories || catalogue.References.Count > MaximumReferences)
            throw new InvalidDataException("Catalogue matériel invalide ou trop volumineux.");
        var categories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var category in catalogue.Categories)
        {
            if (category is null) throw new InvalidDataException("Catégorie vide.");
            Identifier(category.Id); Text(category.Name, 120, true);
            if (!categories.Add(category.Id)) throw new InvalidDataException("Identifiant de catégorie répété.");
        }
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in catalogue.References)
        {
            if (reference is null) throw new InvalidDataException("Référence vide.");
            Identifier(reference.Id); Text(reference.Code, 100); Text(reference.Label, 180, true); Text(reference.Note, 2000);
            if (!categories.Contains(reference.CategoryId) || !references.Add(reference.Id) || !Units.Contains(reference.Unit, StringComparer.Ordinal))
                throw new InvalidDataException("Référence matériel : catégorie, identifiant ou unité incorrects.");
        }
    }
    public static void Validate(FieldPreparationPlan plan)
    {
        if (plan is null || plan.SchemaVersion != 1) throw new InvalidDataException("Version de préparation inconnue.");
        ValidateOwner(plan.Owner);
        if (plan.Id != IdFor(plan.Owner)) throw new InvalidDataException("La préparation n’est pas liée à la bonne intervention.");
        Text(plan.Title, 300, true); Text(plan.SiteId, 80); Text(plan.SiteName, 200); Text(plan.Address, 1000);
        Text(plan.Contact, 1000); Text(plan.PreparationNote, 4000);
        if (plan.State is not ("planned" or "done" or "cancelled")) throw new InvalidDataException("État de préparation inconnu.");
        if (plan.PlannedEnd.HasValue && (!plan.PlannedStart.HasValue || plan.PlannedEnd <= plan.PlannedStart))
            throw new InvalidDataException("La fin de l’intervention doit suivre son début.");
        Strings(plan.ParticipantKeys, 200, 400); Strings(plan.TourIds, 20, 100);
        if (plan.Lines is null || plan.Lines.Count > MaximumLines || plan.NoMaterialsRequired && plan.Lines.Count > 0)
            throw new InvalidDataException("Liste matériel incohérente : conservez au maximum 100 lignes, ou choisissez aucun matériel.");
        var lines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in plan.Lines)
        {
            if (line is null) throw new InvalidDataException("Ligne matériel vide.");
            Identifier(line.Id); Identifier(line.ReferenceId); Text(line.Code, 100); Text(line.Label, 180, true);
            Text(line.Category, 120); Text(line.Note, 2000); Text(line.PreparedBy, 200);
            if (!lines.Add(line.Id) || !Units.Contains(line.Unit, StringComparer.Ordinal)
                || line.Quantity is <= 0 or > 10000 || line.PreparedQuantity < 0 || line.PreparedQuantity > line.Quantity
                || decimal.Round(line.Quantity, 3) != line.Quantity || decimal.Round(line.PreparedQuantity, 3) != line.PreparedQuantity
                || line.Unit != "metre" && (decimal.Truncate(line.Quantity) != line.Quantity || decimal.Truncate(line.PreparedQuantity) != line.PreparedQuantity))
                throw new InvalidDataException("Quantité ou identifiant matériel invalide ; une quantité préparée ne dépasse pas la quantité prévue.");
        }
    }
    public static string Readiness(FieldPreparationPlan plan)
    {
        Validate(plan);
        if (plan.State != "planned") return plan.State;
        if (plan.NoMaterialsRequired) return "none";
        if (plan.Lines.Count == 0) return "unspecified";
        if (plan.Lines.All(line => line.PreparedQuantity == line.Quantity)) return "ready";
        return plan.Lines.Any(line => line.PreparedQuantity > 0) ? "partial" : "to-prepare";
    }
    public static string ReadinessLabel(string state) => state switch
    {
        "none" => "Aucun matériel nécessaire", "unspecified" => "Matériel à préciser", "ready" => "Prêt à emporter",
        "partial" => "Préparation en cours", "done" => "Intervention terminée", "cancelled" => "Intervention annulée", _ => "À préparer"
    };
    public static string UnitLabel(string unit) => unit switch { "metre" => "m", "lot" => "lot(s)", _ => "pièce(s)" };
    public static FieldPreparationPlan AddReference(FieldPreparationPlan original, FieldMaterialCatalogue catalogue,
        string referenceId, decimal quantity, string note = "")
    {
        Validate(original); Validate(catalogue);
        var reference = catalogue.References.SingleOrDefault(value => value.Id == referenceId)
            ?? throw new InvalidDataException("Cette référence matériel n’existe plus.");
        if (reference.Archived || catalogue.Categories.Single(value => value.Id == reference.CategoryId).Archived)
            throw new InvalidDataException("Cette référence ou sa catégorie est archivée.");
        if (original.State != "planned") throw new InvalidOperationException("Rouvrez l’intervention avant de modifier sa préparation.");
        var plan = Copy(original);
        // No change to any shared input on a validation failure; old snapshots stay intact.
        plan.NoMaterialsRequired = false;
        plan.Lines.Add(new FieldMaterialLine { ReferenceId = reference.Id, Label = reference.Label, Code = reference.Code,
            Category = catalogue.Categories.Single(value => value.Id == reference.CategoryId).Name,
            Unit = reference.Unit, Quantity = quantity, Note = note });
        Validate(plan); return plan;
    }
    public static IReadOnlyList<FieldPreparationPlan> DistinctPlans(IEnumerable<FieldPreparationPlan> source, bool includeClosed = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        var plans = new Dictionary<string, (FieldPreparationPlan Plan, string Json)>(StringComparer.Ordinal);
        int count = 0;
        foreach (var plan in source)
        {
            if (++count > MaximumPlans * 4) throw new InvalidDataException("La sélection de préparations est trop volumineuse.");
            Validate(plan); var json = JsonSerializer.Serialize(plan, Json);
            if (plans.TryGetValue(plan.Id, out var old) && old.Json != json)
                throw new InvalidDataException("Deux versions différentes d’une préparation sont présentes. Actualisez avant de totaliser.");
            plans[plan.Id] = (Copy(plan), json);
            if (plans.Count > MaximumPlans) throw new InvalidDataException("Trop de préparations ; réduisez la période ou le périmètre.");
        }
        return plans.Values.Select(value => value.Plan).Where(plan => includeClosed || plan.State == "planned")
            .OrderBy(plan => plan.PlannedStart).ThenBy(plan => plan.Id, StringComparer.Ordinal).ToArray();
    }
    public static FieldPreparationSummary Summarize(IEnumerable<FieldPreparationPlan> source, bool includeClosed = false)
    {
        var plans = DistinctPlans(source, includeClosed); var states = plans.Select(Readiness).ToArray();
        var warnings = new List<string>();
        var entries = plans.SelectMany(plan => plan.Lines.Select(line => (PlanId: plan.Id, Line: line))).ToArray();
        foreach (var reference in entries.GroupBy(value => value.Line.ReferenceId, StringComparer.Ordinal))
            if (reference.Select(value => value.Line.Unit).Distinct(StringComparer.Ordinal).Count() > 1)
                warnings.Add("Des unités différentes sont conservées pour une même référence ; les quantités sont présentées séparément.");
        var materials = entries.GroupBy(value => (value.Line.ReferenceId, value.Line.Unit)).Select(group =>
        {
            var first = group.OrderBy(value => value.PlanId, StringComparer.Ordinal).ThenBy(value => value.Line.Id, StringComparer.Ordinal).First().Line;
            var total = group.Sum(value => value.Line.Quantity); var prepared = group.Sum(value => value.Line.PreparedQuantity);
            return new FieldPreparationTotal(first.ReferenceId, first.Label, first.Code, first.Category, first.Unit, total, prepared, total - prepared,
                group.Select(value => value.PlanId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        }).OrderBy(value => value.Category, StringComparer.CurrentCultureIgnoreCase).ThenBy(value => value.Label, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(value => value.ReferenceId, StringComparer.Ordinal).ThenBy(value => value.Unit, StringComparer.Ordinal).ToArray();
        return new(plans.Count, states.Count(value => value == "unspecified"), states.Count(value => value == "none"),
            states.Count(value => value == "to-prepare"), states.Count(value => value == "partial"), states.Count(value => value == "ready"),
            materials, warnings.Distinct(StringComparer.Ordinal).ToArray());
    }
    private static void Identifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100 || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            throw new InvalidDataException("Identifiant matériel invalide.");
    }
    private static void Text(string? value, int maximum, bool required = false)
    {
        if (value is null || value.Length > maximum || value.Contains('\0') || required && string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("Texte de préparation absent ou trop long.");
    }
    private static void Strings(List<string>? values, int count, int width)
    {
        if (values is null || values.Count > count || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new InvalidDataException("Liste de préparation invalide.");
        foreach (var value in values) Text(value, width, true);
    }
}
