using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlpiPilot.Core;

/// <summary>A deliberately bounded French grammar and evidence-based local summary; never guesses unconsumed clauses.</summary>
public static class LanguageAssistantLocalEngine
{
    public const string Help = "Moteur local sans réseau. Exemples : « tickets actifs par catégorie », « tickets en retard par technicien », "
        + "« tickets dont le titre contient \"VPN\" », « tickets actifs avec ville = \"Lyon\" par priorité », « moyenne de \"Coût\" des tickets actifs ». "
        + "Une synthèse locale accepte « synthèse », « synthèse par statut », « synthèse par priorité » ou tout autre rôle fourni. Les demandes hors de cette grammaire restent à reformuler ou à préparer avec le moteur compatible facultatif.";

    public static string Generate(LanguageAssistantRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Settings.Provider != "local") throw new InvalidOperationException("La demande ne sélectionne pas le moteur local.");
        if (request.Operation == "query")
        {
            var draft = Translate(request.Prompt, request.Context); ct.ThrowIfCancellationRequested();
            return JsonSerializer.Serialize(new { kind = "query", query = draft.Query, explanation = draft.Explanation, assumptions = draft.Assumptions }, LanguageAssistantService.Json);
        }
        if (request.Operation != "summary") throw new InvalidDataException("Opération locale inconnue.");
        var summary = Summarize(request.Prompt, request.Context, ct);
        return JsonSerializer.Serialize(new { kind = "summary", summary }, LanguageAssistantService.Json);
    }

    public static LanguageQueryDraft Translate(string prompt, LanguageAssistantContext context)
    {
        if (!QueryText.Valid(prompt, 2000, false, true)) throw new InvalidDataException("Demande locale vide ou trop longue.");
        var literals = new List<string>();
        var withTokens = Rx("«([^»]*)»|\"([^\"]*)\"").Replace(prompt, match =>
        {
            var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            literals.Add(value); return "@" + (literals.Count - 1).ToString(CultureInfo.InvariantCulture);
        });
        var text = Normalize(withTokens).Trim().TrimEnd('?', '.').Trim();
        text = Rx("^(?:(?:affiche|montre|liste|compte|donne)(?:-moi)? |nombre de |combien de )").Replace(text, "", 1);
        var query = new SavedQuery { Name = "Requête assistée", Category = "Assistant", BaseProbe = "total", Aggregate = "count", Match = "all" };
        var aggregate = Rx("^(moyenne|somme|mediane|minimum|maximum|nombre distinct) de (@[0-9]+) (?:des|de) ").Match(text);
        if (aggregate.Success)
        {
            query.Aggregate = aggregate.Groups[1].Value switch { "moyenne" => "average", "somme" => "sum", "mediane" => "median", "minimum" => "min", "maximum" => "max", _ => "distinct" };
            query.AggregateField = Field(aggregate.Groups[2].Value, literals, context); text = text[aggregate.Length..];
        }
        text = Rx("^(?:tous les |les |des )").Replace(text, "", 1);
        if (text != "tickets" && !text.StartsWith("tickets ", StringComparison.Ordinal)) throw Unsupported();
        text = text[7..].Trim();
        var bases = new (string Phrase, string Key)[]
        {
            ("sans technicien", "without-technician"), ("sans groupe", "without-group"), ("sans affectation", "unassigned"),
            ("en retard", "overdue"), ("a echeance proche", "soon"), ("en attente", "pending"), ("en cours", "processing"),
            ("de priorite haute", "high"), ("actifs", "active"), ("nouveaux", "new"), ("resolus", "resolved"), ("clos", "closed")
        };
        foreach (var basis in bases)
            if (text == basis.Phrase || text.StartsWith(basis.Phrase + " ", StringComparison.Ordinal))
            { query.BaseProbe = basis.Key; text = text[basis.Phrase.Length..].Trim(); break; }
        string groupText = "";
        if (text.StartsWith("par ", StringComparison.Ordinal)) { groupText = text[4..]; text = ""; }
        else
        {
            var offset = text.IndexOf(" par ", StringComparison.Ordinal);
            if (offset >= 0) { groupText = text[(offset + 5)..]; text = text[..offset].Trim(); }
        }
        if (groupText.Length > 0)
        {
            var groups = Rx(" puis | et | > ").Split(groupText);
            if (groups.Length is < 1 or > 3) throw Unsupported();
            var keys = groups.Select(g => Field(g.Trim(), literals, context)).ToArray();
            query.GroupBy = keys[0]; if (keys.Length > 1) query.GroupBySecondary = keys[1]; if (keys.Length > 2) query.GroupByTertiary = keys[2];
        }
        if (text.Length > 0)
        {
            if (!text.StartsWith("dont ", StringComparison.Ordinal) && !text.StartsWith("avec ", StringComparison.Ordinal)) throw Unsupported();
            text = text[5..];
            var clauses = text.Split(" et ", StringSplitOptions.None);
            if (clauses.Length > SavedQuery.MaximumConditions) throw Unsupported();
            foreach (var clause in clauses)
            {
                var match = Rx("^(.+?) (ne contient pas|contient|est egal a|est different de|commence par|finit par|=|!=|>=|<=|>|<) (@[0-9]+)$").Match(clause);
                if (match.Success)
                {
                    var op = match.Groups[2].Value switch
                    {
                        "contient" => "contains", "ne contient pas" => "not-contains", "est different de" or "!=" => "not-equals",
                        "commence par" => "starts-with", "finit par" => "ends-with", ">" => "number-gt", ">=" => "number-gte", "<" => "number-lt", "<=" => "number-lte", _ => "equals"
                    };
                    query.Conditions.Add(new() { Field = Field(match.Groups[1].Value, literals, context), Operator = op, Value = Literal(match.Groups[3].Value, literals) });
                    continue;
                }
                match = Rx("^(.+?) (est vide|est renseigne|est renseignee)$").Match(clause);
                if (!match.Success) throw Unsupported();
                query.Conditions.Add(new() { Field = Field(match.Groups[1].Value, literals, context), Operator = match.Groups[2].Value == "est vide" ? "empty" : "not-empty" });
            }
        }
        query.Name = prompt.Length > 106 ? prompt[..(char.IsHighSurrogate(prompt[105]) ? 105 : 106)] : prompt;
        query.Name = query.Name.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        LanguageAssistantService.ValidateQuery(query, context);
        var basisLabel = QueryService.BaseProbes.Single(b => b.Key == query.BaseProbe).Label;
        return new(query, $"Base : {basisLabel}. Calcul : {query.Aggregate}. {query.Conditions.Count} condition(s) combinée(s) par ET. Les clauses non reconnues sont refusées.",
            new[] { "Traduction déterministe selon la grammaire locale affichée.", "Les bases actifs, retards et priorités utilisent les réglages du dashboard. Vérifiez leur disponibilité et les résultats dans la prévisualisation." });
    }

    public static LanguageSummary Summarize(string prompt, LanguageAssistantContext context, CancellationToken ct = default)
    {
        var text = Normalize(prompt).Trim().TrimEnd('.', '?').Trim(); var role = "status";
        if (text is not ("synthese" or "resume" or "synthese des tickets" or "resume des tickets"))
        {
            var match = Rx("^(?:synthese|resume)(?: des tickets)? par (.+)$").Match(text);
            if (!match.Success) throw new InvalidDataException("Synthèse locale : indiquez « synthèse » ou « synthèse par statut / priorité / catégorie / technicien ». Seuls les champs fournis peuvent être utilisés.");
            role = Role(match.Groups[1].Value) ?? throw Unsupported();
        }
        if (context.Tickets.Count == 0 || context.Tickets.Any(t => !t.Fields.ContainsKey(role)))
            throw new InvalidDataException("Ce champ n’est pas fourni pour tous les tickets. Sélectionnez-le explicitement ou utilisez un autre regroupement.");
        var label = DashboardService.Roles.Single(r => r.Key == role).Label;
        var summary = new LanguageSummary { Title = "Répartition locale · " + label, Limitations = context.Limitations.ToList() };
        var groups = context.Tickets.GroupBy(t => t.Fields[role], StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
        foreach (var group in groups.Take(30))
        {
            ct.ThrowIfCancellationRequested(); var displayed = group.Key.Length == 0 ? "cellule vide" : group.Key.Length > 700 ? group.Key[..700] + "…" : group.Key;
            summary.Claims.Add(new()
            {
                Text = $"{group.Count()} ticket(s) fourni(s) présentent {label.ToLowerInvariant()} : « {displayed} ».",
                Citations = group.Select(ticket => new LanguageCitation { TicketId = ticket.Id, Field = role, Quote = ticket.Fields[role] }).ToList()
            });
        }
        if (groups.Length > 30) summary.Limitations.Add($"Seuls les 30 groupes les plus nombreux sont présentés sur {groups.Length} valeurs distinctes.");
        summary.Limitations.Add($"Décompte des {context.Tickets.Count} tickets explicitement fournis sur {context.ScopeRows} lignes du périmètre ; aucune extrapolation.");
        summary.Limitations = summary.Limitations.Take(30).ToList();
        LanguageAssistantService.ValidateSummary(summary, context); return summary;
    }

    private static string Field(string text, IReadOnlyList<string> literals, LanguageAssistantContext context)
    {
        text = Rx("^(?:le |la |les |l'|champ )").Replace(text.Trim(), "", 1);
        var field = text.StartsWith('@') ? "column:" + Literal(text, literals) : "role:" + (Role(text) ?? throw Unsupported());
        if (!context.Fields.Any(f => f.Key == field)) throw new InvalidDataException("Le champ demandé n’est pas disponible dans le relevé : " + field);
        return field;
    }
    private static string Literal(string token, IReadOnlyList<string> literals)
    {
        if (!token.StartsWith('@') || !int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= literals.Count)
            throw Unsupported();
        return literals[index];
    }
    private static string? Role(string name) => name.Trim() switch
    {
        "identifiant" or "id" => "id", "titre" or "objet" => "title", "statut" or "etat" => "status", "priorite" => "priority", "urgence" => "urgency", "impact" => "impact",
        "type" => "type", "ville" => "city", "lieu" => "location", "categorie" => "category", "entite" => "entity", "technicien" or "technicien attribue" => "assignee",
        "groupe" or "groupe attribue" => "group", "demandeur" => "requester", "date d'ouverture" or "ouverture" => "created", "echeance" => "due", "modification" => "updated",
        "resolution" => "resolved", "cloture" => "closed", "satisfaction" => "satisfaction", "source" or "origine" => "source", "code postal" => "postal", _ => null
    };
    private static string Normalize(string value)
    {
        var builder = new StringBuilder();
        foreach (var ch in value.Normalize(NormalizationForm.FormD)) if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) builder.Append(char.ToLowerInvariant(ch == '’' ? '\'' : ch));
        return Rx("\\s+").Replace(builder.ToString().Normalize(NormalizationForm.FormC), " ");
    }
    private static Regex Rx(string pattern) => new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    private static InvalidDataException Unsupported() => new("Une partie de la demande n’appartient pas à la grammaire locale. Aucun filtre n’a été ignoré. Reformulez en suivant les exemples ou choisissez explicitement le moteur compatible.");
}
