using System.IO;

namespace GlpiPilot.Core;

[Flags]
public enum ConfigurationParts { None = 0, Source = 1, Extraction = 2, Ticket = 4, Indicators = 8, Dashboards = 16, Queries = 32, Cities = 64, Theme = 128, All = 255 }
public sealed record ConfigurationDifference(ConfigurationParts Part, string Section, string Current, string Incoming);

public static class ConfigurationReview
{
    public static IReadOnlyList<ConfigurationDifference> Describe(AppSettings current, AppSettings incoming) => new[]
    {
        new ConfigurationDifference(ConfigurationParts.Source, "Sources", Address(current) + $" · {current.Sources.Count} enregistrées", Address(incoming) + $" · {incoming.Sources.Count} enregistrées"),
        new(ConfigurationParts.Extraction, "Extraction", Extraction(current), Extraction(incoming)),
        new(ConfigurationParts.Ticket, "Détail des tickets", $"{current.TicketDetails.Sections.Count} sections", $"{incoming.TicketDetails.Sections.Count} sections"),
        new(ConfigurationParts.Indicators, "Indicateurs et règles", Rules(current), Rules(incoming)),
        new(ConfigurationParts.Dashboards, "Dashboards", Boards(current), Boards(incoming)),
        new(ConfigurationParts.Queries, "Requêtes", $"{current.Queries.Queries.Count} requêtes", $"{incoming.Queries.Queries.Count} requêtes"),
        new(ConfigurationParts.Cities, "Villes et sites", $"{current.Cities.Cities.Count} villes", $"{incoming.Cities.Cities.Count} villes"),
        new(ConfigurationParts.Theme, "Thème clair / sombre", current.Theme, incoming.Theme)
    };
    private static string Address(AppSettings value) => AppPaths.SafeUrl(value.PortalUrl);
    private static string Extraction(AppSettings value) => $"{value.AutoMode} · {value.MaxPages} pages · {value.MaxRows:N0} lignes max · {value.TimeoutSeconds} s SSO";
    private static string Rules(AppSettings value) => $"{value.Dashboard.ColumnMappings.Count} colonnes · {value.Dashboard.DateCulture} · seuils {value.Dashboard.StaleDays}/{value.Dashboard.OldDays} j";
    private static string Boards(AppSettings value) => $"{value.Dashboard.Layout.Boards.Count} globaux / {value.Dashboard.PersonalLayout.Boards.Count} personnels";

