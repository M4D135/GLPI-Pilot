# QueryChecks

Tests BCL sur relevés synthétiques. Aucun navigateur, accès GLPI, SQL, interpréteur d’expression ou requête réseau.

```bash
dotnet run --project tests/QueryChecks/QueryChecks.csproj
```

Le constructeur combine des conditions typées et une cohorte native de lignes. Le champ sémantique reprend les correspondances du dashboard ; une colonne exacte doit être unique. Un champ nécessaire absent ou ambigu rend la requête indisponible, même dans une condition OU ou avec un périmètre vide.

Les textes utilisent une comparaison littérale insensible à la casse, avec espaces de bord ignorés. Les opérateurs numériques acceptent un point ou une virgule décimale et des espaces de groupement de milliers. Ils refusent les séparateurs concurrents, unités et nombres non finis. Les dates utilisent la culture du dashboard et son instant `CalculatedAt` ; les fenêtres de jours sont glissantes et inclusives aux bornes. En mode ET, toutes les conditions doivent être lisibles et vraies. En mode OU, une condition vraie suffit même si une autre valeur de cellule est illisible ; sans condition vraie, la ligne reste exclue. Les diagnostics distinguent les lignes avec une valeur illisible de celles qui n’ont pas été retenues. Une colonne nécessaire absente ou ambiguë bloque toujours la requête entière.

`QueryResult.Rows` contient les lignes retenues par les conditions. `Metric.Rows` contient les contributeurs de l’agrégat : valeurs non vides pour les valeurs distinctes, valeurs numériques lisibles pour les agrégats numériques. Une somme sans contribution reste inconnue. Les répartitions conservent le nombre de lignes retenues et ajoutent la valeur réelle de cet agrégat, son unité, son échantillon et les indices des contributeurs. Une moyenne par groupe est donc une moyenne ; elle ne devient jamais le nombre de lignes. Le pourcentage utilise le périmètre initial de chaque groupe comme dénominateur. Les techniciens multiples sont séparés avec les règles conservatrices partagées du dashboard ; un technicien ne reçoit jamais deux occurrences de la même ligne.

Les modèles par ville utilisent le rôle `city` configuré. La ville déduite du titre reste une inférence fondée sur les variantes saisies, sans géocodage ni reconnaissance approximative. Les trois modèles d’activité comptent les dates présentes dans le relevé courant et ne reconstituent pas un historique complet.

## Atelier avancé

Les anciennes requêtes ET/OU restent compatibles. L’expression facultative combine les numéros des conditions avec `ET`, `OU`, `NON` et des parenthèses, par exemple `1 ET (2 OU NON 3)`. Elle doit citer toutes les conditions ; une condition oubliée rend le brouillon invalide. Ce petit langage n’exécute aucun code. La profondeur est limitée à huit niveaux et l’expression à 1 024 caractères. Une donnée inconnue reste inconnue sous `NON`.

Chaque borne peut être une valeur littérale, un autre champ (`role:due`, `column:Date attendue`, `calc:delai`) ou un paramètre typé. `IN` et `NOT IN` séparent les choix par point-virgule ou saut de ligne ; une virgule peut donc rester dans le nom d’un technicien. Les intervalles numériques et de dates incluent leurs deux bornes. Les paramètres nommés possèdent une valeur par défaut enregistrée et une valeur temporaire pour l’aperçu ; la seconde ne modifie jamais la configuration partagée.

Trois niveaux de regroupement peuvent être combinés. Chaque niveau accepte une période de date : jour, semaine ISO, mois, trimestre ou année. Les périodes ne sont appliquées que lorsqu’elles sont explicitement choisies. Les dates vides et illisibles restent des catégories visibles. Une affectation à plusieurs techniciens peut contribuer à plusieurs groupes : ces groupes ne sont donc pas additifs.

Les agrégats disponibles sont le nombre, le pourcentage, les valeurs distinctes, la somme, la moyenne, le minimum, le maximum, la médiane et un percentile de 0 à 100. Médiane et percentile interpolent linéairement les valeurs numériques finies triées ; aucune valeur n’est inventée pour un groupe sans contribution lisible.

Les champs calculés proposent des fonctions bornées : addition, soustraction, multiplication, division, durée entre deux dates, ancienneté, longueur Unicode, minuscules, concaténation et valeur de remplacement. Les cycles et champs absents sont refusés. Une division par zéro produit une valeur inconnue ; une concaténation dépassant 100 000 caractères rend la requête indisponible avec une explication. Les durées sont exprimées en jours calendaires, sans déduction automatique des week-ends ou horaires de travail.

Le moteur explique un ticket par son identifiant GLPI ou `#n` pour sa ligne source (à partir de 1). Dans l’atelier WebView, le champ « Expliquer la ligne » transmet le numéro de ligne à l’opération C#. L’explication présente les valeurs observées, les comparaisons et la décision vraie, fausse ou indéterminée de chaque condition. Elle utilise les paramètres de la prévisualisation et le périmètre du relevé.

La bibliothèque contient 80 modèles, dont 16 nouveaux modèles d’âge et délai, regroupements hiérarchiques et filtres paramétrés. Les modèles nécessitant des paramètres ou colonnes absents restent explicitement indisponibles.

## Couverture et interface WebView

Les **99 vérifications** couvrent notamment les arbres logiques, la propagation des inconnus, les comparaisons entre champs, les paramètres sans mutation des valeurs enregistrées, les calculs bornés, les contributions par groupe, les semaines ISO, les percentiles, les séparateurs multitechniciens configurés, les packs et l’explication de ligne. Les sous-totaux sont recalculés sur les lignes uniques du préfixe, avec leur propre dénominateur ; les conflits d’import reçoivent un nouvel identifiant sans écraser les définitions existantes.

[AnalyticsBridgeChecks](../AnalyticsBridgeChecks) exerce les [opérations C# de l’atelier](../../src/GlpiPilot/Runtime/PilotApplication.Analytics.Queries.cs) et les [modules WebView de production](../../web/js/pages/analytics-queries.js) : prévisualisation, paramètres temporaires, sauvegarde, packs, sous-totaux et exports. Le [harnais Windows](../WindowsStartupChecks/README.md) vérifie séparément le véritable pack WinForms/WebView2. Sa compilation croisée seule ne prouve pas une exécution native réussie.
