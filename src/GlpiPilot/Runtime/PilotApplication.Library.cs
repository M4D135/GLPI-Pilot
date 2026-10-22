using System.Globalization;
using System.IO;
using System.Text.Json;
using GlpiPilot.Core;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace GlpiPilot.Runtime;

public sealed partial class PilotApplication
{
    private readonly LibraryPreferencesStore _libraryPreferencesStore = new();
    private LibraryPreferences? _libraryPreferences;
    private string _libraryPassword = "";
    private string _libraryPasswordBinding = "";
    private DateTimeOffset? _nextLibrarySync;
    private string _librarySyncState = "idle";
    private string _librarySyncLabel = "Prêt";
    private string _librarySyncError = "";
    private TicketLibraryStore? _ticketLibrary;
    private TeamWorkspaceStore? _teamWorkspace;

    private LibraryPreferences LibrarySettings => _libraryPreferences ??= _libraryPreferencesStore.Load();
    private TicketLibraryStore TicketLibrary => _ticketLibrary ??= new(LibrarySettings.DatabasePath);
    private TeamWorkspaceStore TeamWorkspace => _teamWorkspace ??= new(LibrarySettings.DatabasePath);

    private void RegisterLibraryOperations()
    {
        Register("library.get", (_, ct) => LibraryStateAsync(ct));
        Register("library.search", async (a, ct) => await TicketLibrary.SearchAsync(new()
        {
            Text = Text(a, "text", 2000), Installation = Text(a, "installation", 3000),
            ResolvedOnly = Flag(a, "resolvedOnly"), Offset = Number(a, "offset", 0, 0, 1_000_000), Limit = Number(a, "limit", 25, 1, 100)
        }, ct).ConfigureAwait(false));
        Register("library.ticket", async (a, ct) => await RequireLibraryTicketAsync(a, ct).ConfigureAwait(false));
        Register("library.open", async (a, ct) =>
        {
            var document = await RequireLibraryTicketAsync(a, ct).ConfigureAwait(false);
            _ = TicketLibraryRules.Key(document.Seed.Ticket);
            await Host.OpenExternalAsync(document.Seed.Ticket.Url).ConfigureAwait(false); return new { opened = true };
        });
        Register("library.conflicts", LibraryConflictsAsync);
        Register("library.capture", CaptureLibraryAsync);
        Register("library.sync", (_, ct) => SynchronizeLibraryAsync(ct));
        Register("library.tick", AutomaticLibrarySyncAsync);
        Register("library.export", async (_, ct) =>
        {
            var path = await Host.SaveFileAsync("Exporter la bibliothèque de tickets", "Corpus JSONL|*.jsonl", "GLPI-Pilot-corpus-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl").ConfigureAwait(false);
            if (path is null) return new { cancelled = true };
            await TicketLibrary.ExportAsync(path, ct).ConfigureAwait(false); return new { path, cancelled = false };
        });
        Register("library.folder.pick", async (_, _) =>
        {
            var folder = await Host.PickFolderAsync("Choisir le dossier local de la bibliothèque").ConfigureAwait(false);
            return new { folder = folder is null ? null : LibraryPreferencesStore.ValidateLocalFolder(folder), cancelled = folder is null };
        });
        Register("library.settings.save", SaveLibrarySettingsAsync);
        Register("library.connection.test", TestLibraryConnectionAsync);
        Register("library.loadRecords", LoadLibraryRecordsAsync);
        RegisterSharedWorkspaceOperations();
    }