    public static AppSettings Apply(AppSettings current, AppSettings incoming, ConfigurationParts parts, bool mergeCollections)
    {
        var result = current.Copy();
        if (parts.HasFlag(ConfigurationParts.Extraction))
        {
            result.AutoMode = incoming.AutoMode; result.AutoSaveXlsx = incoming.AutoSaveXlsx;
            result.ReadySelector = incoming.ReadySelector; result.NextSelector = incoming.NextSelector; result.SelectorOrigin = incoming.SelectorOrigin;
            result.TimeoutSeconds = incoming.TimeoutSeconds; result.DownloadTimeoutSeconds = incoming.DownloadTimeoutSeconds;
            result.MaxPages = incoming.MaxPages; result.MaxRows = incoming.MaxRows; result.ExpectedTotal = incoming.ExpectedTotal;
            result.TableSelector = incoming.TableSelector; result.TableHeaderFingerprint = incoming.TableHeaderFingerprint;
            result.TableFrameOrigin = incoming.TableFrameOrigin; result.TableFrameName = incoming.TableFrameName; result.TableTargetUrl = incoming.TableTargetUrl;
            result.NativeSelectorOrigin = incoming.NativeSelectorOrigin; result.NativeMenuSelector = incoming.NativeMenuSelector;
            result.NativeFormatSelector = incoming.NativeFormatSelector; result.NativeFormatValue = incoming.NativeFormatValue;
            result.NativeScopeSelector = incoming.NativeScopeSelector; result.NativeScopeValue = incoming.NativeScopeValue; result.NativeSubmitSelector = incoming.NativeSubmitSelector;
        }
        if (parts.HasFlag(ConfigurationParts.Source))
        {
            result.PortalUrl = incoming.PortalUrl; result.BrowserChannel = incoming.BrowserChannel;
            result.Sources = mergeCollections ? Merge(current.Sources, incoming.Sources, x => x.Id, x => x.Copy(), 100, "sources") : incoming.Sources.Select(x => x.Copy()).ToList();
            result.ActiveSourceId = "";
        }
        if (parts.HasFlag(ConfigurationParts.Ticket)) result.TicketDetails = incoming.TicketDetails.Copy();
        if (parts.HasFlag(ConfigurationParts.Indicators))
        {
            var old = result.Dashboard; result.Dashboard = incoming.Dashboard.Copy();
            result.Dashboard.Layout = old.Layout; result.Dashboard.PersonalLayout = old.PersonalLayout;
            result.Dashboard.PersonalTechnician = old.PersonalTechnician; result.Dashboard.OpenPersonalAfterExtraction = old.OpenPersonalAfterExtraction;
        }
        if (parts.HasFlag(ConfigurationParts.Dashboards))
        {
            result.Dashboard.Layout = mergeCollections ? MergeLayout(result.Dashboard.Layout, incoming.Dashboard.Layout) : incoming.Dashboard.Layout.Copy();
            result.Dashboard.PersonalLayout = mergeCollections ? MergeLayout(result.Dashboard.PersonalLayout, incoming.Dashboard.PersonalLayout) : incoming.Dashboard.PersonalLayout.Copy();
        }
        if (parts.HasFlag(ConfigurationParts.Queries))
            result.Queries = new() { Queries = mergeCollections ? Merge(result.Queries.Queries, incoming.Queries.Queries, x => x.Id, x => x.Copy(), 128, "requêtes") : incoming.Queries.Copy().Queries };
        if (parts.HasFlag(ConfigurationParts.Cities))
        {
            if (!mergeCollections) result.Cities = incoming.Cities.Copy();
            else
            {
                result.Cities.Cities = Merge(result.Cities.Cities, incoming.Cities.Cities, x => x.Id, x => x.Copy(), 1000, "villes");
                result.Cities.Sites = Merge(result.Cities.Sites, incoming.Cities.Sites, x => x.Id, x => x.Copy(), 2000, "sites");
            }
        }
        if (parts.HasFlag(ConfigurationParts.Theme)) result.Theme = incoming.Theme;
        result.Cities.Validate(); result.Queries.Validate(); result.Dashboard.Layout.Validate(); result.Dashboard.PersonalLayout.Validate();
        result.Normalize(); return result;
    }
    private static DashboardLayoutSettings MergeLayout(DashboardLayoutSettings current, DashboardLayoutSettings incoming)
    {
        var result = current.Copy();
        result.Boards = Merge(result.Boards, incoming.Boards, x => x.Id, x => x.Copy(), 12, "dashboards");
        result.CatalogFavorites = result.CatalogFavorites.Union(incoming.CatalogFavorites, StringComparer.Ordinal).Take(512).ToList();
        result.FilterPresets = Merge(result.FilterPresets, incoming.FilterPresets, x => x.Name, x => x.Copy(), 30, "contextes");
        result.Validate(); return result;
    }
    private static List<T> Merge<T>(IEnumerable<T> current, IEnumerable<T> incoming, Func<T, string> key, Func<T, T> copy, int maximum, string label)
    {
        var result = current.Select(copy).ToList(); var ids = result.Select(key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.AddRange(incoming.Where(item => ids.Add(key(item))).Select(copy));
        if (result.Count > maximum) throw new InvalidDataException($"La fusion dépasserait {maximum} {label}. Réduisez la sélection ou choisissez de remplacer cette section.");
        return result;
    }
}
