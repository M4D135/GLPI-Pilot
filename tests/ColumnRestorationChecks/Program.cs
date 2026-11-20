using System.Text.Json;
using System.Text.Json.Nodes;
using GlpiPilot.Core;

var checks = new (string Name, Action Run)[]
{
    ("Budget de récupération indépendant du délai de lecture", CheckCleanupBudget),
    ("Restauration progressive : dix étapes vérifiées dépassent le budget initial sans expiration", ColumnCleanupWindowChecks.Run),
    ("Reçu natif complet conservé malgré une restauration de préférences en attente", CheckPendingRestorationReceipt),
    ("Capture indépendante et vue personnelle initialement inactive", CheckCapture),
    ("Ordre, identité native et libellés conservés exactement", CheckExactOrder),
    ("Ajout moderne : seul l’identifiant réellement demandé est attribuable", CheckModernAddition),
    ("Ajout legacy : ID de préférence réel et option consommée", CheckLegacyAddition),
    ("Ajout concurrent ou ambigu : aucune colonne attribuée à la collecte", CheckAmbiguousAddition),
    ("Suppression : toutes les colonnes étrangères et leur ordre sont conservés", CheckRemovedOnly),
    ("Propriété d’une colonne : identité et schéma complet inchangés", CheckOwnership),
    ("Désactivation : uniquement une vue créée par la collecte et restée identique", CheckDisable),
    ("Activation complète : une copie personnelle identifiée peut réexposer Désactiver", CheckActivationCopy),
    ("Copie d’activation : aucun retrait si vue existante, conflit ou commande absente", CheckActivationCopyRefusals),
    ("Dernière préférence retirée : désactivation implicite uniquement sur autorisation", CheckImplicitDeactivation),
    ("Journal durable : nouvelle instance et intention d’ajout conservée", CheckReload),
    ("Journal isolé par installation et profil navigateur, indépendant des filtres", CheckIsolation),
    ("Journal en attente : aucun écrasement par une autre session", CheckNoOverwrite),
    ("Restauration terminée : état durable sans commande à rejouer", CheckComplete),
    ("Adresse native : origine, type Ticket et absence de paramètres privés", CheckNativeAddress),
    ("Adresse modale : seul indicateur natif facultatif _in_modal=1 autorisé", CheckNativeModalAddress),
    ("Adresse modale : paramètres inconnus, doublons et variantes ambiguës refusés", CheckNativeModalAddressRefusals),
    ("Journal modal : adresse observée conservée exactement après redémarrage", CheckNativeModalJournal),
    ("Journal corrompu : refus explicite et contenu conservé", CheckMalformedJournal),
    ("Commande de suppression : seules les additions journalisées sont autorisées", CheckInvalidCommand),
    ("Échec de création : aucune trace partielle ni fausse session", CheckCreateFailure),
    ("Échec de sauvegarde : la dernière intention durable reste intacte", CheckSaveFailure),
    ("Cadre initial : URL en attente observée sans accepter origine, parent ou page étrangers", BrowserColumnFrameChecks.Run)
};
var failures = 0;
foreach (var (name, run) in checks)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + "\n" + error); }
}
Console.WriteLine($"{checks.Length - failures}/{checks.Length} vérifications pures de restauration des colonnes réussies.");
return failures == 0 ? 0 : 1;

static void Require(bool value, string message)
{ if (!value) throw new InvalidOperationException(message); }

static void Throws<T>(Action run, string message) where T : Exception
{
    try { run(); }
    catch (T) { return; }
    throw new InvalidOperationException(message);
}

static TemporaryColumnView Clone(TemporaryColumnView view) => JsonSerializer.Deserialize<TemporaryColumnView>(JsonSerializer.Serialize(view))!;

static NativeColumnPreferences Preferences(bool modern = false) => new()
{
    Supported = true, PersonalScope = true, PersonalActive = true, ItemType = "Ticket", UsersId = "17",
    Layout = modern ? "modern" : "legacy", SelectedIdsKnown = modern,
    Selected = new()
    {
        new() { Value = modern ? "1" : "", Label = "Titre", Fixed = true },
        new() { Value = modern ? "2" : "", PreferenceId = modern ? "" : "700", Label = "ID" },
        new() { Value = modern ? "12" : "", PreferenceId = modern ? "" : "701", Label = "Statut" }
    },
    Options = new() { new() { Value = "3", Label = "Priorité", Group = "Caractéristiques" } }
};

