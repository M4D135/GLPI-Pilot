using System.IO;
using System.Windows;
using System.Windows.Controls;
using GlpiPilot.Core;
using Microsoft.Win32;

namespace GlpiPilot.UI;

public sealed partial class QueryWorkshopView
{
    private readonly Button _importPack = Command("Importer un pack…"), _exportPack = Command("Exporter le métier…"), _builtinPacks = Command("Packs métiers fournis…");
    private void AddPackActions(Panel panel)
    {
        panel.Children.Add(_importPack); panel.Children.Add(_exportPack); panel.Children.Add(_builtinPacks);
        _importPack.Click += (_, _) => PackAction(() =>
        {
            CommitDraft(); var dialog = new OpenFileDialog { Filter = "Pack de requêtes (*.json)|*.json", CheckFileExists = true };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) ReviewPack(QueryPackService.Read(dialog.FileName));
        });
        _exportPack.Click += (_, _) => PackAction(() =>
        {
            CommitDraft(); var category = SelectedQuery?.Category ?? "";
            var queries = _working.Queries.Where(q => q.Category == category).Select(q => q.Copy()).ToList();
            if (queries.Count == 0) throw new InvalidDataException("Sélectionnez une requête du métier à exporter.");
            var pack = new QueryPack { Name = string.IsNullOrWhiteSpace(category) ? "Mes requêtes" : category, Description = "Pack exporté depuis la catégorie sélectionnée dans GLPI Pilot.", Queries = queries };
            var dialog = new SaveFileDialog { Filter = "Pack de requêtes (*.json)|*.json", FileName = "GLPI-pack-metier.json", AddExtension = true };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) { QueryPackService.Write(pack, dialog.FileName); SetStatus($"Pack « {pack.Name} » exporté : {queries.Count} requêtes.", "SuccessBrush"); }
        });
        _builtinPacks.Click += (_, _) => PackAction(() =>
        {
            var packs = QueryPackService.BuiltInPacks(); var menu = new ContextMenu();
            foreach (var pack in packs)
            {
                var item = new MenuItem { Header = pack.Name + " · " + pack.Queries.Count + " modèles" }; item.Click += (_, _) => PackAction(() => { CommitDraft(); ReviewPack(pack); }); menu.Items.Add(item);
            }
            menu.PlacementTarget = _builtinPacks; menu.IsOpen = true;
        });
    }
    private void ReviewPack(QueryPack pack)
    {
        var preview = QueryPackService.PreviewMerge(_working, pack);
        if (preview.Added == 0) { SetStatus($"Pack « {pack.Name} » déjà présent : {preview.Identical} requêtes identiques.", "MutedBrush"); return; }
        AskConfirmation($"Pack « {pack.Name} »\n{pack.Description}\n{preview.Added} ajouts · {preview.Identical} identiques ignorés · {preview.Conflicts} conflits conservés sous de nouveaux identifiants.\n" + string.Join("\n", preview.Names.Take(12)) + (preview.Names.Count > 12 ? "\n…" : "") + "\nAjouter ce pack au brouillon de bibliothèque ?", "Ajouter le pack au brouillon", () =>
        {
            _working = preview.Library; _dirty = true; EnsureSavedFields(); RebuildLibrary(); LoadEditor();
            InvalidatePreview("Pack importé dans le brouillon. Prévisualisez ses requêtes sur le relevé courant.");
            SetStatus($"{preview.Added} requêtes ajoutées au brouillon. Enregistrez la bibliothèque pour les conserver.", "WarningBrush");
        });
    }
    private void PackAction(Action action)
    {
        if (_busy || _disposed) return;
        try { action(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException) { SetStatus(error.Message, "ErrorBrush"); }
    }
}
