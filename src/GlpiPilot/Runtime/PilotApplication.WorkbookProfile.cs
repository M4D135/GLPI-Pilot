using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed record WorkbookProfileDraft(string Token, string Path, string Fingerprint, long Revision,
        string SettingsFingerprint, DateTimeOffset ExpiresAt, WorkbookPilotProfile Profile);
    private WorkbookProfileDraft? _workbookProfileDraft;

    private void RegisterBusinessProfileOperations()
    {
        Register("dashboard.business.profile.preview", PreviewWorkbookProfileAsync);
        Register("dashboard.business.profile.apply", ApplyWorkbookProfileAsync);
    }

    private static object? WorkbookProfileSummary(WorkbookPilotProfile? profile) => profile is null ? null : new
    {
        available = true, profile.SourceFile, profile.ParameterSheet,
        weatherRuleCount = profile.WeatherRules.Count,
        visibleWeatherRuleCount = profile.WeatherRules.Count(rule => rule.Visible),
        siteCount = profile.Sites.Count, aliasCount = profile.Sites.Sum(site => site.Aliases.Count),
        profile.Notes, profile.FreshnessWarningHours, profile.Warnings
    };

    private async Task<object?> PreviewWorkbookProfileAsync(JsonElement args, CancellationToken ct)
    {
        _workbookProfileDraft = null;
        var path = await Host.OpenFileAsync("Reprendre le paramétrage du classeur de pilotage", "Classeur de pilotage Excel|*.xlsx;*.xlsm").ConfigureAwait(false);
        if (path is null) return new { cancelled = true };
        ct.ThrowIfCancellationRequested();
        if (Path.GetExtension(path).ToLowerInvariant() is not (".xlsx" or ".xlsm")) throw new InvalidDataException("Choisissez un classeur XLSX ou XLSM.");
        var before = await TestImportFingerprintAsync(path, ct).ConfigureAwait(false);
        var inspection = WorkbookPilotImportService.Inspect(path, ct);
        var after = await TestImportFingerprintAsync(path, ct).ConfigureAwait(false);
        if (before != after) throw new InvalidOperationException("Le classeur a changé pendant son examen. Choisissez-le de nouveau.");
        var profile = inspection.Profile;
        if (profile is null)
            throw new InvalidDataException("Aucun tableau de paramètres météo ou de sites reconnu dans ce classeur. " + string.Join(" ", inspection.Warnings));
        // Build and validate the complete proposed result before presenting an apply token.
        var next = profile.ApplyTo(Settings);
        var token = Guid.NewGuid().ToString("N");
        _workbookProfileDraft = new(token, path, after, Revision, JsonSerializer.Serialize(Settings, Json), DateTimeOffset.UtcNow.AddMinutes(20), profile);
        return new
        {
            token, fileName = Path.GetFileName(path), profile = WorkbookProfileSummary(profile),
            sitesAdded = next.Cities.Sites.Count - Settings.Cities.Sites.Count,
            sitesUpdated = profile.Sites.Count - (next.Cities.Sites.Count - Settings.Cities.Sites.Count),
            weatherReplaced = profile.WeatherRules.Count > 0,
            warnings = inspection.Warnings.Concat(profile.Warnings).ToArray(),
            simulation = IsTestMode
        };
    }

    private async Task<object?> ApplyWorkbookProfileAsync(JsonElement args, CancellationToken ct)
    {
        var draft = _workbookProfileDraft;
        if (draft is null || Text(args, "token", 96) != draft.Token || draft.Revision != Revision
            || draft.ExpiresAt < DateTimeOffset.UtcNow || draft.SettingsFingerprint != JsonSerializer.Serialize(Settings, Json))
            throw new InvalidOperationException("Les paramètres ont changé depuis l’aperçu. Examinez de nouveau le classeur.");
        if (await TestImportFingerprintAsync(draft.Path, ct).ConfigureAwait(false) != draft.Fingerprint)
            throw new InvalidOperationException("Le classeur a changé depuis l’aperçu. Examinez-le de nouveau avant application.");
        var next = draft.Profile.ApplyTo(Settings);
        ct.ThrowIfCancellationRequested(); CommitSettings(next); _workbookProfileDraft = null;
        Log($"PILOTAGE · paramètres du classeur repris : {draft.Profile.WeatherRules.Count} règles météo et {draft.Profile.Sites.Count} sites.");
        return new { settings = Settings, revision = Revision, simulation = IsTestMode };
    }
}