static TemporaryColumnCommand AddCommand(NativeColumnPreferences before) => new()
{
    Kind = "add", OptionValue = "3", OptionLabel = "Priorité", OptionHeader = "Caractéristiques - Priorité",
    Before = TemporaryColumnPolicy.Capture(before)
};

static NativeSelectedColumn Added(bool modern = false) => new()
{ Value = modern ? "3" : "", PreferenceId = modern ? "" : "702", Label = "Priorité" };

static AppSettings Settings(string url = "https://glpi.test/glpi/front/ticket.php?criteria%5B0%5D%5Bvalue%5D=private-filter") => new()
{ PortalUrl = url, BrowserChannel = BrowserChannels.Edge };

static TemporaryColumnJournalRecord Record(AppSettings settings)
{
    var original = TemporaryColumnPolicy.Capture(Preferences());
    return new()
    {
        InstallationKey = ObjectIdentity.InstallationKey(settings.PortalUrl), BrowserChannel = settings.BrowserChannel,
        Origin = "https://glpi.test", PreferencesUrl = "https://glpi.test/glpi/front/displaypreference.form.php?itemtype=Ticket",
        UsersId = "17", Layout = "legacy", SourceScopeFingerprint = new string('a', 64), ContextFingerprint = new string('b', 64),
        ContextObserved = true, Original = original, Expected = Clone(original), PendingCommand = AddCommand(Preferences())
    };
}

static void CheckCleanupBudget()
{
    Require(BrowserService.ColumnCleanupBudgetSeconds(45) == 180, "Le délai normal de liste ne doit pas interrompre dix retraits natifs.");
    Require(BrowserService.ColumnCleanupBudgetSeconds(300) == 300, "Un délai de liste supérieur au plancher doit rester disponible.");
    Require(BrowserService.ColumnCleanupBudgetSeconds(999) == 600, "La récupération doit rester bornée.");
}

static void CheckPendingRestorationReceipt()
{
    var data = new ExtractionResult
    {
        Complete = true, Headers = ["ID", "Titre"], Rows = [new[] { "7", "Exemple" }], ExpectedTotal = 1,
        Quality = new()
        {
            AllPagesRequested = true, RowCountVerified = true, FinalTotalVerified = true, ScopeStable = true,
            InitialSample = new() { IdentityVerified = true, RepresentationVerified = true },
            FinalSample = new() { IdentityVerified = true, RepresentationVerified = true }
        },
        Provenance = new() { Method = "native-csv", ScopeVerified = true,
            ColumnPreparation = new() { AddedCount = 10, ColumnCount = 14 } }
    };
    var quality = JsonSerializer.Serialize(data.Quality);
    var pending = new ColumnRestorationResult("recovery-pending", "Six retraits vérifiés, récupération restante en attente.", true, 6);
    ColumnRestorationReceipt.Apply(data, pending);
    ColumnRestorationReceipt.Apply(data, pending);
    Require(data.Complete && data.Rows.Count == 1 && data.Rows[0][1] == "Exemple"
        && JsonSerializer.Serialize(data.Quality) == quality && data.Provenance.ScopeVerified,
        "A pending preference restoration cannot invalidate or alter an independently verified native receipt.");
    Require(data.Provenance.ColumnPreparation is { RecoveryPending: true, TemporaryView: true, RestoredCount: 6 }
        && data.Warnings.Count == 1 && data.Provenance.ColumnPreparation.Warnings.Count == 1,
        "The incomplete preference cleanup must remain explicit and retain its verified progress without duplicate warnings.");
}

