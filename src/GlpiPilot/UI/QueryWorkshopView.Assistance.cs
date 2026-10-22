using System.IO;
using GlpiPilot.Core;

namespace GlpiPilot.UI;

public sealed partial class QueryWorkshopView
{
    public void AddAssistedDraft(SavedQuery proposed)
    {
        if (_busy || _disposed) throw new InvalidOperationException("L’atelier n’est pas disponible pour recevoir un brouillon.");
        CommitDraft(); proposed.Validate();
        if (_working.Queries.Count >= MaximumQueries) throw new InvalidDataException("La bibliothèque contient déjà 128 requêtes. Libérez une place avant d’ajouter cette proposition.");
        var query = proposed.Copy(); query.Id = Guid.NewGuid().ToString("N"); query.Name = UniqueName(query.Name); query.Category = "Assistant";
        _working.Queries.Add(query); SelectNew(query.Id);
        SetStatus("Proposition assistée non enregistrée. Vérifiez les critères et l’aperçu, puis enregistrez la bibliothèque.", "WarningBrush");
    }
}