    private async Task<object?> LibraryStateAsync(CancellationToken ct)
    {
        var local = TicketLibrary; var stats = await local.GetStateAsync(ct).ConfigureAwait(false);
        var sources = await local.ReadSourcesAsync(ct).ConfigureAwait(false);
        var team = await TeamWorkspace.CountsAsync(ct).ConfigureAwait(false);
        return new
        {
            settings = PublicLibrarySettings(LibrarySettings), stats, team,
            sources = sources.Select(source => new { installation = source, label = source }),
            sync = new { state = _librarySyncState, label = _librarySyncLabel, lastError = _librarySyncError }
        };
    }
    private object PublicLibrarySettings(LibraryPreferences settings) => new
    {
        settings.LocalFolder, settings.PostgresEnabled, settings.Host, settings.Port, settings.Database,
        settings.Username, settings.TlsMode, settings.AutoSync, settings.IntervalMinutes, settings.SourceTimeZone,
        settings.SourceDateOrder, settings.MaxAgeDays, settings.DisplayName,
        hasPassword = settings.ProtectedPassword.Length > 0 || _libraryPassword.Length > 0 && _libraryPasswordBinding == settings.ConnectionBinding()
    };
    private LibraryPreferences ReadLibrarySettings(JsonElement args)
    {
        if (!args.TryGetProperty("settings", out var input) || input.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Réglages de bibliothèque manquants.");
        // Only public settings may enter from the WebView. Protected credentials stay in C#.
        var value = new LibraryPreferences
        {
            LocalFolder = Text(input, "localFolder", 3000), PostgresEnabled = Flag(input, "postgresEnabled"),
            Host = Text(input, "host", 500).Trim(), Port = Number(input, "port", 5432, 1, 65535),
            Database = Text(input, "database", 200).Trim(), Username = Text(input, "username", 200).Trim(),
            TlsMode = Text(input, "tlsMode", 30), AutoSync = Flag(input, "autoSync", true),
            IntervalMinutes = Number(input, "intervalMinutes", 5, 1, 1440), SourceTimeZone = Text(input, "sourceTimeZone", 200).Trim(), SourceDateOrder = Text(input, "sourceDateOrder", 10),
            MaxAgeDays = Number(input, "maxAgeDays", 7, 1, 365), DisplayName = Text(input, "displayName", 120).Trim()
        };
        if (value.TlsMode.Length == 0) value.TlsMode = "VerifyFull";
        if (value.Database.Length == 0) value.Database = "glpi_pilot";
        if (value.DisplayName.Length == 0) value.DisplayName = "Technicien";
        value.Validate(); return value;
    }
    private string LibraryConnectionPassword(LibraryPreferences settings, string supplied = "")
    {
        if (supplied.Length > 0) return supplied;
        if (_libraryPasswordBinding == settings.ConnectionBinding()) return _libraryPassword;
        if (LibrarySettings.ConnectionBinding() == settings.ConnectionBinding()) return LibraryPreferencesStore.ReadPassword(LibrarySettings);
        return "";
    }
    private async Task<object?> SaveLibrarySettingsAsync(JsonElement args, CancellationToken ct)
    {
        var previous = LibrarySettings; var next = ReadLibrarySettings(args);
        var password = Text(args, "password", 8000); var forget = Flag(args, "forgetPassword");
        if (!forget && next.ConnectionBinding() == previous.ConnectionBinding())
        { next.ProtectedPassword = previous.ProtectedPassword; next.PasswordBinding = previous.PasswordBinding; }
        if (!forget && password.Length > 0)
        {
            next.ProtectedPassword = ""; next.PasswordBinding = "";
            if (Flag(args, "rememberPassword")) LibraryPreferencesStore.RememberPassword(next, password);
        }
        var moved = !string.Equals(next.DatabasePath, previous.DatabasePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var copied = moved && await CopyLibraryDatabaseAsync(previous.DatabasePath, next.DatabasePath, ct).ConfigureAwait(false);
        await new TicketLibraryStore(next.DatabasePath).InitializeAsync(ct).ConfigureAwait(false);
        await new TeamWorkspaceStore(next.DatabasePath).InitializeAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); _libraryPreferencesStore.Save(next); _libraryPreferences = next; _nextLibrarySync = null;
        if (moved) { _ticketLibrary = null; _teamWorkspace = null; }
        if (forget || next.ConnectionBinding() != _libraryPasswordBinding) { _libraryPassword = ""; _libraryPasswordBinding = ""; }
        if (!forget && password.Length > 0) { _libraryPassword = password; _libraryPasswordBinding = next.ConnectionBinding(); }
        Publish("library.changed", new { settings = PublicLibrarySettings(next) });
        return new { settings = PublicLibrarySettings(next), copied, message = copied ? "Bibliothèque copiée dans le nouveau dossier. La copie précédente est conservée." : "Réglages de bibliothèque enregistrés." };
    }
    private static Task<bool> CopyLibraryDatabaseAsync(string source, string destination, CancellationToken ct) => Task.Run(() =>
    {
        if (!File.Exists(source) || File.Exists(destination)) return false;
        TicketLibraryStore.ValidateDatabasePath(source); TicketLibraryStore.ValidateDatabasePath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            using (var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Pooling = false }.ToString()))
            { ct.ThrowIfCancellationRequested(); from.Open(); to.Open(); from.BackupDatabase(to); }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, destination, false); return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, ct);
    private async Task<object?> TestLibraryConnectionAsync(JsonElement args, CancellationToken ct)
    {
        var settings = ReadLibrarySettings(args); settings.PostgresEnabled = true; settings.Validate();
        var password = LibraryConnectionPassword(settings, Text(args, "password", 8000));
        try
        {
            await using var connection = new NpgsqlConnection(settings.ConnectionString(password));
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var query = new NpgsqlCommand("SELECT 1", connection);
            await query.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return new { success = true, message = "Connexion PostgreSQL établie. Les schémas de partage seront préparés lors de la synchronisation." };
        }
        catch (Exception error) when (error is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException)
        { return new { success = false, message = "Connexion PostgreSQL indisponible. Vérifiez le serveur, le port, les identifiants, le certificat TLS et les droits de connexion." }; }
    }
    private async Task<TicketLibraryDocument> RequireLibraryTicketAsync(JsonElement args, CancellationToken ct)
        => await TicketLibrary.GetAsync(Text(args, "key", 200), ct).ConfigureAwait(false) ?? throw new InvalidDataException("Ticket absent de la bibliothèque locale.");

    private async Task<object?> LoadLibraryRecordsAsync(JsonElement args, CancellationToken ct)
    {
        var installation = Text(args, "installation", 3000);
        if (installation.Length == 0 && (await TicketLibrary.ReadSourcesAsync(ct).ConfigureAwait(false)).Count > 1)
            throw new InvalidDataException("Choisissez une installation dans le filtre de bibliothèque avant de charger le relevé. Les identifiants de portails différents doivent garder leur contexte.");
        var seeds = await TicketLibrary.ReadSeedsAsync(installation, ct).ConfigureAwait(false);
        var baseHeaders = new[] { "ID", "Titre", "Statut", "Catégorie", "Dernière modification" };
        var headers = baseHeaders.ToList(); var headerPositions = headers.Select((header, index) => (header, index)).ToDictionary(pair => pair.header, pair => pair.index, StringComparer.Ordinal);
        var schemas = new Dictionary<string, (int Input, int Output)[]>(StringComparer.Ordinal);
        foreach (var seed in seeds)
        {
            ct.ThrowIfCancellationRequested(); var signature = JsonSerializer.Serialize(seed.IndexHeaders, Json);
            if (schemas.ContainsKey(signature)) continue;
            var core = DashboardService.ResolveColumns(seed.IndexHeaders, new DashboardSettings())
                .Where(column => column.Role is "id" or "title" or "status" or "category" or "updated").Where(column => column.Index.HasValue).Select(column => column.Index!.Value).ToHashSet();
            var projection = new List<(int, int)>(); var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < seed.IndexHeaders.Count; i++)
            {
                if (core.Contains(i)) continue;
                var original = seed.IndexHeaders[i]; var name = original.Length == 0 ? "Colonne source " + (i + 1) : original;
                var occurrence = occurrences.GetValueOrDefault(name) + 1; occurrences[name] = occurrence;
                if (occurrence > 1) name += " (" + occurrence + ")";
                if (baseHeaders.Contains(name, StringComparer.Ordinal)) name = "Source · " + name;
                if (!headerPositions.TryGetValue(name, out var target))
                {
                    if (headers.Count >= 512) throw new InvalidDataException("Les schémas de la bibliothèque dépassent 512 colonnes réunies. Choisissez une installation.");
                    target = headers.Count; headerPositions.Add(name, target); headers.Add(name);
                }
                projection.Add((i, target));
            }
            schemas.Add(signature, projection.ToArray());
        }
        var data = new ExtractionResult
        {
            Title = "Bibliothèque locale de tickets", Scope = "Tickets présents dans le cache local · observations de dates différentes",
            Headers = headers,
            Complete = false, ExpectedTotal = seeds.Count,
            SourceUrl = seeds.Count == 0 ? "" : ObjectIdentity.Installation(seeds[0].Ticket.Url) + "/front/ticket.php",
            Provenance = new() { SourceKind = "local", Method = "ticket-library" }
        };
        foreach (var seed in seeds)
        {
            ct.ThrowIfCancellationRequested();
            var row = Enumerable.Repeat("", headers.Count).ToArray();
            row[0] = seed.Ticket.Id; row[1] = seed.Title; row[2] = seed.Status; row[3] = seed.Category; row[4] = seed.LastModifiedRaw;
            foreach (var column in schemas[JsonSerializer.Serialize(seed.IndexHeaders, Json)])
                if (column.Input < seed.IndexValues.Count) row[column.Output] = seed.IndexValues[column.Input];
            data.Rows.Add(row);
        }
        data.Status = $"Bibliothèque locale · {data.Rows.Count:N0} tickets";
        data.Warnings.Add("Ce relevé rassemble les observations conservées localement ; il ne certifie pas l’état exhaustif actuel de GLPI.");
        var mapping = Settings.Dashboard.Copy();
        foreach (var pair in new[] { ("id", "ID"), ("title", "Titre"), ("status", "Statut"), ("category", "Catégorie"), ("updated", "Dernière modification") }) mapping.ColumnMappings[pair.Item1] = pair.Item2;
        _localMappings.Set(data, mapping); SetResult(data); return new { session = SessionState(), rows = data.Rows.Count };
    }

    private async Task<object?> LibraryConflictsAsync(JsonElement args, CancellationToken ct)
    {
        var conflicts = await TicketLibrary.GetConflictsAsync(Text(args, "key", 200), 3, ct).ConfigureAwait(false);
        return conflicts.Select(conflict => new { conflict.Id, conflict.Reason, conflict.RecordedAt, current = LibraryConflictPreview(conflict.Current), incoming = LibraryConflictPreview(conflict.Incoming) }).ToArray();
    }
    private static object LibraryConflictPreview(TicketLibraryDocument document)
    {
        var budget = 200000; var sections = new List<object>();
        foreach (var section in document.Detail?.Sections ?? [])
        {
            var rows = new List<string[]>();
            foreach (var row in section.Data.Rows)
            {
                if (budget <= 0) break;
                var text = string.Join("\n", row); var shown = text.Length <= budget ? text : text[..budget] + "\n[Suite conservée dans la base ; aperçu limité.]";
                budget -= shown.Length; rows.Add([shown]);
            }
            sections.Add(new { section.Name, data = new { rows } }); if (budget <= 0) break;
        }
        return new { seed = new { document.Seed.Ticket, document.Seed.Title, document.Seed.Status, document.Seed.Category, document.Seed.LastModifiedRaw, document.Seed.LastModifiedAt }, document.ReadAt, detail = new { sections } };
    }
}