static void CheckCapture()
{
    var live = Preferences();
    var snapshot = TemporaryColumnPolicy.Capture(live);
    live.Selected[1].Label = "Changed"; live.Selected.Clear();
    Require(snapshot.Columns.Select(column => column.Label).SequenceEqual(new[] { "Titre", "ID", "Statut" }),
        "A captured rollback view must not alias the live inspector model.");
    live.PersonalActive = false; live.Selected.Add(Added());
    var inactive = TemporaryColumnPolicy.Capture(live);
    Require(!inactive.PersonalActive && inactive.Columns.Count == 0, "An inactive view must not mistake visible global defaults for personal rows.");
}

static void CheckExactOrder()
{
    var original = TemporaryColumnPolicy.Capture(Preferences());
    Require(TemporaryColumnPolicy.Same(original, Clone(original)), "An exact re-read must match the original view.");
    var reordered = Clone(original); (reordered.Columns[1], reordered.Columns[2]) = (reordered.Columns[2], reordered.Columns[1]);
    Require(!TemporaryColumnPolicy.Same(original, reordered), "Identical sets in a different order must remain a conflict.");
    var relabelled = Clone(original); relabelled.Columns[1].Label = "Identifiant";
    Require(!TemporaryColumnPolicy.Same(original, relabelled), "A renamed row must not be silently certified as unchanged.");
    var replaced = Clone(original); replaced.Columns[1].PreferenceId = "900";
    Require(!TemporaryColumnPolicy.Same(original, replaced), "A same-label replacement with another native preference ID must remain distinct.");
}

static void CheckModernAddition()
{
    var live = Preferences(modern: true); var command = AddCommand(live);
    live.Selected.Add(Added(modern: true));
    Require(TemporaryColumnPolicy.ConfirmAdded(command, live)?.Identity == "option:3", "The requested modern option appended to an unchanged prefix must be confirmed.");
    live.Selected[^1].Value = "900";
    Require(TemporaryColumnPolicy.ConfirmAdded(command, live) is null, "A same-label foreign option must not become owned by this preparation.");
}

static void CheckLegacyAddition()
{
    var live = Preferences(); var command = AddCommand(live);
    live.Options.Clear(); live.Selected.Add(Added());
    Require(TemporaryColumnPolicy.ConfirmAdded(command, live)?.Identity == "preference:702", "Legacy ownership must retain the real generated preference ID, without inventing a num field.");
    live.Options.Add(new() { Value = "3", Label = "Priorité" });
    Require(TemporaryColumnPolicy.ConfirmAdded(command, live) is null, "A still-offered legacy option cannot prove that an unrelated appended row is the requested addition.");
    live.Options.Clear(); live.Selected[^1].PreferenceId = "";
    Require(TemporaryColumnPolicy.ConfirmAdded(command, live) is null, "A label alone is never enough to identify a removable legacy row.");
}

static void CheckAmbiguousAddition()
{
    var live = Preferences(); var command = AddCommand(live); live.Options.Clear();
    live.Selected.Add(Added()); live.Selected.Add(new() { PreferenceId = "703", Label = "Priorité" });
    Require(TemporaryColumnPolicy.ConfirmAdded(command, live) is null, "Two simultaneous additions must not be assigned arbitrarily.");
    live.Selected.RemoveAt(live.Selected.Count - 1); live.Selected[1].PreferenceId = "900";
    Require(TemporaryColumnPolicy.ConfirmAdded(command, live) is null, "An altered initial prefix must block attribution even when the requested label appears last.");
}

