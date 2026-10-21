# Contrôles de l’aperçu

Projet de tests console .NET 10, sans navigateur, portail ni dépendance WPF.

```sh
dotnet run --project tests/PreviewQueryChecks/PreviewQueryChecks.csproj
```

Les fixtures sont synthétiques. Les contrôles exercent le service réellement utilisé par l’interface : recherche littérale au-delà des 10 000 premières lignes, compte complet, intersection avec un périmètre dashboard, indices invalides et répétés, annulation pendant le parcours, table triée et résolution du bon indice source. Les lignes étrangères, supprimées ou liées à un relevé remplacé sont refusées.

Les contrôles vérifient aussi les préférences locales de colonnes : empreinte ordonnée des en-têtes, valeurs vides ou dupliquées, au moins une colonne visible, largeurs finies et bornées, ordre complet, copie indépendante, lecture et écriture atomique, récupération d’un fichier malformé et nombre de profils limité. Le fichier de préférences ne contient ni en-têtes en clair ni valeurs de cellules.

Ces tests ne lancent pas l’interface Windows. Le rendu du dialogue, les événements WPF et l’intégration des raccourcis doivent être vérifiés sous Windows avec le projet principal.
