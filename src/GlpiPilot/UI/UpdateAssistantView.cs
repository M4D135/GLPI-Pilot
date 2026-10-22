using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GlpiPilot.Core;
using Microsoft.Win32;

namespace GlpiPilot.UI;

public sealed class UpdateAssistantView : UserControl, IDisposable
{
    private readonly string _installRoot, _updatesRoot, _version, _policyPath;
    private readonly TextBox _archive = new() { IsReadOnly = true, MinWidth = 350, MaxWidth = 800 }, _hash = new() { MaxLength = 64, MinWidth = 350, MaxWidth = 800 }, _commit = new() { MaxLength = 40, MinWidth = 350, MaxWidth = 800 };
    private readonly TextBox _publisher = new() { MaxLength = 64, MinWidth = 350, MaxWidth = 800 };
    private readonly CheckBox _requireSignature = new() { Content = "Exiger une signature Authenticode de l’éditeur ci-dessous", Margin = new Thickness(0, 4, 0, 8) };
    private readonly ComboBox _edition = new() { ItemsSource = new[] { "PROD", "DEV" }, SelectedIndex = 0, Width = 110, Margin = new Thickness(0, 0, 8, 8) };
    private readonly TextBlock _release = PilotControls.Text("Cliquez sur « Vérifier GitHub » pour consulter la dernière publication du dépôt officiel.", 12, true);
    private readonly TextBlock _review = PilotControls.Text("Choisissez une publication ou un ZIP local, puis vérifiez le pack avant de l’installer.", 13);
    private readonly TextBlock _status = PilotControls.Text("Aucune recherche ni installation automatique au démarrage.", 12, true);
    private readonly List<Button> _buttons = new();
    private readonly Button _download, _install, _rollback, _cancel;
    private PreparedUpdate? _prepared;
    private OfficialUpdate? _latest;
    private CancellationTokenSource? _operation;
    private Task _work = Task.CompletedTask;
    private bool _working, _disposed;
    public Task WhenIdle => _work;
    public event Action<PreparedUpdate>? InstallRequested;

