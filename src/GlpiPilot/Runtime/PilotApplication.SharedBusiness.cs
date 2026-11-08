using System.Globalization;
using System.IO;
using System.Text.Json;
using GlpiPilot.Core;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private sealed class BusinessLocalEntry
    {
        public string Kind { get; set; } = "";
        public string Key { get; set; } = "";
        public string Hash { get; set; } = "";
        public long Revision { get; set; }
        public long CentralRevision { get; set; }
    }
    private sealed class BusinessLocalState
    {
        public int Version { get; set; } = 1;
        public string Binding { get; set; } = "";
        public Dictionary<string, BusinessLocalEntry> Entries { get; set; } = new(StringComparer.Ordinal);
        public string FollowedDatasetId { get; set; } = "";
    }
    private ExtractionResult? _sharedDatasetSource;
    private long _sharedDatasetSourceRevision = -1;
    private string _sharedDatasetBinding = "";
    private ExtractionResult? _sharedDatasetLoaded;
    private readonly List<string> _sharedBusinessWarnings = new();

    private void RegisterSharedBusinessOperations()
    {
        Register("sharing.datasets", async (_, ct) => new { items = await TeamWorkspace.BusinessDatasetsAsync(ct).ConfigureAwait(false), enabled = LibrarySettings.SharingEnabled,
            followedId = ReadBusinessLocalState().FollowedDatasetId, warnings = _sharedBusinessWarnings.ToArray() });
        Register("sharing.dataset.load", async (a, ct) =>
        {
            var item = await TeamWorkspace.GetAsync(Text(a, "id", 32), ct).ConfigureAwait(false);
            if (item is null || item.Kind != "team-dataset" || item.Deleted) throw new InvalidDataException("Ce relevé partagé n’est plus disponible.");
            if (a.TryGetProperty("expectedRevision", out var expected) && expected.GetInt64() != item.Revision) throw new InvalidOperationException("Le relevé a changé. Actualisez la liste avant de l’ouvrir.");
            LoadSharedDataset(item); var state = ReadBusinessLocalState(); state.FollowedDatasetId = item.Id; WriteBusinessLocalState(state);
            return new { session = SessionState(), message = "Le relevé commun est chargé. Ses prochaines versions seront reçues lors des synchronisations." };
        });
        Register("sharing.business.status", SharedBusinessStatusAsync);
        Register("sharing.business.conflict", SharedBusinessConflictAsync);
        Register("sharing.business.resolve", ResolveSharedBusinessConflictAsync);
        Register("sharing.business.export", ExportSharedBusinessConflictAsync);
    }
    private BusinessLocalState ReadBusinessLocalState()
    {
        var path = DataPath("shared-business.local.json");
        var binding = TicketActionService.Hash(LibrarySettings.DatabasePath + "\n" + LibrarySettings.SharingBinding());
        if (!File.Exists(path)) return new() { Binding = binding };
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Le suivi local du partage dépasse sa taille autorisée.");
        var state = JsonSerializer.Deserialize<BusinessLocalState>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Suivi local du partage vide.");
        if (state.Version != 1 || state.Entries is null || state.Entries.Count > 25000 || state.Entries.Any(p => p.Key.Length != 32 || !p.Key.All(Uri.IsHexDigit) || p.Value is null || !SharedBusinessDocuments.IsKind(p.Value.Kind)))
            throw new InvalidDataException("Le suivi local du partage est invalide.");
        return state.Binding == binding ? state : new() { Binding = binding };
    }
    private void WriteBusinessLocalState(BusinessLocalState state)
    {
        var path = DataPath("shared-business.local.json"); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(state, Json)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private TeamWorkspaceItem BusinessItem(string kind, string key, string title, object value) => SharedBusinessDocuments.Create(kind, key, title, value, LibrarySettings.DisplayName);
    private List<TeamWorkspaceItem> LocalBusinessItems()
    {
        var items = new List<TeamWorkspaceItem>
        {
            BusinessItem("team-configuration", "common", "Sites, règles et tableaux de bord communs", SharedBusinessDocuments.Configuration(Settings))
        };
        foreach (var tour in ScheduledTourStore.Load(DataPath("scheduled-tours.json")).Tours)
            items.Add(BusinessItem("team-tour", tour.Id, tour.Name, tour));
        var emptyPreference = JsonSerializer.SerializeToElement(new SharedTechnicianBusiness(), SharedBusinessDocuments.Json).GetRawText();
        foreach (var pair in WorkTechnicianPreferences().Technicians.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var business = SharedTechnicianBusiness.From(pair.Value);
            if (JsonSerializer.SerializeToElement(business, SharedBusinessDocuments.Json).GetRawText() != emptyPreference)
                items.Add(BusinessItem("team-technician", pair.Key, "Capacités · " + WorkClip(pair.Key, 270), business));
        }
        if (File.Exists(DataPath("dispatch-scenario.json")))
        { var scenario = WorkScenario(); items.Add(BusinessItem("team-dispatch", "common", scenario.Name, scenario)); }
        foreach (var location in CityOverrideStore.Load(DataPath("city-overrides.json")).Overrides)
            items.Add(BusinessItem("team-location", SharedBusinessDocuments.LocationKey(location), "Localisation du ticket " + WorkClip(location.TicketId, 200), location));
        foreach (var order in TourOrderPreferences.Load(DataPath("tour-order.json")).Profiles.OrderBy(p => p.Key, StringComparer.Ordinal))
            items.Add(BusinessItem("team-order", order.Key, "Ordre de tournée · " + WorkClip(order.Key, 260), order.Value));
        return items;
    }
    private async Task StageSharedBusinessAsync(CancellationToken ct)
    {
        if (!LibrarySettings.SharingEnabled) return;
        _sharedBusinessWarnings.Clear(); var state = ReadBusinessLocalState(); var units = LocalBusinessItems();
        var existingIds = (await TeamWorkspace.BusinessMetadataAsync(ct).ConfigureAwait(false)).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        ExtractionResult? stagedSource = null; var stagedRevision = Revision;
        if (TerrainNativeSource() is { } source && !IsHistorical && (!ReferenceEquals(source, _sharedDatasetLoaded) || _sharedDatasetBinding != state.Binding)
            && (!ReferenceEquals(source, _sharedDatasetSource) || Revision != _sharedDatasetSourceRevision || _sharedDatasetBinding != state.Binding))
        {
            try
            {
                var dataset = SharedBusinessDocuments.Dataset(source, EffectiveSettings.Dashboard);
                units.Add(BusinessItem("team-dataset", dataset.SourceKey, dataset.Title.Length == 0 ? "Relevé commun" : dataset.Title, dataset));
                stagedSource = source;
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
            { _sharedBusinessWarnings.Add(error.Message); }
        }
        var present = units.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in units)
        {
            ct.ThrowIfCancellationRequested(); var envelope = SharedBusinessDocuments.Read(item); var hash = SharedBusinessDocuments.Hash(envelope.Data);
            if (state.Entries.TryGetValue(item.Id, out var marker) && marker.Hash == hash && existingIds.Contains(item.Id)) continue;
            var existing = await TeamWorkspace.GetAsync(item.Id, ct).ConfigureAwait(false);
            if (item.Kind == "team-configuration" && (marker is null || marker.Hash == hash) && existing is null && SharedBusinessDocuments.IsDefaultConfiguration(Settings))
            { state.Entries[item.Id] = new() { Kind = item.Kind, Key = envelope.Key, Hash = hash }; continue; }
            if (existing is not null)
            {
                var prior = SharedBusinessDocuments.Read(existing); envelope.AcknowledgedConflicts = prior.AcknowledgedConflicts;
                if (!existing.Deleted && SharedBusinessDocuments.Hash(prior.Data) == hash)
                { state.Entries[item.Id] = new() { Kind = item.Kind, Key = envelope.Key, Hash = hash, Revision = existing.Revision, CentralRevision = existing.CentralRevision }; continue; }
            }
            item.Payload = JsonSerializer.SerializeToElement(envelope, SharedBusinessDocuments.Json);
            var saved = await TeamWorkspace.StageBusinessAsync(item, marker?.Revision, marker?.CentralRevision, ct).ConfigureAwait(false);
            state.Entries[item.Id] = new() { Kind = item.Kind, Key = envelope.Key, Hash = hash, Revision = saved.Revision, CentralRevision = saved.CentralRevision };
        }
        foreach (var pair in state.Entries.ToArray())
        {
            if (pair.Value.Kind is not ("team-tour" or "team-technician" or "team-location" or "team-order") || present.Contains(pair.Key) || pair.Value.Hash == "deleted") continue;
            var file = pair.Value.Kind switch { "team-tour" => "scheduled-tours.json", "team-technician" => "technician-preferences.json", "team-location" => "city-overrides.json", _ => "tour-order.json" };
            // Deleting an item in its tool saves an existing, possibly empty
            // registry. A missing/unreadable registry is not evidence of deletion.
            if (!File.Exists(DataPath(file))) continue;
            var existing = await TeamWorkspace.GetAsync(pair.Key, ct).ConfigureAwait(false);
            if (existing is not null && !existing.Deleted)
            { existing.Deleted = true; var removed = await TeamWorkspace.StageBusinessAsync(existing, pair.Value.Revision, pair.Value.CentralRevision, ct).ConfigureAwait(false); pair.Value.Revision = removed.Revision; pair.Value.CentralRevision = removed.CentralRevision; }
            pair.Value.Hash = "deleted";
        }
        WriteBusinessLocalState(state);
        if (stagedSource is not null) { _sharedDatasetSource = stagedSource; _sharedDatasetSourceRevision = stagedRevision; _sharedDatasetBinding = state.Binding; }
    }
    private async Task ApplySharedBusinessAsync(CancellationToken ct)
    {
        if (!LibrarySettings.SharingEnabled) return;
        var state = ReadBusinessLocalState(); var changed = new List<TeamWorkspaceItem>();
        foreach (var metadata in await TeamWorkspace.BusinessMetadataAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (state.Entries.TryGetValue(metadata.Id, out var old) && old.Revision == metadata.Revision) continue;
            var item = await TeamWorkspace.GetAsync(metadata.Id, ct).ConfigureAwait(false);
            if (item is null || item.Pending) continue;
            var envelope = SharedBusinessDocuments.Read(item); var hash = item.Deleted ? "deleted" : SharedBusinessDocuments.Hash(envelope.Data);
            if (old?.Hash != hash)
            {
                if (item.Kind == "team-dataset")
                {
                    if (!item.Deleted && (Result is null && state.FollowedDatasetId == item.Id || ReferenceEquals(TerrainNativeSource(), _sharedDatasetLoaded) && state.FollowedDatasetId == item.Id))
                        LoadSharedDataset(item);
                }
                else changed.Add(item);
            }
            state.Entries[item.Id] = new() { Kind = item.Kind, Key = envelope.Key, Hash = hash, Revision = item.Revision, CentralRevision = item.CentralRevision };
        }
        ApplyBusinessItems(changed);
        if (Result is null)
        {
            var datasets = await TeamWorkspace.BusinessDatasetsAsync(ct).ConfigureAwait(false);
            var sourceKey = string.IsNullOrWhiteSpace(Settings.PortalUrl) ? "" : SnapshotStore.SourceKey(SharedBusinessDocuments.PublicSourceUrl(Settings.PortalUrl));
            var matching = datasets.Where(x => x.UrlKey == sourceKey).ToArray();
            var choice = datasets.FirstOrDefault(x => x.Id == state.FollowedDatasetId) ?? (matching.Length == 1 ? matching[0] : null)
                ?? (datasets.Count == 1 ? datasets[0] : null);
            if (choice is not null)
            {
                var item = await TeamWorkspace.GetAsync(choice.Id, ct).ConfigureAwait(false);
                if (item is not null && !item.Deleted) { LoadSharedDataset(item); state.FollowedDatasetId = item.Id; }
            }
        }
        WriteBusinessLocalState(state);
        if (changed.Count > 0)
        {
            Revision++; InvalidatePreparedActions(); _terrainTourContext = null; _terrainProjection = null;
            Publish("settings.changed", new { settings = Settings, revision = Revision });
            Publish("workplace.changed", new { revision = Revision, shared = true });
            Publish("technicians.changed", new { shared = true }); Publish("calendar.changed", new { shared = true });
            Publish("session.changed", SessionState());
        }
    }
    private void BackupBusinessFile(string name)
    {
        var source = DataPath(name); if (!File.Exists(source)) return;
        var directory = DataPath("SharedImports"); Directory.CreateDirectory(directory);
        File.Copy(source, Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6] + ".json"));
    }
    private void ApplyBusinessItems(IReadOnlyList<TeamWorkspaceItem> items)
    {
        foreach (var item in items.Where(x => x.Kind is "team-configuration" or "team-dispatch")) ApplyBusinessItem(item);
        foreach (var group in items.Where(x => x.Kind is not ("team-configuration" or "team-dispatch")).GroupBy(x => x.Kind))
        {
            // One bounded read, backup and atomic save per file, even when thousands
            // of independent ticket corrections arrive during the first pull.
            if (group.Key == "team-tour")
            {
                var path = DataPath("scheduled-tours.json"); var store = ScheduledTourStore.Load(path);
                foreach (var item in group) { var key = SharedBusinessDocuments.Read(item).Key; store.Tours.RemoveAll(x => x.Id == key); if (!item.Deleted) store.Tours.Add(SharedBusinessDocuments.Data<ScheduledTour>(item)); }
                store.Validate(); BackupBusinessFile("scheduled-tours.json"); store.Save(path);
            }
            else if (group.Key == "team-technician")
            {
                var store = WorkTechnicianPreferences();
                foreach (var item in group)
                {
                    var key = SharedBusinessDocuments.Read(item).Key; var personal = store.Get(key);
                    var value = item.Deleted ? new TechnicianPreference() : SharedBusinessDocuments.Data<SharedTechnicianBusiness>(item).ToPreference();
                    value.Favorite = personal.Favorite; value.ActiveTarget = personal.ActiveTarget; value.Notes = personal.Notes; value.AvailabilityNote = personal.AvailabilityNote;
                    store = store.With(key, value);
                }
                BackupBusinessFile("technician-preferences.json"); store.Save(DataPath("technician-preferences.json"));
            }
            else if (group.Key == "team-location")
            {
                var path = DataPath("city-overrides.json"); var store = CityOverrideStore.Load(path); var fingerprint = store.Fingerprint();
                var values = store.Overrides.ToDictionary(value => (value.SourceKey, value.TicketId));
                var removals = store.RemovedCorrections.ToDictionary(value => (value.SourceKey, value.TicketId));
                foreach (var item in group)
                {
                    var value = SharedBusinessDocuments.Data<CityTicketOverride>(item); var key = (value.SourceKey, value.TicketId);
                    if (item.Deleted) { values.Remove(key); removals[key] = new(value.SourceKey, value.TicketId, DateTimeOffset.UtcNow); }
                    else { values[key] = value; removals.Remove(key); }
                }
                store.Overrides = values.Values.ToList(); store.RemovedCorrections = removals.Values.ToList(); store.Validate();
                BackupBusinessFile("city-overrides.json"); store.Save(path, fingerprint); InvalidateSharedLocations();
            }
            else if (group.Key == "team-order")
            {
                var store = TourOrderPreferences.Load(DataPath("tour-order.json"));
                foreach (var item in group) { var key = SharedBusinessDocuments.Read(item).Key; if (item.Deleted) store.Profiles.Remove(key); else store.Profiles[key] = SharedBusinessDocuments.Data<TourOrderProfile>(item); }
                BackupBusinessFile("tour-order.json"); store.Save(DataPath("tour-order.json"));
            }
        }
    }
    private void ApplyBusinessItem(TeamWorkspaceItem item)
    {
        var envelope = SharedBusinessDocuments.Read(item);
        switch (item.Kind)
        {
            case "team-configuration":
                if (item.Deleted) return;
                var incoming = SharedBusinessDocuments.Data<SharedBusinessConfiguration>(item); var local = Settings.Dashboard;
                incoming.Dashboard.PersonalTechnician = local.PersonalTechnician; incoming.Dashboard.PersonalLayout = local.PersonalLayout.Copy();
                incoming.Dashboard.OpenPersonalAfterExtraction = local.OpenPersonalAfterExtraction;
                incoming.Dashboard.Layout.CatalogFavorites = local.Layout.CatalogFavorites.ToList(); incoming.Dashboard.Layout.CatalogRecent = local.Layout.CatalogRecent.ToList();
                if (incoming.Dashboard.Layout.Boards.Any(x => x.Id == local.Layout.ActiveBoardId)) incoming.Dashboard.Layout.ActiveBoardId = local.Layout.ActiveBoardId;
                BackupBusinessFile("settings.json"); Settings.Dashboard = incoming.Dashboard; Settings.Cities = incoming.Cities; Settings.Queries = incoming.Queries; SaveSettings();
                RefreshSharedDatasetRules();
                break;
            case "team-tour":
                var tours = ScheduledTourStore.Load(DataPath("scheduled-tours.json")); BackupBusinessFile("scheduled-tours.json");
                tours.Tours.RemoveAll(x => x.Id == envelope.Key);
                if (!item.Deleted) tours.Tours.Add(SharedBusinessDocuments.Data<ScheduledTour>(item));
                tours.Save(DataPath("scheduled-tours.json"));
                break;
            case "team-technician":
                var preferences = WorkTechnicianPreferences(); var personal = preferences.Get(envelope.Key);
                var business = item.Deleted ? new TechnicianPreference() : SharedBusinessDocuments.Data<SharedTechnicianBusiness>(item).ToPreference();
                business.Favorite = personal.Favorite; business.ActiveTarget = personal.ActiveTarget; business.Notes = personal.Notes; business.AvailabilityNote = personal.AvailabilityNote;
                BackupBusinessFile("technician-preferences.json"); preferences.With(envelope.Key, business).Save(DataPath("technician-preferences.json"));
                break;
            case "team-dispatch":
                BackupBusinessFile("dispatch-scenario.json"); TechnicianPlanningService.Save(DataPath("dispatch-scenario.json"), item.Deleted ? new() : SharedBusinessDocuments.Data<DispatchScenario>(item));
                break;
            case "team-location":
                var locations = CityOverrideStore.Load(DataPath("city-overrides.json")); var location = SharedBusinessDocuments.Data<CityTicketOverride>(item);
                BackupBusinessFile("city-overrides.json");
                locations.With(item.Deleted ? null : location, location.SourceKey, location.TicketId).Save(DataPath("city-overrides.json"), locations.Fingerprint());
                InvalidateSharedLocations();
                break;
            case "team-order":
                var orders = TourOrderPreferences.Load(DataPath("tour-order.json")); BackupBusinessFile("tour-order.json");
                if (item.Deleted) orders.Profiles.Remove(envelope.Key); else orders.Profiles[envelope.Key] = SharedBusinessDocuments.Data<TourOrderProfile>(item);
                orders.Save(DataPath("tour-order.json"));
                break;
        }
    }
    private void LoadSharedDataset(TeamWorkspaceItem item)
    {
        var content = SharedBusinessDocuments.OpenDataset(item); var context = Settings.Copy();
        context.Dashboard.ColumnMappings = new(content.ColumnMappings, StringComparer.OrdinalIgnoreCase);
        _localMappings.Set(content.Table, context.Dashboard); SetResult(content.Table, context: context); _sharedDatasetLoaded = Result;
    }
    private void InvalidateSharedLocations()
    {
        // A sync can change working corrections, never the interpretation of an open archive/cache.
        if (IsHistorical) return;
        if (Locations is { } report && ReferenceEquals(Result, report.Data))
        {
            var followed = ReferenceEquals(_sharedDatasetLoaded, Result) || ReferenceEquals(_sharedDatasetLoaded, report.Source);
            if (_localMappings.Get(report.Data) is { } mapping) _localMappings.Set(report.Source, mapping);
            Result = report.Source; if (followed) _sharedDatasetLoaded = Result;
        }
        Locations = null; _terrainTourContext = null; _terrainProjection = null;
    }
    private void RefreshSharedDatasetRules()
    {
        if (IsHistorical || Result is not { Provenance.Method: "shared-records" } current) return;
        var data = Locations?.Source ?? current; var previous = _localMappings.Get(data) ?? _localMappings.Get(current) ?? _resultContext?.Dashboard;
        var dashboard = Settings.Dashboard.Copy();
        if (previous is not null) dashboard.ColumnMappings = new(previous.ColumnMappings, StringComparer.OrdinalIgnoreCase);
        _localMappings.Set(data, dashboard); _localMappings.Set(current, dashboard);
        _resultContext = Settings.Copy(); _resultContext.Dashboard = dashboard;
    }
    private async Task<object?> SharedBusinessStatusAsync(JsonElement _, CancellationToken ct)
    {
        var documents = await TeamWorkspace.BusinessMetadataAsync(ct).ConfigureAwait(false);
        var conflicts = await TeamWorkspace.BusinessConflictsAsync(ct).ConfigureAwait(false);
        return new { enabled = LibrarySettings.SharingEnabled, documents = documents.Count(x => !x.Deleted), conflicts = conflicts.Take(100), moreConflicts = conflicts.Count > 100, warnings = _sharedBusinessWarnings.ToArray() };
    }
    private async Task<object?> SharedBusinessConflictAsync(JsonElement a, CancellationToken ct)
    {
        var item = await TeamWorkspace.GetAsync(Text(a, "id", 32), ct).ConfigureAwait(false);
        var conflict = await TeamWorkspace.GetConflictAsync(Text(a, "id", 32), Text(a, "conflictId", 64), ct).ConfigureAwait(false);
        if (item is null || !SharedBusinessDocuments.IsKind(item.Kind) || conflict is null) throw new InvalidDataException("Ces versions communes ne sont plus disponibles.");
        EnsureSharedBusinessLocalUnchanged(item, conflict);
        return new { id = item.Id, conflictId = conflict.Id, expectedRevision = item.Revision, item.Title, item.Kind,
            current = BusinessConflictPreview(item), incoming = BusinessConflictPreview(conflict.Incoming) };
    }
    private static object BusinessConflictPreview(TeamWorkspaceItem item)
    {
        var document = SharedBusinessDocuments.Read(item);
        object data = item.Kind == "team-dataset" ? new { SharedBusinessDocuments.Data<SharedBusinessDataset>(item).Rows, SharedBusinessDocuments.Data<SharedBusinessDataset>(item).Columns,
            SharedBusinessDocuments.Data<SharedBusinessDataset>(item).ObservedAt, SharedBusinessDocuments.Data<SharedBusinessDataset>(item).Sha256 } : document.Data;
        return new { item.Author, item.UpdatedAt, item.Deleted, data };
    }
    private async Task<object?> ResolveSharedBusinessConflictAsync(JsonElement a, CancellationToken ct)
    {
        var item = await TeamWorkspace.GetAsync(Text(a, "id", 32), ct).ConfigureAwait(false);
        var conflict = await TeamWorkspace.GetConflictAsync(Text(a, "id", 32), Text(a, "conflictId", 64), ct).ConfigureAwait(false);
        if (item is null || !SharedBusinessDocuments.IsKind(item.Kind) || conflict is null || !a.TryGetProperty("expectedRevision", out var revision) || revision.GetInt64() != item.Revision)
            throw new InvalidOperationException("La version commune a changé. Relisez les deux propositions avant de choisir.");
        EnsureSharedBusinessLocalUnchanged(item, conflict);
        var choice = Text(a, "choice", 20); if (choice is not ("current" or "incoming")) throw new InvalidDataException("Choisissez la version actuelle ou la proposition reçue.");
        var next = choice == "incoming" ? conflict.Incoming.Copy() : item.Copy();
        var envelope = SharedBusinessDocuments.Read(next);
        envelope.AcknowledgedConflicts = envelope.AcknowledgedConflicts.Concat(SharedBusinessDocuments.Read(item).AcknowledgedConflicts).Append(conflict.Id).Distinct(StringComparer.Ordinal).TakeLast(1000).ToList();
        next.Payload = JsonSerializer.SerializeToElement(envelope, SharedBusinessDocuments.Json); next.Author = LibrarySettings.DisplayName;
        var saved = await TeamWorkspace.SaveAsync(next, item.Revision, ct).ConfigureAwait(false);
        var followResolved = false;
        if (saved.Kind == "team-dataset")
        {
            followResolved = ReadBusinessLocalState().FollowedDatasetId == saved.Id;
            if (!followResolved && TerrainNativeSource() is { } source && !IsHistorical)
                followResolved = SharedBusinessDocuments.Dataset(source, EffectiveSettings.Dashboard).SourceKey == envelope.Key;
            if (!saved.Deleted && followResolved) LoadSharedDataset(saved);
        }
        else ApplyBusinessItem(saved);
        var state = ReadBusinessLocalState(); if (followResolved) state.FollowedDatasetId = saved.Id; state.Entries[saved.Id] = new() { Kind = saved.Kind, Key = envelope.Key,
            Hash = saved.Deleted ? "deleted" : SharedBusinessDocuments.Hash(envelope.Data), Revision = saved.Revision, CentralRevision = saved.CentralRevision }; WriteBusinessLocalState(state);
        Publish("library.changed", new { shared = true }); Publish("calendar.changed", new { shared = true });
        return new { saved = true, pending = true, message = "Version retenue sur ce poste. La prochaine synchronisation transmettra ce choix à l’équipe." };
    }
    private void EnsureSharedBusinessLocalUnchanged(TeamWorkspaceItem item, TeamWorkspaceConflict conflict)
    {
        var envelope = SharedBusinessDocuments.Read(item); var state = ReadBusinessLocalState();
        var baseline = state.Entries.GetValueOrDefault(item.Id)?.Hash ?? (item.Deleted ? "deleted" : SharedBusinessDocuments.Hash(envelope.Data));
        string actual;
        if (item.Kind == "team-dataset")
        {
            if (TerrainNativeSource() is not { } source || IsHistorical) return;
            var dataset = SharedBusinessDocuments.Dataset(source, EffectiveSettings.Dashboard);
            if (dataset.SourceKey != envelope.Key) return;
            actual = SharedBusinessDocuments.Hash(JsonSerializer.SerializeToElement(dataset, SharedBusinessDocuments.Json));
            // A refused local observation deliberately remains open. It is safe to
            // review that already archived proposal, but not a newer unstaged edit.
            if (actual == SharedBusinessDocuments.Hash(SharedBusinessDocuments.Read(conflict.Incoming).Data)) return;
        }
        else
        {
            var local = LocalBusinessItems().FirstOrDefault(x => x.Id == item.Id);
            actual = local is null ? "deleted" : SharedBusinessDocuments.Hash(SharedBusinessDocuments.Read(local).Data);
        }
        if (actual != baseline)
            throw new InvalidOperationException("Les données de ce poste ont été modifiées depuis la comparaison. Synchronisez ces modifications, puis relisez les versions avant de choisir.");
    }
    private async Task<object?> ExportSharedBusinessConflictAsync(JsonElement a, CancellationToken ct)
    {
        var item = await TeamWorkspace.GetAsync(Text(a, "id", 32), ct).ConfigureAwait(false);
        var conflict = await TeamWorkspace.GetConflictAsync(Text(a, "id", 32), Text(a, "conflictId", 64), ct).ConfigureAwait(false);
        if (item is null || !SharedBusinessDocuments.IsKind(item.Kind) || conflict is null || !a.TryGetProperty("expectedRevision", out var revision) || revision.GetInt64() != item.Revision)
            throw new InvalidOperationException("Les versions ont changé. Actualisez leur comparaison avant de l’exporter.");
        var path = await Host.SaveFileAsync("Exporter les versions communes", "Versions JSON|*.json", "GLPI-Pilot-versions-communes.json").ConfigureAwait(false);
        if (path is null) return new { cancelled = true };
        var full = Path.GetFullPath(path); var database = LibrarySettings.DatabasePath; var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (new[] { database, database + "-wal", database + "-shm", database + "-journal" }.Any(x => string.Equals(full, x, comparison)))
            throw new InvalidDataException("L’export ne peut pas remplacer la base active ni ses journaux.");
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(new { schema = "glpi-pilot.shared-business-conflict", current = item, incoming = conflict.Incoming }, Json), ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested(); File.Move(temporary, full, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new { cancelled = false, path = full };
    }
}