static void CheckRemovedOnly()
{
    var before = TemporaryColumnPolicy.Capture(Preferences()); before.Columns.Add(TemporaryColumnPolicy.Copy(Added()));
    before.Columns.Add(new() { PreferenceId = "990", Label = "Choix utilisateur concurrent" });
    var after = Clone(before); after.Columns.RemoveAt(3);
    Require(TemporaryColumnPolicy.RemovedOnly(before, after, "preference:702"), "Removing the owned row must preserve every other row, including a concurrent user addition.");
    after.Columns.RemoveAt(after.Columns.Count - 1);
    Require(!TemporaryColumnPolicy.RemovedOnly(before, after, "preference:702"), "Removing the user's concurrent addition as well must fail verification.");
    after = Clone(before); after.Columns.RemoveAt(3); after.Columns.Reverse();
    Require(!TemporaryColumnPolicy.RemovedOnly(before, after, "preference:702"), "A native removal must not silently reorder the remainder.");
    var record = Record(Settings()); record.Expected = Clone(before); record.Added.Add(TemporaryColumnPolicy.Copy(Added()));
    var removed = Clone(before); removed.Columns.RemoveAt(3);
    Require(TemporaryColumnPolicy.OnlyOwnEntriesMissing(record, removed), "A temporary row already removed elsewhere must need no cleanup replay.");
    removed.Columns.RemoveAt(1);
    Require(!TemporaryColumnPolicy.OnlyOwnEntriesMissing(record, removed), "The disappearance of an initial column must not be mistaken for completed owned cleanup.");
}

static void CheckOwnership()
{
    var record = Record(Settings()); var added = TemporaryColumnPolicy.Copy(Added()); record.Added.Add(added);
    Require(TemporaryColumnPolicy.OwnEntryUnchanged(record, TemporaryColumnPolicy.Copy(Added())), "The original owned row must remain eligible for cleanup.");
    Require(!TemporaryColumnPolicy.OwnEntryUnchanged(record, record.Original.Columns[1]), "An initial personal column must never be classified as temporary.");
    var changed = TemporaryColumnPolicy.Copy(Added()); changed.Label = "Choix modifié";
    Require(!TemporaryColumnPolicy.OwnEntryUnchanged(record, changed), "An owned ID reused or relabelled by the user must be protected.");
    Require(!TemporaryColumnPolicy.OwnEntryUnchanged(record, new() { PreferenceId = "990", Label = "Priorité" }), "A same-label foreign row must remain untouched.");
}

static void CheckDisable()
{
    var record = Record(Settings()); record.Original = new(); record.ActivationBaseline = Clone(record.Expected); record.PendingCommand = null;
    Require(TemporaryColumnPolicy.CanDisable(record, Clone(record.Expected)), "A personal view created by this collection can be deactivated after an exact fresh re-read.");
    var changed = Clone(record.Expected); changed.Columns.Add(new() { PreferenceId = "990", Label = "Choix utilisateur" });
    Require(!TemporaryColumnPolicy.CanDisable(record, changed), "Deactivation must preserve a concurrent user choice by refusing to clear the view.");
    record.PendingCommand = new() { Kind = "remove", Before = Clone(record.Expected) };
    Require(!TemporaryColumnPolicy.CanDisable(record, Clone(record.Expected)), "An unresolved native command must prevent deactivation.");
    record.PendingCommand = null; record.Original.PersonalActive = true;
    Require(!TemporaryColumnPolicy.CanDisable(record, Clone(record.Expected)), "A previously active personal view must never be deactivated.");
}

static (TemporaryColumnJournalRecord Record, NativeColumnPreferences Preferences) CompleteActivation()
{
    var preferences = Preferences(modern: true); preferences.Options.Clear();
    foreach (var column in preferences.Selected.Where(column => !column.Fixed)) column.RemoveSelector = "#remove_" + column.Value;
    var record = Record(Settings()); record.Original = new(); record.PendingCommand = null;
    record.Expected = TemporaryColumnPolicy.Capture(preferences); record.ActivationBaseline = Clone(record.Expected);
    return (record, preferences);
}

static void CheckActivationCopy()
{
    var (record, preferences) = CompleteActivation();
    var before = TemporaryColumnPolicy.Capture(preferences);
    var copy = TemporaryColumnPolicy.ActivationCopyForDisable(record, preferences);
    Require(copy is not null && !copy.Fixed && record.ActivationBaseline!.Columns.Any(column => TemporaryColumnPolicy.SameEntry(column, copy))
        && preferences.Selected.Any(column => column.Value == copy.Value && column.RemoveSelector.Length > 0),
        "Only one removable personal row from our exact confirmed activation copy may be proposed to reveal native disable.");
    Require(TemporaryColumnPolicy.Same(before, TemporaryColumnPolicy.Capture(preferences)) && record.Added.Count == 0,
        "Planning the inverse must not mutate the live schema or silently mark a command as executed.");
}

