using System.IO;

namespace GlpiPilot.Core;

public static class TicketLinkPolicy
{
    private static readonly HashSet<string> RelatedTypes = new(StringComparer.Ordinal)
    { "ticket", "problem", "change", "computer", "monitor", "printer", "networkequipment", "peripheral", "phone", "software", "user", "group" };
    public static Uri Validate(TicketReference ticket, TicketObservedItem item)
    {
        if (!Uri.TryCreate(ticket.Url, UriKind.Absolute, out var source) || !Uri.TryCreate(item.Url, UriKind.Absolute, out var target)
            || source.Scheme is not ("http" or "https") || target.Scheme != source.Scheme || target.Host != source.Host || target.Port != source.Port
            || target.UserInfo.Length != 0 || target.Fragment.Length != 0 || item.Identity.Length == 0 || item.Identity.Any(c => c is < '0' or > '9'))
            throw new InvalidDataException("Le lien ne correspond pas à une référence de consultation de cette installation.");
        const string form = "/front/ticket.form.php";
        if (!source.AbsolutePath.EndsWith(form, StringComparison.Ordinal)) throw new InvalidDataException("Installation GLPI inconnue.");
        var front = source.AbsolutePath[..^form.Length] + "/front/";
        var query = target.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2))
            .Select(pair => (Key: Uri.UnescapeDataString(pair[0]), Value: pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "")).ToArray();
        if (query.GroupBy(p => p.Key, StringComparer.Ordinal).Any(g => g.Count() > 1)) throw new InvalidDataException("Le lien contient des paramètres ambigus.");
        if (item.Kind == "document")
        {
            if (target.AbsolutePath != front + "document.send.php" || query.Any(p => p.Key is not ("docid" or "tickets_id" or "itemtype" or "items_id"))
                || query.SingleOrDefault(p => p.Key == "docid").Value != item.Identity
                || query.Any(p => (p.Key is "tickets_id" or "items_id") && p.Value != ticket.Id || p.Key == "itemtype" && p.Value != "Ticket"))
                throw new InvalidDataException("Le lien de document n’est pas un téléchargement GLPI associé à cette fiche.");
        }
        else if (item.Kind != "relation" || !RelatedTypes.Contains(item.RelatedType) || target.AbsolutePath != front + item.RelatedType + ".form.php"
            || query.Length != 1 || query[0].Key != "id" || query[0].Value != item.Identity)
            throw new InvalidDataException("La relation ne désigne pas une fiche GLPI de consultation reconnue.");
        return target;
    }
}
