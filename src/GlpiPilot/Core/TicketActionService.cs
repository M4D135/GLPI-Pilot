using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

public sealed class TicketActionAdapter
{
    public string Key { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "followup";
    public string Installation { get; set; } = "";
    public string FormSelector { get; set; } = "";
    public string FieldName { get; set; } = "";
    public string SubmitName { get; set; } = "";
    public string SubmitValue { get; set; } = "";
    public string ActionPath { get; set; } = "";
    public string StructureHash { get; set; } = "";
    public TicketActionAdapter Copy() => (TicketActionAdapter)MemberwiseClone();
}

public sealed record TicketActionChoice(string Value, string Label);
public sealed record TicketActionCandidate(TicketActionAdapter Adapter, IReadOnlyList<TicketActionChoice> Choices, string CurrentValue, string OtherFields);
public sealed record TicketActionRequest(TicketActionAdapter Adapter, string Value, string DisplayValue);
public sealed record TicketActionPreview(string Token, TicketReference Ticket, string Operation, string Before, string After, string OtherFields, DateTimeOffset ExpiresAt)
{
    public string Text => $"Ticket {Ticket.Id}\n{Ticket.Url}\n\n{Operation}\nValeur actuelle : {Before}\n\nValeur à envoyer :\n{After}\n\nAutres champs du formulaire :\n{OtherFields}\n\nCette confirmation autorise une seule soumission du formulaire GLPI. Validité : {ExpiresAt.LocalDateTime:HH:mm:ss}.";
}
public sealed record TicketActionOutcome(bool Verified, string Message);

/// <summary>Adapters contain native form identifiers only. They never contain executable source or authentication values.</summary>
public static class TicketActionService
{
    public static string Installation(TicketReference ticket)
    {
        var uri = new Uri(ticket.Url);
        var marker = uri.AbsolutePath.LastIndexOf("/front/ticket.form.php", StringComparison.Ordinal);
        if (marker < 0 || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Scheme is not ("http" or "https")) throw new InvalidDataException("La fiche ne désigne pas une installation GLPI reconnue.");
        return uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath[..marker] + "/";
    }
    public static TicketActionAdapter Validate(TicketActionAdapter adapter, TicketReference? ticket = null)
    {
        if (adapter == null || adapter.Kind is not ("assignment" or "followup" or "task")
            || new[] { adapter.Key, adapter.Name, adapter.Installation, adapter.FormSelector, adapter.FieldName, adapter.SubmitName, adapter.SubmitValue, adapter.ActionPath, adapter.StructureHash }.Any(value => value == null || value.Length > 2000 || value.Any(char.IsControl))
            || adapter.FormSelector.Length == 0 || adapter.FieldName.Length == 0 || adapter.SubmitName.Length == 0
            || adapter.StructureHash.Length != 64 || !adapter.StructureHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("L’adaptateur de formulaire est incomplet ou invalide. Inspectez à nouveau le formulaire GLPI ouvert.");
        if (!Uri.TryCreate(adapter.Installation, UriKind.Absolute, out var installation) || installation.Scheme is not ("http" or "https") || installation.UserInfo.Length > 0 || installation.Query.Length > 0 || installation.Fragment.Length > 0 || !installation.AbsolutePath.EndsWith('/'))
            throw new InvalidDataException("L’installation de l’adaptateur est invalide.");
        var allowed = adapter.Kind switch { "assignment" => new[] { "ticket_user.form.php", "ticket_group.form.php", "ticket.form.php" }, "followup" => new[] { "itilfollowup.form.php" }, _ => new[] { "tickettask.form.php" } };
        if (!allowed.Any(file => adapter.ActionPath == installation.AbsolutePath + "front/" + file)) throw new InvalidDataException("La destination de ce formulaire n’est pas autorisée pour cette opération.");
        if (ticket != null && Installation(ticket) != adapter.Installation) throw new InvalidDataException("Cet adaptateur appartient à une autre installation GLPI.");
        return adapter.Copy();
    }
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string Operation(string kind) => kind switch { "assignment" => "Affectation GLPI", "task" => "Ajouter une tâche GLPI", _ => "Ajouter un suivi GLPI" };
    public static string ValidateValue(string kind, string value)
    {
        value = (value ?? "").Replace("\r\n", "\n").Trim();
        if (value.Length == 0 || value.Length > 20000 || value.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new InvalidDataException("Saisissez une valeur de 1 à 20 000 caractères sans caractère de contrôle.");
        if (kind == "assignment" && (!value.All(char.IsAsciiDigit) || value == "0")) throw new InvalidDataException("Choisissez une affectation proposée par le formulaire GLPI.");
        return value;
    }
}

public static class TicketActionProfileStore
{
    public static IReadOnlyList<TicketActionAdapter> Load(string path)
    {
        if (!File.Exists(path)) return Array.Empty<TicketActionAdapter>();
        if (new FileInfo(path).Length > 512 * 1024) throw new InvalidDataException("Le fichier des adaptateurs dépasse 512 Ko.");
        var list = JsonSerializer.Deserialize<List<TicketActionAdapter>>(File.ReadAllText(path)) ?? throw new InvalidDataException("Le fichier des adaptateurs est vide.");
        if (list.Count > 100) throw new InvalidDataException("Au maximum 100 adaptateurs peuvent être conservés.");
        return list.Select(adapter => TicketActionService.Validate(adapter)).ToArray();
    }
    public static void Save(string path, IEnumerable<TicketActionAdapter> adapters)
    {
        var list = adapters.Take(101).Select(adapter => TicketActionService.Validate(adapter)).ToArray();
        if (list.Length > 100) throw new InvalidDataException("Au maximum 100 adaptateurs peuvent être conservés.");
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, full, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
