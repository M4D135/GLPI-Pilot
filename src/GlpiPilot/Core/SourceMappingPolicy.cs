using System.IO;
using System.Runtime.CompilerServices;

namespace GlpiPilot.Core;

public enum SourceMappingTarget { CurrentConfiguration, LocalResult, ArchiveReadOnly, Unavailable }

/// <summary>Decides where an explicit mapping edit may be applied without changing another source.</summary>
public static class SourceMappingPolicy
{
    public static SourceMappingTarget Resolve(ExtractionResult? source, AppSettings settings, bool historical)
    {
        if (historical) return SourceMappingTarget.ArchiveReadOnly;
        if (source == null) return SourceMappingTarget.CurrentConfiguration;
        if (source.Provenance.SourceKind is "file" or "demo" or "local") return SourceMappingTarget.LocalResult;
        if (!source.Provenance.SearchVerified || !source.Provenance.ScopeVerified) return SourceMappingTarget.Unavailable;
        try
        {
            var sourceKey = SnapshotStore.SourceKey(source.Provenance.RequestedUrl);
            if (sourceKey.Length == 0 || sourceKey != SnapshotStore.SourceKey(settings.PortalUrl)) return SourceMappingTarget.Unavailable;
            if (settings.ActiveSourceId.Length > 0)
            {
                var profile = settings.Sources.SingleOrDefault(item => item.Id == settings.ActiveSourceId);
                if (profile == null || sourceKey != SnapshotStore.SourceKey(profile.Url)) return SourceMappingTarget.Unavailable;
            }
            return SourceMappingTarget.CurrentConfiguration;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or UriFormatException)
        { return SourceMappingTarget.Unavailable; }
    }
}

/// <summary>Session-only interpretation of an imported result; it never mutates a saved GLPI profile.</summary>
public sealed class LocalResultMappingStore
{
    private readonly ConditionalWeakTable<ExtractionResult, DashboardSettings> _settings = new();
    public DashboardSettings? Get(ExtractionResult? source) => source != null && _settings.TryGetValue(source, out var settings) ? settings.Copy() : null;
    public void Set(ExtractionResult source, DashboardSettings settings)
    {
        if (source.Provenance.SourceKind is not ("file" or "demo" or "local")) throw new InvalidDataException("Ces correspondances locales sont réservées aux imports, aux bibliothèques locales et aux démonstrations.");
        var copy = settings.Copy(); DashboardService.ValidateSettings(copy);
        _settings.Remove(source); _settings.Add(source, copy);
    }
}