static void CheckActivationCopyRefusals()
{
    foreach (var conflict in new[] { "preexisting", "concurrent", "pending", "disable-present", "no-remove", "no-baseline" })
    {
        var (record, preferences) = CompleteActivation();
        if (conflict == "preexisting") record.Original.PersonalActive = true;
        else if (conflict == "concurrent") preferences.Selected.Reverse();
        else if (conflict == "pending") record.PendingCommand = new() { Kind = "remove", Before = Clone(record.Expected) };
        else if (conflict == "disable-present") preferences.DisableSelector = "#native_disable";
        else if (conflict == "no-remove") foreach (var column in preferences.Selected) column.RemoveSelector = "";
        else record.ActivationBaseline = null;
        Require(TemporaryColumnPolicy.ActivationCopyForDisable(record, preferences) is null,
            "A fallback removal must not be proposed for an unowned, changed or unverifiable activation: " + conflict);
    }
}

static void CheckImplicitDeactivation()
{
    var before = new TemporaryColumnView { PersonalActive = true, Columns = new()
    { new() { Value = "1", Label = "Titre", Fixed = true }, new() { Value = "3", Label = "Priorité" } } };
    var inactive = new TemporaryColumnView();
    Require(!TemporaryColumnPolicy.RemovedOnly(before, inactive, "option:3"),
        "Removing a column from a pre-existing personal view must not silently certify disappearance of the entire view.");
    Require(TemporaryColumnPolicy.RemovedOnly(before, inactive, "option:3", allowImplicitDeactivation: true),
        "Removing the last non-fixed preference of our own temporary activation may legitimately reveal the global view.");
    before.Columns.Add(new() { Value = "12", Label = "Statut" });
    Require(!TemporaryColumnPolicy.RemovedOnly(before, inactive, "option:3", allowImplicitDeactivation: true),
        "Implicit deactivation must never hide removal of another personal preference.");
}

static void CheckReload()
{
    using var fixture = new Storage(); var settings = Settings(); var record = Record(settings);
    fixture.Store.Create(settings, record);
    var fresh = new TemporaryColumnJournalStore(fixture.Root).Load(settings)!;
    Require(fresh.SessionId == record.SessionId && TemporaryColumnPolicy.Same(record.Original, fresh.Original)
        && fresh.PendingCommand is { Kind: "add", OptionValue: "3" } && fresh.Added.Count == 0,
        "A process-independent reload must retain the exact pre-command schema and unresolved add intent.");
    var serialized = File.ReadAllText(fixture.Store.PathFor(settings));
    Require(!serialized.Contains("private-filter", StringComparison.Ordinal) && !serialized.Contains("criteria", StringComparison.OrdinalIgnoreCase)
        && Directory.GetFiles(fixture.Root, "*.tmp").Length == 0,
        "The durable journal must contain schema only and no uncommitted temporary write or raw search value.");
}

static void CheckIsolation()
{
    using var fixture = new Storage(); var first = Settings(); var same = Settings("https://glpi.test/glpi/front/ticket.php?sort=2&start=40");
    var otherPath = Settings("https://glpi.test/other/front/ticket.php"); var otherOrigin = Settings("https://other-glpi.test/glpi/front/ticket.php");
    var otherChannel = Settings(); otherChannel.BrowserChannel = BrowserChannels.Chrome;
    Require(fixture.Store.PathFor(first) == fixture.Store.PathFor(same), "A changed search must still find the same pending personal-view restoration.");
    Require(new[] { first, otherPath, otherOrigin, otherChannel }.Select(fixture.Store.PathFor).Distinct(StringComparer.Ordinal).Count() == 4,
        "Different installations, origins and dedicated browser profiles must never share a recovery record.");
}

