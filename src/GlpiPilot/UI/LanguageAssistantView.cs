using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

/// <summary>Explicit preparation, generation, validation and preview. Opening this view never contacts a provider.</summary>
public sealed class LanguageAssistantView : UserControl, IDisposable
{
    private readonly ExtractionResult _source;
    private readonly DashboardSettings _dashboard;
    private readonly DashboardSnapshot? _scopeSnapshot;
    private readonly Func<bool> _isCurrent;
    private readonly Func<SavedQuery, CancellationToken, Task<QueryResult>> _previewQuery;
    private readonly LanguageAssistantStore _store;
    private LanguageAssistantSettings _saved = new();
    private readonly ComboBox _operation = Choice(new("query", "Créer une requête"), new("summary", "Synthétiser des tickets"));
    private readonly ComboBox _provider = Choice(new("local", "Moteur local déterministe"), new("compatible", "Fournisseur compatible · facultatif"));
    private readonly ComboBox _scope = Choice(new("dashboard", "Périmètre du dashboard capturé à l’ouverture"), new("all", "Tout le relevé"));
    private readonly TextBox _prompt = Box(2000), _endpoint = Box(2000), _model = Box(120), _maximum = Box(3), _timeout = Box(3), _ids = Box(2000);
    private readonly CheckBox _external = Check("Autoriser une génération vers l’endpoint affiché, avec le contenu préparé"), _rememberKey = Check("Mémoriser la clé pour cet endpoint sous mon compte Windows");
    private readonly CheckBox _titles = Check("Inclure les titres"), _people = Check("Inclure techniciens, groupes et demandeurs"), _dates = Check("Inclure les dates observées"), _places = Check("Inclure villes, lieux et entités");
    private readonly PasswordBox _key = new() { MaxLength = 8000, MinHeight = 34, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _keyState = PilotControls.Text("Aucune clé mémorisée.", 12, true), _status = PilotControls.Text("Choisissez une opération et préparez son contenu.", 12, true);
    private readonly TextBox _preparation = ReadOnlyBox(), _queryJson = ReadOnlyBox();
    private readonly StackPanel _inputs = new(), _summaryOptions = new(), _compatibleOptions = new(), _result = new(), _summaryResult = new();
    private readonly TabControl _tabs = new();
    private readonly TabItem _composeTab = new() { Header = "1 · Demande" }, _prepareTab = new() { Header = "2 · Contenu à utiliser" }, _resultTab = new() { Header = "3 · Proposition et vérification" };
    private readonly Button _prepare, _generate, _cancel, _preview, _accept, _showPreviewRows;
    private readonly List<Button> _configurationButtons = new();
    private readonly TextBlock _queryExplanation = PilotControls.Text("", 13), _queryPreview = PilotControls.Text("", 13);
    private LanguageAssistantRequest? _prepared;
    private LanguageQueryDraft? _draft;
    private QueryResult? _previewed;
    private CancellationTokenSource? _cancellation;
    private bool _working, _disposed, _loading;
    private Task _work = Task.CompletedTask;
    public Task WhenIdle => _work;
    public event Action<SavedQuery>? DraftAccepted;
    public event Action<int[], string>? RowsRequested;

    public LanguageAssistantView(ExtractionResult source, DashboardSettings dashboard, DashboardSnapshot? scopeSnapshot,
        Func<bool> isCurrent, Func<SavedQuery, CancellationToken, Task<QueryResult>> previewQuery, LanguageAssistantStore? store = null)
    {
        _source = source; _dashboard = dashboard.Copy(); _scopeSnapshot = scopeSnapshot; _isCurrent = isCurrent; _previewQuery = previewQuery; _store = store ?? new();
        _prompt.Text = "tickets actifs par catégorie"; _prompt.AcceptsReturn = true; _prompt.TextWrapping = TextWrapping.Wrap; _prompt.MinHeight = 90;
        _maximum.Text = "50"; _timeout.Text = "90"; _ids.ToolTip = "Facultatif : identifiants séparés par espaces, virgules ou points-virgules ; 200 au maximum.";
        _inputs.Children.Add(Field("Opération", _operation));
        _inputs.Children.Add(Field("Demande", _prompt));
        _inputs.Children.Add(new Expander { Header = "Exemples du moteur local", Content = PilotControls.Text(LanguageAssistantLocalEngine.Help, 12, true), Margin = new Thickness(0, 0, 0, 10) });
        _summaryOptions.Children.Add(Field("Périmètre des tickets fournis", _scope));
        _summaryOptions.Children.Add(Field("Identifiants précis dans ce périmètre · facultatif", _ids));
        var limits = new WrapPanel(); _maximum.Width = 100; _timeout.Width = 100;
        limits.Children.Add(Field("Tickets · 1 à 200", _maximum)); limits.Children.Add(Field("Délai · 10 à 180 s", _timeout));
        _summaryOptions.Children.Add(PilotControls.Text("Champs de base : identifiant, statut, priorité, type et catégorie lorsqu’ils existent. Les identifiants répétés sont exclus.", 12, true));
        var extras = new WrapPanel(); foreach (var checkbox in new[] { _titles, _people, _dates, _places }) extras.Children.Add(checkbox); _summaryOptions.Children.Add(extras);
        _inputs.Children.Add(_summaryOptions); _inputs.Children.Add(limits);
        _inputs.Children.Add(Field("Moteur", _provider));
        _compatibleOptions.Children.Add(Field("Endpoint complet · https://…/chat/completions ou http://localhost:…/v1/chat/completions", _endpoint));
        _compatibleOptions.Children.Add(Field("Nom du modèle fourni par cet endpoint", _model));
        _compatibleOptions.Children.Add(Field("Clé d’accès · facultative pour un serveur local · conservée en mémoire pour cette session", _key));
        _compatibleOptions.Children.Add(_keyState); _compatibleOptions.Children.Add(_rememberKey); _compatibleOptions.Children.Add(_external);
        _compatibleOptions.Children.Add(PilotControls.Text("Le contenu préparé est envoyé uniquement après « Générer la proposition ». Le fournisseur peut traiter ou conserver les données selon ses propres conditions. GLPI Pilot demande store=false et refuse les redirections. Une annulation ne peut pas rappeler un contenu déjà envoyé.", 12, true));
        _inputs.Children.Add(_compatibleOptions);
        var profiles = new WrapPanel();
        profiles.Children.Add(ConfigurationButton("Enregistrer ce profil local", SaveProfile));
        profiles.Children.Add(ConfigurationButton("Oublier la clé mémorisée", ForgetKey)); _inputs.Children.Add(profiles);
        _composeTab.Content = Scroll(_inputs);

        _prepare = PilotControls.Button("Préparer le contenu", () => Start(PrepareAsync));
        _generate = PilotControls.Button("Générer la proposition", () => Start(GenerateAsync));
        _cancel = PilotControls.Button("Annuler", () => _cancellation?.Cancel());
        _generate.SetResourceReference(Button.StyleProperty, "PrimaryButton");
        var preparation = new Grid(); preparation.RowDefinitions.Add(new() { Height = GridLength.Auto }); preparation.RowDefinitions.Add(new());
        preparation.Children.Add(PilotControls.Text("Relisez la destination, le périmètre et le contenu exact ci-dessous. Une modification de la demande invalide cette préparation. La clé d’accès n’est jamais incluse dans cet aperçu.", 12, true));
        Grid.SetRow(_preparation, 1); preparation.Children.Add(_preparation); _prepareTab.Content = preparation;

        _preview = PilotControls.Button("Prévisualiser sur le relevé", () => Start(PreviewAsync));
        _accept = PilotControls.Button("Ajouter au brouillon dans l’atelier", AcceptDraft);
        _showPreviewRows = PilotControls.Button("Voir les lignes de l’aperçu", () =>
        {
            if (_previewed is not { Available: true } preview || !_isCurrent()) { SetStatus("Le relevé a changé. Ouvrez de nouveau l’assistant."); return; }
            RowsRequested?.Invoke(preview.Rows, "Prévisualisation de la requête assistée");
        });
        _queryJson.MinHeight = 180; _queryJson.MaxHeight = 360;
        _result.Children.Add(_queryExplanation); _result.Children.Add(_queryJson);
        var queryActions = new WrapPanel(); queryActions.Children.Add(_preview); queryActions.Children.Add(_accept); queryActions.Children.Add(_showPreviewRows);
        _result.Children.Add(queryActions); _result.Children.Add(_queryPreview); _result.Children.Add(_summaryResult); _resultTab.Content = Scroll(_result);
        _tabs.Items.Add(_composeTab); _tabs.Items.Add(_prepareTab); _tabs.Items.Add(_resultTab);
        var body = new Grid(); body.RowDefinitions.Add(new()); body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.Children.Add(_tabs); Grid.SetRow(_status, 1); _status.Margin = new Thickness(0, 10, 0, 0); body.Children.Add(_status);
        var toolbar = new WrapPanel(); toolbar.Children.Add(_prepare); toolbar.Children.Add(_generate); toolbar.Children.Add(_cancel);
        Content = PilotControls.Page("Assistant de requêtes et de synthèse", $"Relevé du {source.ExtractedAt.ToLocalTime():dd/MM/yyyy HH:mm} · {source.Rows.Count:N0} lignes · {(source.Complete ? "complétude déclarée" : "complétude non confirmée")} · proposition à relire", body, toolbar);
        AutomationProperties.SetAutomationId(this, "LanguageAssistant");
        AutomationProperties.SetAutomationId(_prepare, "LanguagePrepare"); AutomationProperties.SetAutomationId(_generate, "LanguageGenerate");
        AutomationProperties.SetAutomationId(_preview, "LanguageQueryPreview"); AutomationProperties.SetAutomationId(_accept, "LanguageAcceptDraft");
        foreach (var (control, name) in new (DependencyObject, string)[] { (_prompt, "Demande en langage naturel"), (_endpoint, "Endpoint complet du fournisseur"), (_model, "Nom du modèle"), (_key, "Clé d’accès facultative"), (_ids, "Identifiants à inclure"), (_maximum, "Maximum de tickets fournis"), (_timeout, "Délai en secondes") }) AutomationProperties.SetName(control, name);
        LoadProfile();
        foreach (var box in new[] { _prompt, _endpoint, _model, _maximum, _timeout, _ids }) box.TextChanged += (_, _) => Invalidate();
        foreach (var check in new[] { _external, _titles, _people, _dates, _places }) { check.Checked += (_, _) => Invalidate(); check.Unchecked += (_, _) => Invalidate(); }
        _provider.SelectionChanged += (_, _) => Invalidate(); _scope.SelectionChanged += (_, _) => Invalidate();
        _operation.SelectionChanged += (_, _) =>
        {
            if (_prompt.Text is "tickets actifs par catégorie" or "synthèse par statut") _prompt.Text = Operation == "summary" ? "synthèse par statut" : "tickets actifs par catégorie";
            Invalidate();
        };
        _key.PasswordChanged += (_, _) => Invalidate(); Unloaded += (_, _) => Dispose();
        _scope.SelectedValue = scopeSnapshot is null ? "all" : "dashboard"; Refresh();
    }

    private string Operation => _operation.SelectedValue as string ?? "query";
    private string Provider => _provider.SelectedValue as string ?? "local";
    private void LoadProfile()
    {
        _loading = true;
        try
        {
            _saved = _store.Load(); _provider.SelectedValue = _saved.Provider; _endpoint.Text = _saved.Endpoint; _model.Text = _saved.Model;
            _maximum.Text = _saved.MaximumTickets.ToString(CultureInfo.InvariantCulture); _timeout.Text = _saved.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            _external.IsChecked = false; UpdateKeyState();
        }
        catch (Exception error) { SetStatus("Le profil local n’a pas pu être chargé : " + AppPaths.SafeError(error)); _saved = new(); }
        finally { _loading = false; }
    }
    private LanguageAssistantSettings ReadSettings()
    {
        var settings = _saved.Copy(); settings.Provider = Provider; settings.Endpoint = _endpoint.Text.Trim(); settings.Model = _model.Text.Trim(); settings.ExternalEnabled = _external.IsChecked == true;
        if (!int.TryParse(_maximum.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var maximum) || !int.TryParse(_timeout.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var timeout))
            throw new InvalidDataException("La limite et le délai doivent être des nombres entiers.");
        settings.MaximumTickets = maximum; settings.TimeoutSeconds = timeout; settings.Validate(); return settings;
    }
    private void SaveProfile()
    {
        var settings = ReadSettings();
        if (_rememberKey.IsChecked == true) LanguageAssistantStore.RememberKey(settings, _key.Password);
        settings.ExternalEnabled = false; _store.Save(settings); _saved = settings; UpdateKeyState(); Invalidate();
        SetStatus("Profil enregistré localement. L’autorisation d’envoi devra être réactivée à la prochaine ouverture.");
    }
    private void ForgetKey()
    {
        var settings = _saved.Copy(); settings.ProtectedApiKey = ""; settings.KeyEndpoint = ""; settings.ExternalEnabled = false;
        _store.Save(settings); _saved = settings; _key.Clear(); _rememberKey.IsChecked = false; UpdateKeyState(); Invalidate(); SetStatus("Clé mémorisée supprimée et clé de session effacée.");
    }
    private void UpdateKeyState() => _keyState.Text = _saved.ProtectedApiKey.Length == 0 ? "Aucune clé mémorisée." : "Clé protégée sous votre compte Windows pour : " + _saved.KeyEndpoint;
    private async Task PrepareAsync(CancellationToken ct)
    {
        RequireCurrent(); var settings = ReadSettings(); var operation = Operation; var prompt = _prompt.Text;
        var useDashboard = _scope.SelectedValue as string == "dashboard" && _scopeSnapshot is not null;
        var rows = useDashboard ? _scopeSnapshot!.ScopeRows.ToArray() : Enumerable.Range(0, _source.Rows.Count).ToArray();
        var scope = useDashboard ? _scopeSnapshot!.ScopeLabel : "Tout le relevé";
        var ids = _ids.Text.Split(new[] { ' ', '\r', '\n', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var roles = LanguageAssistantService.DefaultSummaryRoles.ToList();
        if (_titles.IsChecked == true) roles.Add("title");
        if (_people.IsChecked == true) roles.AddRange(new[] { "assignee", "group", "requester" });
        if (_dates.IsChecked == true) roles.AddRange(new[] { "created", "updated", "resolved", "closed", "due" });
        if (_places.IsChecked == true) roles.AddRange(new[] { "city", "location", "entity" });
        _prepared = null; ClearResult(); SetStatus("Préparation du périmètre et des champs visibles…");
        var request = await Task.Run(() => LanguageAssistantService.Prepare(operation, prompt, settings,
            LanguageAssistantService.BuildContext(_source, _dashboard, operation, rows, scope, roles, settings.MaximumTickets, ids.Length == 0 ? null : ids, ct)), ct);
        ct.ThrowIfCancellationRequested(); if (_disposed) return; RequireCurrent(); _prepared = request; _preparation.Text = request.Preview; _tabs.SelectedItem = _prepareTab;
        SetStatus(settings.Provider == "local" ? "Préparation prête. Le moteur local ne contacte aucun fournisseur." : "Préparation prête. Relisez le contenu puis déclenchez l’envoi si vous l’autorisez.");
    }
    private async Task GenerateAsync(CancellationToken ct)
    {
        RequireCurrent(); var request = _prepared ?? throw new InvalidOperationException("Préparez d’abord le contenu à utiliser.");
        ClearResult(); SetStatus(request.Settings.Provider == "local" ? "Génération locale…" : "Envoi explicite vers " + request.Settings.Endpoint + "…");
        var response = request.Settings.Provider == "local" ? await Task.Run(() => LanguageAssistantLocalEngine.Generate(request, ct), ct)
            : await LanguageAssistantClient.GenerateAsync(request, _key.Password, ct);
        ct.ThrowIfCancellationRequested(); if (_disposed) return; RequireCurrent();
        if (request.Operation == "query")
        {
            _draft = LanguageAssistantService.ParseQuery(response, request.Context);
            _queryJson.Text = JsonSerializer.Serialize(_draft.Query, LanguageAssistantService.Json);
            _queryExplanation.Text = _draft.Explanation + "\n\nHypothèses à vérifier :\n" + string.Join("\n", _draft.Assumptions.Select(a => "• " + a));
            _queryPreview.Text = "La proposition n’a pas encore été exécutée. Prévisualisez pour vérifier le calcul, sa disponibilité et ses lignes avant de l’ajouter au brouillon.";
            SetStatus("Contrat de requête et champs validés. La prévisualisation reste à votre initiative.");
        }
        else
        {
            var summary = LanguageAssistantService.ParseSummary(response, request.Context); ShowSummary(summary, request.Context);
            SetStatus("Identifiants, champs et extraits contrôlés. Vérifiez aussi le sens et la portée des constats ; ce contrôle ne prouve pas leur justesse sémantique.");
        }
        _tabs.SelectedItem = _resultTab;
    }
    private async Task PreviewAsync(CancellationToken ct)
    {
        RequireCurrent(); if (_draft is null) throw new InvalidOperationException("Générez une proposition de requête.");
        _previewed = null; SetStatus("Calcul explicite de la proposition sur toutes les lignes du relevé…");
        var result = await _previewQuery(_draft.Query.Copy(), ct); ct.ThrowIfCancellationRequested(); if (_disposed) return; RequireCurrent();
        _previewed = result;
        _queryPreview.Text = result.Available ? $"Résultat : {result.Metric.Value:N2} {result.Metric.Unit} · {result.Rows.Length:N0} lignes correspondantes · {result.UnknownRows:N0} lignes indéterminées.\n{result.Metric.Explanation}\n{result.Message}"
            : "Prévisualisation indisponible : " + result.Message;
        SetStatus(result.Available ? "Prévisualisation terminée. Le brouillon peut être ouvert dans l’atelier pour être relu et enregistré." : "La proposition reste visible ; son ajout est bloqué tant que l’aperçu est indisponible.");
    }
    private void AcceptDraft()
    {
        if (_working || _disposed || _draft is null || _previewed is not { Available: true }) return;
        try { RequireCurrent(); DraftAccepted?.Invoke(_draft.Query.Copy()); }
        catch (Exception error) { SetStatus(AppPaths.SafeError(error)); }
    }
    private void ShowSummary(LanguageSummary summary, LanguageAssistantContext context)
    {
        _summaryResult.Children.Add(PilotControls.Text(summary.Title, 20));
        _summaryResult.Children.Add(PilotControls.Text($"{context.Tickets.Count} tickets fournis sur {context.ScopeRows} lignes · relevé du {context.ExtractedAt.ToLocalTime():g} · {context.Scope}", 12, true));
        _summaryResult.Children.Add(PilotControls.Text("Les références et extraits sont contrôlés automatiquement. Le sens des phrases, les regroupements et les conclusions restent à vérifier par la personne qui utilise cette synthèse.", 12, true));
        var sourceRows = context.Tickets.ToDictionary(t => t.Id, t => t.SourceRow, StringComparer.Ordinal);
        foreach (var claim in summary.Claims)
        {
            var card = new StackPanel(); card.Children.Add(PilotControls.Text(claim.Text)); var citations = new StackPanel();
            foreach (var citation in claim.Citations)
            {
                var row = sourceRows[citation.TicketId];
                citations.Children.Add(PilotControls.Button("#" + citation.TicketId + " · " + citation.Field + " : « " + citation.Quote + " »", () =>
                {
                    if (!_isCurrent()) { SetStatus("Le relevé a changé. Ouvrez de nouveau l’assistant pour consulter une référence à jour."); return; }
                    RowsRequested?.Invoke(new[] { row }, "Source de la synthèse · ticket " + citation.TicketId);
                }, "Ouvrir la ligne source du relevé daté ; aucune lecture ou modification de GLPI"));
            }
            card.Children.Add(new Expander { Header = $"{claim.Citations.Count} référence(s) et extrait(s) vérifiés", Content = citations });
            _summaryResult.Children.Add(PilotControls.Card(card));
        }
        var limitations = context.Limitations.Concat(summary.Limitations).Distinct(StringComparer.Ordinal);
        _summaryResult.Children.Add(PilotControls.Text("Limites et incertitudes\n" + string.Join("\n", limitations.Select(l => "• " + l)), 12, true));
    }
    private void Invalidate()
    {
        if (_loading || _disposed) return; _prepared = null; _preparation.Clear(); ClearResult(); Refresh();
        SetStatus("Demande modifiée. Préparez le contenu avant de générer une nouvelle proposition.");
    }
    private void ClearResult() { _draft = null; _previewed = null; _queryExplanation.Text = ""; _queryJson.Clear(); _queryPreview.Text = ""; _summaryResult.Children.Clear(); }
    private void RequireCurrent() { if (!_isCurrent()) throw new InvalidOperationException("Le relevé ou son contexte a changé. Fermez puis ouvrez de nouveau l’assistant."); }
    private void Start(Func<CancellationToken, Task> operation)
    {
        if (_working || _disposed) return; _cancellation?.Dispose(); _cancellation = new(); _working = true; Refresh(); _work = RunAsync(operation, _cancellation.Token);
    }
    private async Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        try { await operation(ct); }
        catch (OperationCanceledException) { if (!_disposed) SetStatus("Opération annulée. Un fournisseur peut avoir reçu la demande déjà envoyée."); }
        catch (Exception error) { if (!_disposed) { _draft = null; _previewed = null; SetStatus("Proposition refusée ou opération interrompue : " + AppPaths.SafeError(error)); } }
        finally { _working = false; if (!_disposed) Refresh(); }
    }
    private void Refresh()
    {
        _inputs.IsEnabled = !_working; _summaryOptions.Visibility = Operation == "summary" ? Visibility.Visible : Visibility.Collapsed;
        _compatibleOptions.Visibility = Provider == "compatible" ? Visibility.Visible : Visibility.Collapsed;
        _prepare.IsEnabled = !_working && !_disposed; _generate.IsEnabled = !_working && !_disposed && _prepared is not null && (_prepared.Settings.Provider == "local" || _prepared.Settings.ExternalEnabled);
        _cancel.IsEnabled = _working; _preview.IsEnabled = !_working && _draft is not null; _accept.IsEnabled = !_working && _previewed is { Available: true }; _showPreviewRows.IsEnabled = _accept.IsEnabled;
        foreach (var button in _configurationButtons) button.IsEnabled = !_working;
        foreach (var element in new FrameworkElement[] { _queryExplanation, _queryJson, _preview, _accept, _showPreviewRows, _queryPreview }) element.Visibility = _draft is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void SetStatus(string text) => _status.Text = text;
    private Button ConfigurationButton(string label, Action action)
    {
        var button = PilotControls.Button(label, () => { if (_working || _disposed) return; try { action(); } catch (Exception error) { SetStatus(AppPaths.SafeError(error)); } });
        _configurationButtons.Add(button); return button;
    }
    private static ComboBox Choice(params QueryChoice[] items) => new() { ItemsSource = items, DisplayMemberPath = nameof(QueryChoice.Label), SelectedValuePath = nameof(QueryChoice.Key), SelectedIndex = 0, MinHeight = 34, Margin = new Thickness(0, 0, 0, 8) };
    private static TextBox Box(int maximum) => new() { MaxLength = maximum, MinHeight = 34, Margin = new Thickness(0, 0, 8, 8), VerticalContentAlignment = VerticalAlignment.Center };
    private static TextBox ReadOnlyBox() => new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(8), FontSize = 12 };
    private static CheckBox Check(string text) => new() { Content = text, Margin = new Thickness(0, 0, 14, 10) };
    private static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 6, 12, 0) };
    private static FrameworkElement Field(string label, FrameworkElement control) { var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) }; panel.Children.Add(PilotControls.Text(label, 12, true)); panel.Children.Add(control); return panel; }
    public void Dispose() { if (_disposed) return; _disposed = true; _cancellation?.Cancel(); _key.Clear(); _prepared = null; }
}
