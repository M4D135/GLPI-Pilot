# Vérification des exports

Depuis le dossier du projet, avec le SDK .NET 10 :

```powershell
dotnet run --project tests/ExportChecks/ExportChecks.csproj -c Release
```

Le programme utilise seulement les bibliothèques .NET. Il crée ses fichiers dans un dossier temporaire et le supprime à la fin.

Les vérifications portent sur les valeurs conservées dans le classeur Open XML, les identifiants à zéros initiaux, les caractères Unicode, les textes ressemblant à des formules, les métadonnées d’extraction partielle, les filtres et l’en-tête figé, les limites de longueur d’Excel, les paires Unicode, le CSV multiligne, la protection contre l’interprétation des formules et la conservation d’un fichier existant après un export en échec.

La version 1.1.2 ajoute un décodage SpreadsheetML indépendant des cellules pour vérifier la conservation des contrôles C0/C1, des retours chariot, des séquences littérales `_xHHHH_` et des combinaisons de celles-ci. Tous les composants XML sont relus avec un parseur strict. Les unités UTF-16 isolées restent retirées et signalées ; les caractères Unicode valides incompatibles avec XML sont désormais conservés par encodage. Six scénarios sont exécutés.

Référence primaire : [Microsoft MS-OE376, 2.1.1127 — ST_Xstring](https://learn.microsoft.com/en-us/openspecs/office_standards/ms-oe376/bd0aa042-434a-4ca7-b25f-4e1fd25a954d).

Le CSV est séparé par des points-virgules et encodé en UTF-8 avec BOM. Les cellules qui pourraient être interprétées comme des formules reçoivent une apostrophe initiale. Le XLSX conserve ces mêmes valeurs en cellules texte, sans apostrophe supplémentaire.

Ces vérifications ne remplacent pas un essai du véritable hôte WinForms/WebView2 sous Windows et du SSO sur le portail de l’utilisateur.
