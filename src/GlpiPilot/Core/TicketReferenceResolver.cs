using System.Globalization;
using System.Text.RegularExpressions;

namespace GlpiPilot.Core;

/// <summary>Resolves an explicit numeric ticket identity to a proven GLPI read URL.</summary>
public static class TicketReferenceResolver
{
    private const string TicketListSuffix = "/front/ticket.php";
    private const string TicketFormSuffix = "/front/ticket.form.php";
    private static readonly HashSet<string> IdHeaders = new(StringComparer.OrdinalIgnoreCase) { "ID", "Identifiant", "Ticket ID" };
    private static readonly char[] GroupSpaces = { ' ', '\u00A0', '\u202F', '\u2007' };

    public static TicketReference Resolve(ExtractionResult data, IReadOnlyList<string> row, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(data.SourceUrl))
            throw Error("La source de ce tableau est inconnue. Recharge la recherche GLPI avant d’ouvrir une fiche ticket.");
        var source = ReadHttpUrl(data.SourceUrl, "La source du tableau");
        var portal = ReadHttpUrl(settings.PortalUrl, "L’adresse GLPI configurée");
        if (!SameOrigin(source, portal) || !source.AbsolutePath.Equals(portal.AbsolutePath, StringComparison.Ordinal))
            throw Error("Ce tableau ne provient pas de la recherche GLPI configurée. Recharge la source avant d’ouvrir une fiche ticket.");
        if (data.Headers is null || data.Headers.Count == 0 || row.Count != data.Headers.Count)
            throw Error("La ligne sélectionnée ne correspond pas aux colonnes du tableau. Relance l’extraction.");

