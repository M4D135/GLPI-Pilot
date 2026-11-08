# Contrôles des tournées locales

Ce projet BCL .NET 10 lie les vrais services de tournées, de repérage des villes,
de techniciens et de dashboard. Il ne charge aucune interface graphique et ne contacte ni GLPI, ni un
service de cartes. Les tickets et identités sont synthétiques ; les noms de villes
et les coordonnées servent uniquement aux fixtures de calcul.

```powershell
dotnet run --project tests/TourPlanningChecks/TourPlanningChecks.csproj -c Release
```

Les **87 contrôles** vérifient les périmètres actifs/tous statuts, les affectations
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

Les [opérations C# exposées aux pages](../TerrainBridgeChecks) et leurs
[interactions WebView](../TerrainWebChecks) sont vérifiées séparément. Le
[harnais Windows](../WindowsStartupChecks/README.md) lance le pack réel avec son
runtime fixe ; une réussite de ce projet BCL ne constitue pas une exécution native.

Les scénarios de tournées datées vérifient les paniers d’ID
uniques, regroupement par site, fenêtres et attentes, départ/retour, distribution
par durées saisies, verrouillages, scénarios indépendants, rapprochement de
relevés réordonnés ou partiels, reconstruction explicite, calendrier UTC avec
échappement et pliage UTF-8, changement d’heure, briefing encodé et persistance.
La couverture comprend également les états motivés, ouvertures et fermetures des
sites, contacts et matériel, propositions sur plusieurs jours, compétences,
absences, présence et budgets par demi-journée. Une capacité positive n’invente
pas de présence ; les contraintes de retour et les étapes verrouillées sont
conservées. Les adaptateurs routiers et météo reçoivent des réponses HTTP de
fixture et vérifient consentement, bornes, erreurs, annulation et péremption.
Aucun fournisseur externe réel n’est contacté ; une information absente reste
inconnue.