static void CheckNoOverwrite()
{
    using var fixture = new Storage(); var settings = Settings(); var first = Record(settings); fixture.Store.Create(settings, first);
    var original = File.ReadAllText(fixture.Store.PathFor(settings)); var second = Record(settings);
    Throws<InvalidOperationException>(() => fixture.Store.Create(settings, second), "A second collection must not overwrite a pending restoration.");
    Throws<InvalidOperationException>(() => fixture.Store.Save(settings, second), "Another session must not replace the existing journal.");
    Require(File.ReadAllText(fixture.Store.PathFor(settings)) == original, "Both refusals must preserve the original durable record byte for byte.");
}

static void CheckComplete()
{
    using var fixture = new Storage(); var settings = Settings(); var record = Record(settings); fixture.Store.Create(settings, record);
    record.State = "restored"; record.PendingCommand = null; fixture.Store.Save(settings, record);
    var durable = new TemporaryColumnJournalStore(fixture.Root).Load(settings)!;
    Require(durable.State == "restored" && durable.PendingCommand is null, "Completion must be representable durably before a possibly delayed deletion.");
    var next = Record(settings);
    fixture.Store.Create(settings, next);
    Require(fixture.Store.Load(settings)?.SessionId == next.SessionId && next.SessionId != durable.SessionId,
        "A retained but durably completed file must allow the next collection to create its own independent journal.");
    Throws<InvalidOperationException>(() => fixture.Store.Save(settings, durable), "The old completed session must not overwrite the next collection.");
    Throws<InvalidOperationException>(() => fixture.Store.Complete(settings, durable), "A delayed old cleanup must not delete the next collection's journal.");
    Require(fixture.Store.Load(settings)?.PendingCommand is { Kind: "add" }, "The next session's pending command must survive both stale completion attempts.");
    fixture.Store.Complete(settings, next);
    Require(!fixture.Store.Exists(settings) && fixture.Store.Load(settings) is null, "Completed cleanup must leave no active record to replay.");
    Throws<InvalidOperationException>(() => fixture.Store.Save(settings, record), "An old completed session cannot recreate itself through Save.");
}

static void CheckNativeAddress()
{
    const string origin = "https://glpi.test";
    Require(TemporaryColumnJournalStore.IsObservedPreferencesUrl(origin + "/glpi/front/displaypreference.form.php?itemtype=Ticket", origin),
        "The observed native Ticket preferences GET must remain usable after restart.");
    foreach (var address in new[]
    {
        "https://other-glpi.test/glpi/front/displaypreference.form.php?itemtype=Ticket",
        "https://user:secret@glpi.test/glpi/front/displaypreference.form.php?itemtype=Ticket",
        origin + "/glpi/front/displaypreference.form.php?itemtype=Computer",
        origin + "/glpi/front/displaypreference.form.php?itemtype=Ticket&users_id=0",
        origin + "/glpi/front/displaypreference.form.php?itemtype=Ticket&_glpi_csrf_token=secret",
        origin + "/glpi/front/displaypreference.form.php?itemtype=Ticket#secret",
        origin + "/glpi/front/ticket.form.php?itemtype=Ticket"
    }) Require(!TemporaryColumnJournalStore.IsObservedPreferencesUrl(address, origin), "A foreign, global, private or business endpoint must never be persisted for recovery: " + address);
}

static void CheckNativeModalAddress()
{
    foreach (var origin in new[] { "https://glpi.test", "http://glpi.test:8080" })
    foreach (var query in new[] { "itemtype=Ticket", "itemtype=Ticket&_in_modal=1", "_in_modal=1&itemtype=Ticket" })
        Require(TemporaryColumnJournalStore.IsObservedPreferencesUrl(origin + "/glpi/front/displaypreference.form.php?" + query, origin),
            "The observed Ticket GET must accept only the optional native modal marker, in either parameter order.");
}

