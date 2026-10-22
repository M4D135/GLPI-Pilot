using System.Runtime.InteropServices;
using System.Text;

namespace GlpiPilot.Core;

public static class DiagnosticReport
{
    public static string Create(AppSettings settings, ExtractionResult? result, string sessionLog)
    {
        var report = new StringBuilder();
        report.AppendLine($"GLPI Pilot {AppPaths.ApplicationVersion} — diagnostic");
        report.AppendLine("Date : " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        report.AppendLine("Système : " + RuntimeInformation.OSDescription + " · " + RuntimeInformation.ProcessArchitecture);
        report.AppendLine("Runtime : " + RuntimeInformation.FrameworkDescription);
        report.AppendLine($"Navigateur : {settings.BrowserChannel} · mode : {settings.AutoMode} · thème : {settings.Theme}");
        report.AppendLine("Parcours automatique : navigateur ouvert derrière le logiciel ; priorité de fenêtre suspendue par Voir le navigateur.");
        report.AppendLine("Adresse source (paramètres masqués) : " + AppPaths.SafeUrl(settings.PortalUrl));
        if (Uri.TryCreate(settings.PortalUrl, UriKind.Absolute, out var url))
            report.AppendLine("Nombre de paramètres mémorisés : " + url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Length);
        report.AppendLine($"Attente SSO : {settings.TimeoutSeconds} s · export : {settings.DownloadTimeoutSeconds} s");
        report.AppendLine($"Limites : {settings.MaxRows} lignes · {settings.MaxPages} pages · Excel auto : {settings.AutoSaveXlsx}");
        report.AppendLine($"Repères mémorisés : tableau={settings.TableHeaderFingerprint.Length > 0}, session={settings.ReadySelector.Length > 0}, suivant={settings.NextSelector.Length > 0}, export={settings.NativeSubmitSelector.Length > 0}");
        report.AppendLine($"Détail des tickets : automatique={settings.TicketDetails.AutoLoadOnSelection}, détection={settings.TicketDetails.IncludeAutoDetected}, sections={settings.TicketDetails.Sections.Count}, limite={settings.TicketDetails.MaxRowsPerSection}");
        report.AppendLine($"Dashboard : correspondances={settings.Dashboard.ColumnMappings.Count}, inactivité={settings.Dashboard.StaleDays} j, ancienneté={settings.Dashboard.OldDays} j, échéance={settings.Dashboard.DueSoonHours} h, dates={settings.Dashboard.DateCulture}");
        report.AppendLine($"Présentation : tableaux de bord={settings.Dashboard.Layout.Boards.Count}, widgets={settings.Dashboard.Layout.Boards.Sum(board => board.Widgets.Count)}, technicien personnel mémorisé={!string.IsNullOrWhiteSpace(settings.Dashboard.PersonalTechnician)}");
        report.AppendLine($"Présentation personnelle : tableaux de bord={settings.Dashboard.PersonalLayout.Boards.Count}, widgets={settings.Dashboard.PersonalLayout.Boards.Sum(board => board.Widgets.Count)}, ouverture automatique={settings.Dashboard.OpenPersonalAfterExtraction}");
        report.AppendLine($"Villes : actif={settings.Cities.Enabled}, villes={settings.Cities.Cities.Count}, variantes={settings.Cities.Cities.Sum(city => city.Aliases.Count)}, coordonnées={settings.Cities.Cities.Count(city => city.Latitude.HasValue && city.Longitude.HasValue)}, colonne du titre configurée={!string.IsNullOrWhiteSpace(settings.Cities.TitleColumn)}");
        report.AppendLine($"Atelier de requêtes : définitions={settings.Queries.Queries.Count}, conditions={settings.Queries.Queries.Sum(query => query.Conditions.Count)}, regroupements={settings.Queries.Queries.Count(query => query.GroupBy.Length > 0)} ; noms et critères non inclus.");
        if (result != null)
        {
            report.AppendLine($"Résultat : lignes={result.Rows.Count}, colonnes={result.Headers.Count}, pages={result.Pages}, attendu={result.ExpectedTotal?.ToString() ?? "inconnu"}, total vérifié={result.Complete}, avertissements={result.Warnings.Count}");
            report.AppendLine("--- Intitulés des colonnes du relevé (aucune valeur de cellule) ---");
            foreach (var (header, index) in result.Headers.Take(128).Select((header, index) => (header, index)))
                report.AppendLine($"COLONNE {index + 1} : {HeaderLabel(header)}");
            if (result.Headers.Count > 128) report.AppendLine("Intitulés limités aux 128 premières colonnes.");
            foreach (var column in DashboardService.ResolveColumns(result.Headers, settings.Dashboard))
                report.AppendLine($"CORRESPONDANCE {column.Role} : {column.State} · colonne={(column.Index.HasValue ? (column.Index.Value + 1).ToString() : "aucune")}");
        }
        report.AppendLine("Aucun contenu de cellule, cookie, mot de passe ou jeton SSO n’est inclus.");
        report.AppendLine("--- Journal de cette session ---");
        foreach (var line in sessionLog.Split('\n').TakeLast(500)) report.AppendLine(AppPaths.SafeLog(line.TrimEnd('\r')));
        return report.ToString();
    }

    private static string HeaderLabel(string value)
    {
        var clean = new StringBuilder();
        foreach (char character in AppPaths.SafeLog(value ?? "").Take(200))
        {
            if (char.IsControl(character)) clean.Append($"\\u{(int)character:X4}");
            else clean.Append(character);
        }
        if (value?.Length > 200) clean.Append('…');
        return clean.ToString();
    }
}
