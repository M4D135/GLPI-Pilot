# Import du CSV natif GLPI

```powershell
dotnet run --project tests/NativeImportChecks/NativeImportChecks.csproj -c Release
```

Les 34 contrôles couvrent le CSV téléchargé avant sa conversion automatique en Excel : UTF-8 avec ou sans BOM, UTF-16 LE/BE avec BOM, repli Windows-1252 signalé, séparateurs point-virgule/virgule/tabulation, guillemets doubles, champs multilignes, en-têtes uniques, doublons de données, séparateur terminal GLPI 10, limites d’import, erreurs de format, page de connexion HTML et conservation des valeurs texte jusqu’au XLSX. Les régressions de la version 1.1.2 couvrent également les contrôles C0/C1 dans les descriptions, les diagnostics sans contenu privé et un fichier synthétique de 831 lignes dépassant un mégaoctet.

## API

```csharp
ExtractionResult NativeCsvImportService.Read(
    string path,
    string sourceUrl,
    int? expectedTotal = null,
    int maxRows = 1_000_000,
    bool allPagesRequested = true,
    CancellationToken cancellationToken = default,
    Action<string>? diagnostics = null);
```

Le nombre de lignes du fichier ne devient jamais le total attendu de GLPI. `Complete` exige un choix toutes-pages confirmé, un total GLPI connu et concordant et l’absence de limite atteinte. Le nombre de pages parcourues vaut zéro pour cet import d’un fichier natif.

Toutes les lignes sont analysées même quand la limite d’aperçu est atteinte : le statut indique le nombre conservé et le nombre réel de lignes dans le fichier. Une erreur plus loin dans le CSV reste une erreur. Le fichier téléchargé est ouvert en lecture seule et n’est jamais modifié.

Seules les lignes physiques sans aucun caractère à l’extérieur des champs sont ignorées. Les cellules vides explicites, les paragraphes vides à l’intérieur d’un champ et les lignes de données identiques sont conservés. Une colonne finale est retirée uniquement si son en-tête est vide et toutes ses valeurs dans le fichier le sont aussi, pour couvrir le séparateur terminal de GLPI 10.

L’import refuse une structure ambiguë ou malformée au lieu de deviner et de déplacer des valeurs entre colonnes. La directive optionnelle `sep=;`, `sep=,` ou `sep=` suivie d’une tabulation est reconnue. Aucun autre préambule n’est ignoré automatiquement.

Le fichier est limité à 512 Mio et chaque champ à 16 Mio de caractères pour éviter une allocation non bornée. La valeur de `maxRows` doit être comprise entre 1 et 1 000 000. Le CSV natif demeure disponible si l’import est refusé. Ces limites ne remplacent pas `maxRows`.

## Caractères de contrôle et diagnostics — 1.1.2

La version 1.1.1 refusait tout caractère classé `char.IsControl` à l’exception de TAB, CR et LF. Cette règle rejetait aussi du texte contenant un caractère C1 ou un contrôle de mise en page. La version 1.1.2 conserve ces caractères dans les valeurs importées et ajoute un avertissement indiquant leur code Unicode, leur nombre et leur première position. Elle ne remplace pas les caractères, ne normalise pas les fins de ligne et ne réécrit jamais le fichier téléchargé. La conversion XLSX possède ses propres règles de représentation et avertissements ; elle ne doit pas modifier le résultat importé en mémoire.

Le callback facultatif `diagnostics` reçoit uniquement des informations structurelles : taille du fichier, encodage choisi, présence et longueur du BOM, recours éventuel à Windows-1252, séparateur, lignes de préambule, histogramme des contrôles inhabituels, nombres de lignes et de colonnes avant/après la colonne terminale GLPI, total attendu et état complet/partiel. Un histogramme ressemble à `U+0085×2 (premier offset 40960)`. Les offsets sont comptés en unités UTF-16, depuis le premier caractère après le BOM, avec une base de zéro ; ce ne sont pas des offsets d’octets. TAB, CR et LF ne sont pas des contrôles inhabituels.

Le diagnostic ne contient ni extrait du fichier, ni en-tête, ni valeur de cellule, ni nom de fichier, ni URL. L’histogramme est réparti en groupes de dix codes maximum par ligne, sous la limite de 1 800 caractères du journal ; aucun code n’est perdu quand de nombreux contrôles différents sont présents. Une erreur du décodeur n’est pas jointe comme exception interne, car sa représentation peut afficher les octets d’entrée.

Les limites de reconnaissance restent explicites :

- Un caractère NUL U+0000 provoque un refus avec son nombre et sa première position. Aucun encodage UTF-16 sans BOM n’est deviné à partir de NUL alternés ; le format natif GLPI consulté émet du UTF-8 avec BOM. Le UTF-16 LE/BE muni d’un BOM reste pris en charge.
- Les signatures de fichiers binaires reconnues et les contenus HTML/XML/JSON restent refusés, y compris après un long préambule de blancs ou de contrôles de mise en page.
- Un contenu dominé par des contrôles non textuels reste refusé : au moins 32 de ces contrôles et plus de 25 % des unités UTF-16. VT U+000B, FF U+000C et NEL U+0085 sont exclus de cette heuristique de densité, car ils peuvent servir à la mise en page. Ils restent comptés dans l’avertissement.
- Une séquence d’encodage invalide avec BOM, des guillemets incohérents, une largeur de ligne incorrecte ou un séparateur ambigu restent des erreurs. L’assouplissement des contrôles ne répare pas un CSV malformé.

Le CSV utilisateur qui a déclenché le correctif n’est pas inclus dans les tests et n’a pas été examiné. Le caractère exact à l’origine de son refus demeure inconnu. Le fichier de 831 lignes est une régression synthétique, pas une copie de ses données.

Référence de classification : https://learn.microsoft.com/en-us/dotnet/api/system.char.iscontrol?view=net-10.0

## Formats GLPI vérifiés dans le code officiel

- GLPI 10.0.18 : `src/Search.php`, méthodes `showHeader`, `showHeaderItem`, `showItem` et `showEndLine` : BOM UTF-8, séparateur de session après chaque cellule, en-têtes directement suivis des données.
- GLPI 11, branche `11.0/bugfixes` : `src/Glpi/Search/Output/Csv.php` et `Spreadsheet.php` : CSV PhpSpreadsheet, séparateur configuré, guillemets doubles, BOM, CRLF, en-têtes en première ligne.

Sources :

- https://github.com/glpi-project/glpi/blob/10.0.18/src/Search.php
- https://github.com/glpi-project/glpi/blob/11.0/bugfixes/src/Glpi/Search/Output/Csv.php
- https://github.com/glpi-project/glpi/blob/11.0/bugfixes/src/Glpi/Search/Output/Spreadsheet.php

Aucun SSO réel n’est sollicité par ces tests.
