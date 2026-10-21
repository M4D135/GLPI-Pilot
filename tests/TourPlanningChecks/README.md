# Contrôles des tournées locales

Ce projet BCL .NET 10 lie les vrais services de tournées, de repérage des villes,
de techniciens et de dashboard. Il ne charge pas WPF et ne contacte ni GLPI, ni un
service de cartes. Les tickets et identités sont synthétiques ; les noms de villes
et les coordonnées servent uniquement aux fixtures de calcul.

```powershell
dotnet run --project tests/TourPlanningChecks/TourPlanningChecks.csproj -c Release
```

Les **41 contrôles** vérifient les périmètres actifs/tous statuts, les affectations
multiples sans double comptage de l’équipe, les villes inconnues/ambiguës et les
titres indisponibles, les indices source, les compteurs et échantillons canoniques,
les ordres de visite indépendants et la proximité indicative à vol d’oiseau.
L’export conserve chaque ligne sélectionnée une seule fois, dans l’ordre des
étapes affichées, puis les lignes sans localisation certaine sans leur attribuer
d’étape. La provenance et la complétude source sont préservées.

Les contrôles couvrent aussi l’isolation des copies, l’annulation, les rapports de
localisation incohérents, les préférences locales bornées, la séparation des
périmètres et un échec d’écriture. Les préférences utilisent un répertoire
temporaire propre au test, supprimé à la fin.

Une réussite de ce projet ne constitue pas une exécution des contrôles WPF sous
Windows. Cette validation est distincte dans `tests/WindowsStartupChecks`.
