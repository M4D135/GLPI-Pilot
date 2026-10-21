# QueryChecks

Tests BCL sur relevés synthétiques. Aucun navigateur, accès GLPI, SQL, interpréteur d’expression ou requête réseau.

```bash
dotnet run --project tests/QueryChecks/QueryChecks.csproj
```

Le constructeur combine des conditions typées et une cohorte native de lignes. Le champ sémantique reprend les correspondances du dashboard ; une colonne exacte doit être unique. Un champ nécessaire absent ou ambigu rend la requête indisponible, même dans une condition OU ou avec un périmètre vide.

Les textes utilisent une comparaison littérale insensible à la casse, avec espaces de bord ignorés. Les opérateurs numériques acceptent un point ou une virgule décimale et des espaces de groupement de milliers. Ils refusent les séparateurs concurrents, unités et nombres non finis. Les dates utilisent la culture du dashboard et son instant `CalculatedAt` ; les fenêtres de jours sont glissantes et inclusives aux bornes. En mode ET, toutes les conditions doivent être lisibles et vraies. En mode OU, une condition vraie suffit même si une autre valeur de cellule est illisible ; sans condition vraie, la ligne reste exclue. Les diagnostics distinguent les lignes avec une valeur illisible de celles qui n’ont pas été retenues. Une colonne nécessaire absente ou ambiguë bloque toujours la requête entière.

`QueryResult.Rows` contient les lignes retenues par les conditions. `Metric.Rows` contient les contributeurs de l’agrégat : valeurs non vides pour les valeurs distinctes, valeurs numériques lisibles pour les agrégats numériques. Une somme sans contribution reste inconnue. Les répartitions comptent les lignes retenues indépendamment de cet agrégat. Les techniciens multiples sont séparés avec les règles conservatrices partagées du dashboard ; un technicien ne reçoit jamais deux occurrences de la même ligne.

Les modèles par ville utilisent le rôle `city` configuré. La ville déduite du titre reste une inférence fondée sur les variantes saisies, sans géocodage ni reconnaissance approximative. Les trois modèles d’activité comptent les dates présentes dans le relevé courant et ne reconstituent pas un historique complet.