static void CheckNativeModalAddressRefusals()
{
    const string origin = "https://glpi.test";
    const string endpoint = origin + "/glpi/front/displaypreference.form.php?";
    foreach (var query in new[]
    {
        "_in_modal=1", "itemtype=Computer&_in_modal=1", "itemtype=ticket&_in_modal=1",
        "itemtype=Ticket&_in_modal=0", "itemtype=Ticket&_in_modal=01", "itemtype=Ticket&_in_modal=true", "itemtype=Ticket&_in_modal=",
        "itemtype=Ticket&itemtype=Ticket&_in_modal=1", "itemtype=Ticket&itemtype=Computer&_in_modal=1",
        "itemtype=Ticket&_in_modal=1&_in_modal=1", "_in_modal=1&itemtype=Ticket&_in_modal=0",
        "itemtype[]=Ticket&_in_modal=1", "itemtype=Ticket&_in_modal[]=1",
        "itemtype=Ticket&_in_modal=1&users_id=0", "itemtype=Ticket&_in_modal=1&_glpi_csrf_token=secret",
        "itemtype=Ticket&_in_modal=1&session=secret", "itemtype=Ticket&_in_modal=1&unknown=1",
        "itemtype=Ticket&_in_modal=1&", "&itemtype=Ticket&_in_modal=1", "itemtype=Ticket&&_in_modal=1",
        "?itemtype=Ticket", "itemtype=Ticket;_in_modal=1", "itemtype=Ticket&_in_modal=1=1",
        "itemtype=Ticket&%5Fin_modal=1", "itemtype=Ticket&_in_modal=%31", "itemtype=%54icket&_in_modal=1",
        "itemtype=Ticket%26_in_modal%3D1", "itemtype=Ticket&_in_modal=1+", "itemtype=Ticket&_in_modal=1%20"
    }) Require(!TemporaryColumnJournalStore.IsObservedPreferencesUrl(endpoint + query, origin),
        "An unknown, duplicated, private or ambiguous modal query must be rejected: " + query);
    foreach (var address in new[]
    {
        "http://glpi.test/glpi/front/displaypreference.form.php?itemtype=Ticket&_in_modal=1",
        "https://other-glpi.test/glpi/front/displaypreference.form.php?itemtype=Ticket&_in_modal=1",
        "https://glpi.test:8443/glpi/front/displaypreference.form.php?itemtype=Ticket&_in_modal=1",
        "https://user:secret@glpi.test/glpi/front/displaypreference.form.php?itemtype=Ticket&_in_modal=1",
        endpoint + "itemtype=Ticket&_in_modal=1#secret",
        origin + "/glpi/front/ticket.form.php?itemtype=Ticket&_in_modal=1",
        origin + "/glpi/front/displaypreference.form.php/other?itemtype=Ticket&_in_modal=1",
        "about:blank", "/glpi/front/displaypreference.form.php?itemtype=Ticket&_in_modal=1"
    }) Require(!TemporaryColumnJournalStore.IsObservedPreferencesUrl(address, origin),
        "The native modal marker must not relax the observed origin, endpoint or URL privacy requirements: " + address);
    var settings = Settings(); var foreignInstallation = Record(settings);
    foreignInstallation.PreferencesUrl = origin + "/other/front/displaypreference.form.php?itemtype=Ticket&_in_modal=1";
    Throws<InvalidDataException>(() => TemporaryColumnJournalStore.Validate(foreignInstallation, settings),
        "A same-origin modal from another installation must not become a recovery target.");
}

static void CheckNativeModalJournal()
{
    foreach (var query in new[] { "itemtype=Ticket&_in_modal=1", "_in_modal=1&itemtype=Ticket" })
    {
        using var fixture = new Storage(); var settings = Settings(); var record = Record(settings);
        var observedAddress = record.PreferencesUrl = "https://glpi.test/glpi/front/displaypreference.form.php?" + query;
        fixture.Store.Create(settings, record);
        var restartedStore = new TemporaryColumnJournalStore(fixture.Root); var restored = restartedStore.Load(settings)!;
        Require(restored.PreferencesUrl == observedAddress && restored.SessionId == record.SessionId
            && restored.PendingCommand is { Kind: "add", OptionValue: "3" },
            "A restart must retain the exact observed modal URL and pending intent, without dropping or reordering its native query.");
        restored.State = "active"; restartedStore.Save(settings, restored);
        var reread = new TemporaryColumnJournalStore(fixture.Root).Load(settings)!;
        var serializedAddress = JsonNode.Parse(File.ReadAllText(fixture.Store.PathFor(settings)))!["PreferencesUrl"]!.GetValue<string>();
        Require(reread.PreferencesUrl == observedAddress && serializedAddress == observedAddress && reread.State == "active",
            "Subsequent durable writes must keep the native recovery URL exactly as it was observed.");
    }
}

