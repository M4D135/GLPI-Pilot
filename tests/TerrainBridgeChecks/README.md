# Opérations terrain et contexte du relevé

Cette suite compile toutes les sources `Core` et `Runtime` de production. Seuls le chargement initial des données synthétiques et les dialogues de fichiers natifs sont remplacés par une fixture. Les commandes s’exécutent par `PilotApplication.ExecuteAsync`, avec la sérialisation, les révisions, les règles de contexte et la persistance réellement utilisées par l’application.

```sh
dotnet run --project tests/TerrainBridgeChecks/TerrainBridgeChecks.csproj -c Release
```

Les contrôles couvrent les brouillons du référentiel, simulations avant application, import/export CSV, corrections locales motivées, paniers, projections, tournées, capacités et contraintes, exports ICS/HTML, consentement des services et préparation des mises à jour. Ils utilisent un profil temporaire explicitement isolé ; aucun portail GLPI réel n’est consulté ou modifié.

Le scénario d’aperçu périmé passe par **`mapping.save`**, après création d’un relevé synthétique dont la source et la portée sont déclarées vérifiées. Il exige que la commande change les règles effectives et la révision sans remplacer les cellules extraites, puis que l’aperçu géographique préparé avant cette modification soit refusé. Une affectation directe à `Settings` ne simule pas ce parcours et ne doit pas remplacer la commande publique.

## Parcours des pages

Le mode `--serve` fournit le protocole stdio utilisé par [TerrainWebChecks](../TerrainWebChecks/run.cjs). Ce mode n’expose que les familles `terrain.*` et `update.*`, et refuse explicitement les commandes réseau et le lancement d’une installation. Les pages HTML/CSS/JavaScript de production communiquent ainsi avec les véritables handlers C# sur données synthétiques.

Les résultats et nombres de contrôles du commit livré figurent dans les journaux de [la chaîne Windows](../../.github/workflows/windows.yml). Ces tests portables ne remplacent pas le démarrage du pack Windows et de son runtime WebView2 fixe.
