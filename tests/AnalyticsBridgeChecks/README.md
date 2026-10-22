# Vérifications du domaine analytique WebView

Cette suite exécute les moteurs C#, les handlers `PilotApplication.Analytics*`
et le `WebBridge` de production. Les jeux de tickets sont synthétiques ; les
calculs, la persistance, les archives et les réponses du pont ne sont pas
remplacés par des simulations.

## Exécution

Le projet cible .NET 10 et référence Microsoft.Playwright comme les autres
suites navigateur du dépôt.

```sh
dotnet run --project tests/AnalyticsBridgeChecks/AnalyticsBridgeChecks.csproj
dotnet run --project tests/AnalyticsBridgeChecks/AnalyticsBridgeChecks.csproj -- --web
```

La première commande vérifie les handlers et l’historique sans ouvrir de
navigateur. La seconde ajoute les parcours Chromium des vrais modules
`web/js/pages/analytics*.js` et de leurs CSS. Installer le navigateur Playwright
du projet ou définir `GLPI_TEST_CHROMIUM` vers un exécutable Chromium existant.
Une option facultative conserve une capture après le parcours :

```sh
dotnet run --project tests/AnalyticsBridgeChecks/AnalyticsBridgeChecks.csproj -- --web --screenshots /tmp/glpi-analytics-ui
```

Le test crée un profil explicite `GLPI-Pilot-Checks-Analytics-*` sous le
répertoire temporaire, active `GLPI_PILOT_TEST_MODE` pour ce seul processus,
puis nettoie le profil. Aucun profil navigateur de l’utilisateur, fournisseur
d’IA réel ou service cartographique n’est utilisé. Le navigateur n’accepte que
l’origine locale du harnais ; toute tentative externe fait échouer le contrôle.

## Ce qui est vérifié

- Révisions et jetons de calcul, détail sur les indices originaux, refus de
  résultats périmés, annulation et restauration fidèle au démarrage.
- Dispositions globales/personnelles, annuler/rétablir persistants, catalogue
  et disponibilité effective, édition d’un widget personnel.
- Mini-historiques globaux, filtres communs et propres, identité personnelle
  recalculée après réaffectation et changement d’ordre des lignes. Les tests
  distinguent le zéro observé des données incompatibles, partielles ou absentes,
  et vérifient cache, annulation, limite de 64 périmètres et budget cumulé.
- Matrice à deux axes, retour aux lignes contributrices, dénominateurs des
  empilements, courbe de Pareto et mise en page responsive sous CSP stricte.
- Requêtes typées, aperçu sans écriture, sous-totaux, export intégral CSV/XLSX,
  packs en aperçu puis validation explicite.
- Comparaisons d’archives, absence distincte d’une résolution, références
  sûres des absents, calendrier et historique d’un ticket indexé.
- Carte à coordonnées déclarées, sélection des actifs, inventaire daté,
  import avec aperçu, concurrence optimiste et dénominateurs d’archive figés.
- Rapport HTML avec tickets/provenance et captures PDF/PNG réellement produites
  par Chromium après fermeture du dialogue ; refus d’un hôte incapable de
  produire un export au lieu d’un succès simulé.
- Assistant local : contenu exact avant génération, DSL validé, aperçu et
  brouillon sans ajout implicite, synthèses à citations contrôlées, refus d’un
  appel externe sans consentement et invalidation après changement de source.

Le harnais remplace uniquement les frontières natives : choix déterministes
des chemins de fichiers et API de capture/impression Chromium. Il ne vérifie
pas les dialogues Windows ni le démarrage du runtime fixe WebView2. Ces points
relèvent des suites natives Windows. `WebViewChecks` complète ce projet en
ouvrant le shell intégral et les autres domaines de l’application.

## Opérations exposées par le domaine

Les 50 noms ci-dessous sont enregistrés dans le dispatcher fermé. Les appels
de lecture de lignes et les exports d’un calcul portent son identifiant et sa
révision. Les imports et l’assistant séparent préparation et action explicite.

| Domaine | Opérations |
| --- | --- |
| Dashboard (12) | `dashboard.get`, `dashboard.history`, `dashboard.catalog`, `dashboard.layout.get`, `dashboard.layout.save`, `dashboard.layout.undo`, `dashboard.layout.redo`, `dashboard.board.create`, `dashboard.layout.export`, `dashboard.layout.import`, `dashboard.rows`, `dashboard.data` |
| Requêtes (14) | `queries.list`, `queries.get`, `queries.fields`, `queries.preview`, `queries.results`, `queries.results.export`, `queries.rows`, `queries.explain`, `queries.save`, `queries.delete`, `queries.export`, `queries.import.preview`, `queries.import.commit`, `queries.widget.add` |
| Historique (12) | `history.list`, `history.open`, `history.export`, `history.archive`, `history.protect`, `history.compare`, `history.changes`, `history.absent.references`, `history.analyze`, `history.analysis.page`, `history.analysis.rows`, `history.ticket` |
| Effectifs (6) | `populations.list`, `populations.upsert`, `populations.delete`, `populations.import.preview`, `populations.import.commit`, `populations.export` |
| Assistant (5) | `assistant.config.get`, `assistant.config.save`, `assistant.prepare`, `assistant.generate`, `assistant.draft` |
| Rapports (1) | `reports.export` |

Les pages sont `dashboard` (global et personnel via `technicianKey`), `queries`,
`history`, `populations` et `assistant`. Les liens vers un ticket utilisent
`navigate('tickets', { rowIndex, revision })`. La vérification des absents
prépare une file `workplace.enrichment.prepare` sans la lancer.