    public UpdateAssistantView(string installRoot, string version, string updatesRoot, string policyPath)
    {
        _installRoot = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar); _version = version; _updatesRoot = updatesRoot; _policyPath = policyPath;
        var body = new StackPanel(); body.Children.Add(PilotControls.Text("Version installée : " + version + " · " + _installRoot, 13));
        var online = new WrapPanel(); online.Children.Add(_edition); online.Children.Add(Command("Vérifier GitHub", () => Start(FindLatestAsync)));
        _download = Command("Télécharger et vérifier le pack officiel", () => Start(DownloadLatestAsync)); online.Children.Add(_download);
        online.Children.Add(Command("Ouvrir les publications", () => Process.Start(new ProcessStartInfo(UpdateReleaseClient.ReleasesPage) { UseShellExecute = true }))); body.Children.Add(online); body.Children.Add(_release);
        body.Children.Add(PilotControls.Text("Le contrôle en ligne contacte uniquement GitHub et ses hôtes de téléchargement autorisés. Aucun contenu GLPI, cookie ni identifiant n’est transmis. Le ZIP local permet une préparation hors ligne.", 12, true));
        var local = new StackPanel(); local.Children.Add(Field("Archive ZIP locale", _archive)); local.Children.Add(Command("Choisir le ZIP…", Browse)); local.Children.Add(Field("SHA-256 attendu · depuis la publication de confiance", _hash)); local.Children.Add(Field("Commit attendu · 40 caractères hexadécimaux", _commit));
        local.Children.Add(Command("Vérifier le ZIP local", () => Start(PrepareLocalAsync))); body.Children.Add(new Expander { Header = "Utiliser un pack déjà téléchargé", Content = local, IsExpanded = true });
        var trust = new StackPanel(); trust.Children.Add(_requireSignature); trust.Children.Add(Field("Empreinte SHA-256 du certificat d’éditeur approuvé", _publisher));
        trust.Children.Add(PilotControls.Text("L’empreinte d’un certificat se configure depuis une source de confiance indépendante. Une empreinte de fichier vérifie l’intégrité ; l’état de signature de l’EXE reste indiqué séparément. Sans certificat de publication configuré, les packs peuvent être non signés.", 12, true));
        trust.Children.Add(Command("Enregistrer la politique de confiance", () => { ReadPolicy().Save(_policyPath); _prepared = null; _status.Text = "Politique enregistrée ; vérifiez de nouveau le pack avant installation."; Refresh(); })); body.Children.Add(new Expander { Header = "Signature facultative et éditeur approuvé", Content = trust });
        body.Children.Add(PilotControls.Card(_review)); var actions = new WrapPanel();
        _install = Command("Fermer GLPI Pilot et installer la version vérifiée", () => { if (_prepared == null) throw new InvalidDataException("Vérifiez le pack avant installation."); InstallRequested?.Invoke(_prepared); }); _install.SetResourceReference(Button.StyleProperty, "PrimaryButton"); actions.Children.Add(_install);
        _rollback = Command("Préparer le retour à la version conservée", () => Start(PrepareRollbackAsync)); actions.Children.Add(_rollback);
        _cancel = PilotControls.Button("Annuler la préparation", () => _operation?.Cancel()); actions.Children.Add(_cancel); body.Children.Add(actions); body.Children.Add(_status);
        body.Children.Add(PilotControls.Text("L’installation remplace GLPI-Pilot.exe et le dossier wad après fermeture. Une copie vérifiée de l’ancien pack est conservée. Si la copie ou l’ouverture de la nouvelle fenêtre échoue, l’auxiliaire restaure le pack précédent. Les réglages et données locales gardent leur emplacement habituel.", 12, true));
        Content = PilotControls.Page("Mise à jour et retour arrière", "Dernière publication officielle, vérification complète du pack et installation explicite.", new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        try
        {
            var policy = UpdatePolicy.Load(_policyPath); _requireSignature.IsChecked = policy.RequireSignature; _publisher.Text = policy.TrustedPublisherSha256;
            var last = UpdateService.ReadLastOutcome(_updatesRoot); if (last != null) _status.Text = last.At.ToLocalTime().ToString("g") + " · " + last.Detail;
        }
        catch (Exception error) { _status.Text = "Réglages ou dernier bilan illisibles : " + AppPaths.SafeError(error); }
        foreach (var input in new[] { _hash, _commit, _publisher }) input.TextChanged += (_, _) => { _prepared = null; Refresh(); };
        _requireSignature.Checked += (_, _) => { _prepared = null; Refresh(); }; _requireSignature.Unchecked += (_, _) => { _prepared = null; Refresh(); };
        _edition.SelectionChanged += (_, _) => { _latest = null; Refresh(); };
        Unloaded += (_, _) => Dispose(); Refresh();
    }
    private Button Command(string label, Action action)
    {
        var button = PilotControls.Button(label, () => { if (_working || _disposed) return; try { action(); } catch (Exception error) { _status.Text = AppPaths.SafeError(error); } }); _buttons.Add(button); return button;
    }
    private static FrameworkElement Field(string label, FrameworkElement value) { var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 8) }; panel.Children.Add(PilotControls.Text(label, 12, true)); panel.Children.Add(value); return panel; }
    private UpdatePolicy ReadPolicy() { var value = new UpdatePolicy { RequireSignature = _requireSignature.IsChecked == true, TrustedPublisherSha256 = _publisher.Text.Trim() }; value.Validate(); return value; }
    private void Browse()
    {
        var dialog = new OpenFileDialog { Filter = "Pack GLPI Pilot (*.zip)|*.zip", CheckFileExists = true, Multiselect = false, Title = "Choisir le pack Windows officiel" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return; _archive.Text = dialog.FileName; _hash.Clear(); _commit.Clear(); _prepared = null; _review.Text = "Renseignez les deux références de confiance de cette archive, puis lancez sa vérification."; Refresh();
    }
    private void Start(Func<CancellationToken, Task> operation)
    {
        if (_working || _disposed) return; _operation?.Dispose(); _operation = new(); _working = true; Refresh(); _work = RunAsync(operation, _operation.Token);
    }
    private async Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        try { await operation(ct); }
        catch (OperationCanceledException) { if (!_disposed) _status.Text = ct.IsCancellationRequested ? "Préparation annulée. Aucun fichier de l’installation n’a été remplacé." : "Le service n’a pas répondu dans le délai prévu. La voie ZIP locale reste disponible."; }
        catch (Exception error) { if (!_disposed) { _prepared = null; _status.Text = "Opération interrompue : " + AppPaths.SafeError(error); } }
        finally { _working = false; if (!_disposed) Refresh(); }
    }
    private async Task FindLatestAsync(CancellationToken ct)
    {
        var edition = _edition.SelectedItem as string ?? "PROD"; _status.Text = "Lecture de la publication, du commit et des empreintes du dépôt officiel…";
        using var client = new UpdateReleaseClient(); var release = await client.FindLatestAsync(edition, ct); if (_disposed) return; _latest = release;
        _release.Text = $"Version {release.Version} · {release.Edition} · publiée le {release.PublishedAt.ToLocalTime():dd/MM/yyyy} · {release.Bytes / (1024d * 1024):N1} Mio\nCommit : {release.Commit}\nSHA-256 : {release.ArchiveSha256}";
        _status.Text = Version.Parse(release.Version) > Version.Parse(_version) ? "Une version plus récente est disponible. Le téléchargement reste à votre initiative." : "La publication officielle ne dépasse pas la version installée.";
    }
    private async Task DownloadLatestAsync(CancellationToken ct)
    {
        var release = _latest ?? throw new InvalidDataException("Vérifiez d’abord la dernière publication officielle."); var policy = ReadPolicy(); _prepared = null;
        using var client = new UpdateReleaseClient(); _status.Text = "Téléchargement du pack officiel…";
        var progress = new Progress<long>(bytes => { if (!_disposed) _status.Text = $"Téléchargement : {bytes / (1024d * 1024):N1} / {release.Bytes / (1024d * 1024):N1} Mio"; });
        var archive = await client.DownloadAsync(release, Path.Combine(_updatesRoot, "downloads"), progress, ct);
        try
        {
            if (_disposed) return; _archive.Text = archive; _hash.Text = release.ArchiveSha256; _commit.Text = release.Commit; _status.Text = "Vérification de chaque fichier, de la provenance et de la signature…";
            var prepared = await UpdateService.PrepareAsync(archive, release.ArchiveSha256, release.Commit, _installRoot, _updatesRoot, _version, policy, ct: ct); if (!_disposed) Publish(prepared);
        }
        finally { if (File.Exists(archive)) File.Delete(archive); if (!_disposed && _archive.Text == archive) _archive.Clear(); }
    }
    private async Task PrepareLocalAsync(CancellationToken ct)
    {
        var path = _archive.Text; var hash = _hash.Text.Trim(); var commit = _commit.Text.Trim(); var policy = ReadPolicy(); _prepared = null; _status.Text = "Vérification de l’archive locale et de tous ses fichiers…";
        var prepared = await UpdateService.PrepareAsync(path, hash, commit, _installRoot, _updatesRoot, _version, policy, ct: ct); if (!_disposed) Publish(prepared);
    }
    private async Task PrepareRollbackAsync(CancellationToken ct)
    {
        _prepared = null; _status.Text = "Vérification de la dernière sauvegarde de binaires…";
        var prepared = await Task.Run(() =>
        {
            var previous = UpdateService.FindRollback(_updatesRoot, _installRoot) ?? throw new InvalidDataException("Aucune version antérieure conservée n’est disponible pour cette installation.");
            return UpdateService.PrepareRollback(previous, _updatesRoot, ct);
        }, ct);
        if (!_disposed) Publish(prepared);
    }
    private void Publish(PreparedUpdate prepared)
    {
        _prepared = prepared;
        _review.Text = (prepared.IsRollback ? "Retour arrière préparé" : "Pack vérifié") + $" : {prepared.Package.Version} · {prepared.Package.Edition}\n{prepared.Files.Count:N0} fichiers vérifiés ; commit {prepared.Package.SourceCommit}.\n"
            + (prepared.IsRollback ? "Copie antérieure vérifiée contre son inventaire conservé." : prepared.Signature.Description) + "\nSauvegarde prévue : " + prepared.BackupRoot + "\nL’installation attend votre clic sur le bouton de fermeture et d’installation.";
        _status.Text = "Préparation terminée. La version en cours reste inchangée jusqu’à l’installation.";
    }
    private void Refresh()
    {
        if (_disposed) return; foreach (var button in _buttons) button.IsEnabled = !_working; _edition.IsEnabled = _requireSignature.IsEnabled = !_working;
        _hash.IsReadOnly = _commit.IsReadOnly = _publisher.IsReadOnly = _working; _download.IsEnabled = !_working && _latest != null && Version.TryParse(_latest.Version, out var next) && Version.TryParse(_version, out var current) && next > current;
        _install.IsEnabled = !_working && _prepared != null; _rollback.IsEnabled = !_working; _cancel.IsEnabled = _working;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _operation?.Cancel(); _operation?.Dispose(); }
}