        string configured = settings.TicketDetails?.IdColumn ?? "";
        var columns = Enumerable.Range(0, data.Headers.Count).Where(index => configured.Length > 0
            ? string.Equals(data.Headers[index], configured, StringComparison.Ordinal)
            : IdHeaders.Contains(NormalizedHeader(data.Headers[index]))).ToList();
        if (columns.Count == 0)
            throw Error(configured.Length > 0
                ? "La colonne d’identifiant configurée est absente du tableau. Choisis son nom exact dans les réglages des tickets."
                : "Aucune colonne ID, Identifiant ou Ticket ID n’a été trouvée. Configure explicitement la colonne d’identifiant des tickets.");
        if (columns.Count > 1)
            throw Error("Plusieurs colonnes peuvent contenir l’identifiant du ticket. Configure une colonne au nom exact et unique.");
        string id = NormalizeId(row[columns[0]]);
        string template = settings.TicketDetails?.UrlTemplate ?? "";
        if (template.Length == 0)
        {
            if (!source.AbsolutePath.EndsWith(TicketListSuffix, StringComparison.Ordinal))
                throw Error("Cette source n’est pas la liste standard des tickets GLPI. Configure un modèle explicite de fiche ticket contenant {id}.");
            string prefix = source.AbsolutePath[..^TicketListSuffix.Length];
            return new(id, source.GetLeftPart(UriPartial.Authority) + prefix + TicketFormSuffix + "?id=" + id);
        }
        string validated = ValidateTemplate(template, source.AbsoluteUri);
        return new(id, validated.Replace("{id}", id, StringComparison.Ordinal));
    }

    internal static string NormalizeId(string? value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0 || text.Length > 64)
            throw Error("L’identifiant du ticket doit être un entier strictement positif, sans titre ni autre texte.");
        string[] groups = text.Split(GroupSpaces, StringSplitOptions.RemoveEmptyEntries);
        if (groups.Length > 1 && (groups[0].Length is < 1 or > 3 || groups.Skip(1).Any(group => group.Length != 3)))
            throw Error("Les espaces dans l’identifiant doivent uniquement séparer des groupes de trois chiffres.");
        string digits = string.Concat(groups);
        if (digits.Length == 0 || digits.Any(c => c is < '0' or > '9') ||
            !ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || id == 0)
            throw Error("L’identifiant du ticket doit contenir uniquement des chiffres et être strictement positif. Aucun numéro n’est déduit d’un titre.");
        return id.ToString(CultureInfo.InvariantCulture);
    }

    internal static string ValidateTemplate(string template, string sourceUrl)
    {
        if (template.Length > 8192 || Count(template, "{id}") != 1 ||
            template.Replace("{id}", "", StringComparison.Ordinal).IndexOfAny(new[] { '{', '}' }) >= 0)
            throw Error("Le modèle de fiche doit contenir une seule variable {id}, sans autre variable, et ne pas dépasser 8 192 caractères.");
        var source = ReadHttpUrl(sourceUrl, "La source GLPI");
        var target = ReadHttpUrl(template, "Le modèle de fiche ticket");
        if (!SameOrigin(source, target))
            throw Error("Le modèle de fiche doit utiliser la même origine HTTP(S) que la recherche GLPI.");
        if (!target.AbsolutePath.EndsWith(TicketFormSuffix, StringComparison.Ordinal) || target.Fragment.Length != 0)
            throw Error("Le modèle doit viser une fiche GLPI /front/ticket.form.php en lecture, sans fragment ni route de script personnalisée.");
        string targetPrefix = target.AbsolutePath[..^TicketFormSuffix.Length];
        string? sourcePrefix = GlpiPrefix(source.AbsolutePath);
        if (sourcePrefix is not null ? !sourcePrefix.Equals(targetPrefix, StringComparison.Ordinal)
            : !source.AbsolutePath.StartsWith(targetPrefix + "/", StringComparison.Ordinal))
            throw Error("La fiche et la recherche doivent appartenir à la même installation GLPI.");

        var parameters = ParseQuery(target);
        if (parameters.Count(pair => pair.Key == "id") != 1 || parameters.First(pair => pair.Key == "id").Value != "{id}")
            throw Error("Le modèle doit transmettre la variable uniquement sous la forme id={id}.");
        foreach (var pair in parameters)
        {
            if (pair.Key == "id") continue;
            if (pair.Key is not ("forcetab" or "_glpi_tab") || !Regex.IsMatch(pair.Value, @"\A[A-Za-z0-9_$\\:.\-]{1,128}\z"))
                throw Error("Le modèle contient un paramètre qui ne peut pas être prouvé en lecture seule. Seuls id, forcetab et _glpi_tab sont admis.");
        }
        return target.GetLeftPart(UriPartial.Path) + "?" + string.Join("&", parameters.Select(pair =>
            pair.Key + "=" + (pair.Key == "id" ? "{id}" : Uri.EscapeDataString(pair.Value))));
    }

    internal static Uri ReadHttpUrl(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsControl) || value.Contains('\\') ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
            throw Error(label + " doit être une adresse HTTP(S) absolue, sans identifiant ni caractère de contrôle.");
        string path = uri.AbsolutePath;
        for (int i = 0; i < 2; i++)
        {
            if (Regex.IsMatch(path, @"%(?:2f|5c|00|2e)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw Error(label + " contient un chemin encodé ambigu qui ne peut pas être utilisé pour une lecture automatique.");
            path = Uri.UnescapeDataString(path);
        }
        return uri;
    }

    internal static bool SameOrigin(Uri one, Uri two) =>
        one.Scheme.Equals(two.Scheme, StringComparison.OrdinalIgnoreCase) &&
        one.IdnHost.Equals(two.IdnHost, StringComparison.OrdinalIgnoreCase) && one.Port == two.Port;

    internal static List<KeyValuePair<string, string>> ParseQuery(Uri uri)
    {
        var result = new List<KeyValuePair<string, string>>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            string name = Decode(pair[0]);
            string value = pair.Length > 1 ? Decode(pair[1]) : "";
            if (name.Length == 0 || name.Any(char.IsControl) || value.Any(char.IsControl) || !names.Add(name))
                throw Error("L’adresse contient des paramètres dupliqués, vides ou ambigus.");
            result.Add(new(name, value));
        }
        return result;
    }

    internal static bool IsBusinessParameter(string name)
    {
        string key = name.Split('[', 2)[0].TrimStart('_').ToLowerInvariant();
        return key is "action" or "actions" or "delete" or "purge" or "update" or "restore" or "add" or "save" or "submit"
            or "massiveaction" or "massive_action" or "delete_document" or "sla_delete" or "ola_delete" ||
            key.StartsWith("delete_", StringComparison.Ordinal) || key.StartsWith("purge_", StringComparison.Ordinal) ||
            key.StartsWith("update_", StringComparison.Ordinal) || key.StartsWith("addme_", StringComparison.Ordinal);
    }

    private static string NormalizedHeader(string? value) => string.Join(" ", (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    private static string? GlpiPrefix(string path)
    {
        foreach (string marker in new[] { "/front/", "/ajax/", "/plugins/", "/marketplace/" })
        {
            int index = path.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0) return path[..index];
        }
        return null;
    }
    private static int Count(string value, string token) => (value.Length - value.Replace(token, "", StringComparison.Ordinal).Length) / token.Length;
    private static InvalidOperationException Error(string message) => new(message);
}
