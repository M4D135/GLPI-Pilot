# Vérifications du tableau de bord

Exécuter avec le SDK .NET 10 :

```bash
dotnet run --project tests/DashboardChecks/DashboardChecks.csproj -c Release
```

Les jeux synthétiques fixent l’horloge au 21 octobre 2026 à 12 h, UTC+02.
Ils vérifient les correspondances de colonnes françaises, les ambiguïtés,
les proportions et dénominateurs, les dates françaises/américaines/ISO,
les médianes et percentiles, la météo pondérée et ses limites de couverture,
les doublons sans suppression de lignes, les données manquantes et les relevés
partiels. Un jeu de 10 001 lignes prouve que la limite de l’aperçu ne limite pas
les statistiques. Le bilan Excel est ouvert comme archive et chaque XML lu.

La version 1.3.0 ajoute les libellés masculins d’impact (Moyen, Haut, etc.),
les en-têtes qualifiés de ticket ou de lieu et les doublons renommés à l’import.
Une correspondance ambiguë reste indisponible jusqu’au choix explicite de
l’utilisateur ; un suffixe de colonne arbitraire ne suffit jamais à la déduire.

Les vues par technicien utilisent uniquement la colonne d’affectation choisie.
Les retours à la ligne, points-virgules et barres verticales entourées d’espaces
séparent les affectations multiples. Les noms ne sont pas découpés sur les
espaces, virgules, tirets ou barres obliques. La casse et les espaces sont
normalisés ; accents et ponctuation restent distincts. Chaque ligne n’est
comptée qu’une fois dans la vue d’un technicien. Un ticket avec plusieurs
techniciens peut apparaître dans plusieurs vues : leurs totaux ne s’additionnent
donc pas pour reconstituer l’équipe.

Les tests de vues personnelles contrôlent les effectifs, l’absence de statut,
les noms proches, le technicien mémorisé absent d’un relevé, les références aux
lignes originales pour toutes les interactions et le bilan exporté. Un relevé
source incomplet reste incomplet dans chaque vue ; aucun total personnel
inconnu n’est inventé. Les vérifications restent fondées sur des données
synthétiques, sans reprendre les personnes ou les tickets des captures.

Les régressions de réception distinguent les lignes réellement reçues de la
concordance avec l’affichage GLPI. Un reçu complet avec des réserves conserve
ses comptes exacts et ses sous-ensembles, avec une météo « relevé à vérifier »
et `Complete=false`. Le bilan conserve les observations et réserves. Une
ancienne archive partielle, des compteurs incohérents ou un total GLPI final
modifié n’acquièrent pas une réception complète par déduction.

Ces tests ne lancent aucune interface graphique et n’accèdent à aucun portail réel. Les opérations C# et pages WebView sont vérifiées séparément dans [AnalyticsBridgeChecks](../AnalyticsBridgeChecks), puis le pack natif dans [WindowsStartupChecks](../WindowsStartupChecks/README.md).