static void CheckMalformedJournal()
{
    using var fixture = new Storage(); var settings = Settings(); var record = Record(settings); fixture.Store.Create(settings, record);
    var path = fixture.Store.PathFor(settings); var valid = File.ReadAllText(path);
    var malformed = new List<string> { "", "{", "null" };
    foreach (var field in new[] { "UsersId", "Original", "Expected", "Added", "PendingCommand" })
    {
        var json = JsonNode.Parse(valid)!.AsObject();
        if (field == "PendingCommand") json[field]!["OptionValue"] = null;
        else json[field] = null;
        malformed.Add(json.ToJsonString());
    }
    var columns = JsonNode.Parse(valid)!.AsObject(); columns["Original"]!["Columns"] = null; malformed.Add(columns.ToJsonString());
    var version = JsonNode.Parse(valid)!.AsObject(); version["Version"] = 999; malformed.Add(version.ToJsonString());
    foreach (var contents in malformed)
    {
        File.WriteAllText(path, contents);
        Throws<InvalidDataException>(() => fixture.Store.Load(settings), "An unreadable or structurally invalid journal must fail explicitly before any native mutation.");
        Require(File.ReadAllText(path) == contents, "A corrupt journal must remain available for diagnosis and must not be replaced by a fresh session.");
    }
}

static void CheckInvalidCommand()
{
    using var fixture = new Storage(); var settings = Settings(); var record = Record(settings); fixture.Store.Create(settings, record);
    var before = File.ReadAllText(fixture.Store.PathFor(settings));
    record.PendingCommand = new() { Kind = "remove", TargetIdentity = "preference:700", Before = Clone(record.Original) };
    Throws<InvalidDataException>(() => fixture.Store.Save(settings, record), "A pending removal of an initial personal column must be rejected.");
    record.PendingCommand = new() { Kind = "business", Before = Clone(record.Original) };
    Throws<InvalidDataException>(() => fixture.Store.Save(settings, record), "The journal must not accept commands outside native display preferences.");
    record.UsersId = "0"; record.PendingCommand = null;
    Throws<InvalidDataException>(() => fixture.Store.Save(settings, record), "A global view cannot become a recovery target.");
    Require(File.ReadAllText(fixture.Store.PathFor(settings)) == before, "Rejected command changes must leave the preceding safe journal intact.");
}

static void CheckCreateFailure()
{
    using var fixture = new Storage(); var rootFile = Path.Combine(fixture.Root, "blocked"); File.WriteAllText(rootFile, "unchanged");
    var store = new TemporaryColumnJournalStore(rootFile); var settings = Settings();
    Throws<IOException>(() => store.Create(settings, Record(settings)), "An unwritable journal destination must stop preparation before claiming a durable session.");
    Require(File.ReadAllText(rootFile) == "unchanged" && Directory.GetFiles(fixture.Root).Length == 1,
        "A failed create must not overwrite an unrelated file or leave a partial recovery record.");
}

static void CheckSaveFailure()
{
    using var fixture = new Storage(); var settings = Settings(); var record = Record(settings); fixture.Store.Create(settings, record);
    var path = fixture.Store.PathFor(settings); var before = File.ReadAllText(path);
    using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        record.State = "active";
        Throws<IOException>(() => fixture.Store.Save(settings, record), "A locked durable journal must stop the next command instead of continuing with only memory state.");
    }
    Require(File.ReadAllText(path) == before && Directory.GetFiles(fixture.Root, "*.tmp").Length == 0,
        "A failed save must preserve the last committed command and leave no half-written substitute.");
}

sealed class Storage : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "GLPI-Pilot-Checks-ColumnJournal-" + Guid.NewGuid().ToString("N"));
    public TemporaryColumnJournalStore Store { get; }
    public Storage() { Directory.CreateDirectory(Root); Store = new(Root); }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
}
