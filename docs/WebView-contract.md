# Contrat de migration WebView — GLPI Pilot 3

Modèle étudié : M4D135/domenia-d120, branche v2, commit 048627387efdc9e44d4da77121cd82d92b2d28e9. Hôte WinForms minimal, WebView2 et interface locale ; règles et E/S en C#.

## C# partagé

`GlpiPilot.Runtime.PilotApplication` est une classe `public sealed partial`.
Le constructeur appelle `RegisterAnalyticsOperations()`, `RegisterWorkplaceOperations()` et `RegisterTerrainOperations()` ; chaque équipe définit sa méthode privée et ses handlers dans ses propres fichiers `Runtime/PilotApplication.<domaine>.cs`.

Membres communs fournis par le socle :

- `AppSettings Settings { get; private set; }`
- `ExtractionResult? Result { get; private set; }`
- `SnapshotDocument? HistoricalDocument { get; private set; }`
- `CityEnrichmentResult? Locations { get; private set; }`
- `BrowserService Browser { get; }`, `SnapshotStore Snapshots { get; }`, `BackgroundWorkScheduler Jobs { get; }`
- `IPilotHost Host { get; }`, `long Revision { get; private set; }`
- `void Register(string op, Func<JsonElement, CancellationToken, Task<object?>> handler)`
- `ExtractionResult RequireResult()` ; `void RequireCurrentResult()` refuse archives/démonstrations pour actions exigeant une source GLPI actuelle.
- `void SetResult(ExtractionResult data, CityEnrichmentResult? locations = null, SnapshotDocument? historical = null)` invalide les vues/calculs par révision et publie session.changed.
- `void SaveSettings()` valide/enregistre puis publie settings.changed.
- `string DataPath(string fixedName)` uniquement pour des noms constants de l’application.
- `void Publish(string eventName, object? data)` ; `void Log(string message)`.
- Helpers `static string Text(JsonElement args, string name, int max = 8192, string fallback = "")`, `static int Number(JsonElement args, string name, int fallback = 0, int min = 0, int max = 1000000)`, `static bool Flag(JsonElement args, string name, bool fallback = false)`, `static T Read<T>(JsonElement args)`.
- `public static JsonSerializerOptions Json` : camelCase, propriétés en lecture/écriture insensibles à la casse ; enums numériques conformes aux modèles existants. Les handlers valident leurs modèles avant mutation.

`IPilotHost` : `Task<string?> OpenFileAsync(string title, string filter)`, `Task<string?> SaveFileAsync(string title, string filter, string suggestedName)`, `Task<string?> PickFolderAsync(string title)`, `Task SetClipboardAsync(string text)`, `Task OpenExternalAsync(string url)`, `Task CloseAsync()`, `Task<byte[]> CapturePngAsync()`, `Task PrintPdfAsync(string path)`, `Task<bool> NotifyAsync(string title, string message)`.

Les opérations sont fermées/enregistrées. Exécution sérialisée hors du thread de rendu ; annulation par identifiant de requête dans le pont, progression via événements. Les événements ne doivent jamais contenir de secret. Le système ne passe ni méthode CLR arbitraire ni JavaScript reçu au moteur.

Chaque handler retourne un objet sérialisable borné ; ne pas renvoyer de contexte Dashboard complet avec fonctions/cycles. Les moteurs C# restent responsables des calculs, de la pagination et des validations. Un événement `session.changed` invalide les résultats UI ; les pages abandonnées utilisent leur AbortSignal.

## Pages web

Chaque groupe exporte depuis `web/js/pages/<groupe>.js` :

```js
export const pages = [
 { id: 'dashboard', title: 'Dashboard', group: 'Analyser', icon: 'chart',
   async render(ctx) { /* DOM via ctx.root ; retourne éventuellement cleanup() */ } }
];
```

`ctx` : `{ root, api, state, signal, navigate, notify, refresh }`.
`api.call(op,args={}, {signal}={})` retourne la réponse métier ; `api.on(event,handler)` retourne une désinscription. `state` contient le bootstrap (settings, session, version, capabilities). `navigate(id, params={})` : la nouvelle page reçoit `ctx.params`. `refresh()` recharge la page courante.

Les pages peuvent importer depuis `../ui.js` :

- `el(tag, attrs = {}, ...children)` : classes via class, texte via text, événements via onClick/onChange ; aucun HTML brut.
- `button(label, handler, options = {})` : options `{primary,danger,disabled,title,icon}`.
- `card(title, ...children)`, `toolbar(...children)`, `empty(title, detail = '')`, `badge(text, tone = '')`.
- `input(value = '', options = {})`, `select(options, value = '')` (`options = [{value,label}]`), `field(label, control, help = '')`.
- `table(rows, columns, options = {})` : colonnes `{key,label,render(row),width}` ; options `{onSelect,key,selected,emptyText,maxRows}`. Rend des lignes bornées et sélection clavier.
- `form(fields, values = {})` : retourne `{element, controls, read()}` ; champs `{key,label,type,options,help,min,max,required}` ; type text/number/checkbox/select/textarea/date/time/password.
- `dialog(title, content, actions = [])` : retourne `{close, element}` ; actions `{label, primary, danger, action(close)}`.
- `confirm(title,message, confirmLabel = 'Confirmer')` retourne Promise<boolean>.
- `formatNumber`, `formatDate`, `showError(root,error)`.

Les erreurs métier sont affichées ; pas de catch silencieux ni succès simulé. Les grandes données restent paginées. Les formulaires réels priment sur les éditeurs JSON ; JSON est réservé à import/export et diagnostics techniques explicitement nommés.

## Répartition

- Analytics : `PilotApplication.Analytics*.cs`, pages `analytics.js` et CSS analytique dédié. Dashboard/éditeur/widgets, requêtes, archives, analyses, taux/cartes, rapports et assistant de langage. Définit `RegisterAnalyticsOperations()`.
- Workplace : `PilotApplication.Workplace*.cs`, pages `workplace.js` et CSS dédié. Tickets/lecteur/actions/documents, journée, enrichissement, techniciens/calendriers/scénarios. Définit `RegisterWorkplaceOperations()`.
- Terrain : `PilotApplication.Terrain*.cs`, pages `terrain.js` et CSS dédié. Villes/règles/corrections/simulations, tournées/routage/météo/exports, assistant de mise à jour. Définit `RegisterTerrainOperations()`.
- Socle : projet/hôte/pont, `PilotApplication.cs`, collecte/relevé/configuration/automatisations/alertes, shell/UI commune, empaquetage/CI.

Les sources WPF sont retirées du projet et de cette arborescence. Leur état avant migration reste dans le commit historique `d9c6434b462bb3ad700a206c93c70fd52142bd5e`. Aucun écran de la version 3 ne dépend de WPF.


## Révision et cycle de vie

Les formulaires et mutations de données transmettent la révision reçue lors de leur ouverture. Le runtime refuse une révision remplacée ; le navigateur GLPI possède aussi une génération de sélection d’onglet/tableau distincte. Les résultats chargés gardent une copie de leurs règles. Un changement de recherche invalide un relevé GLPI actif ; il ne réinterprète pas un import ou une archive avec les règles d’un autre profil.

`operations.cancel` est une opération enregistrée qui contourne le sémaphore métier pour annuler les travaux admis, y compris les collectes programmées. Les autres opérations sont sérialisées. `DisposeAsync` ferme l’admission, annule puis draine les tâches avant de libérer le navigateur et les ressources.

La fermeture native émet `app.beforeClose` avec un nonce ; la page annule les travaux, attend son cleanup asynchrone (sauvegarde des brouillons), puis répond `app.closeReady`. Une erreur ou un délai de 15 secondes sans confirmation maintient la fenêtre ouverte. Le démarrage n’est acquitté qu’après navigation locale, bootstrap C# et première peinture confirmée par la page.

## Environnement Windows et profil de test

Le pack impose `wad/runtime/webview2/msedgewebview2.exe`. La vue locale est associée à `https://glpi-pilot.local` ; aucune ressource externe n’est admise. La CSP refuse le code inline et eval. Les styles dynamiques passent par les propriétés CSSOM des éléments.

Le port CDP de vérification est disponible uniquement avec `GLPI_PILOT_TEST_MODE=1`, une racine temporaire `GLPI-Pilot-Checks-*` sans lien et `--test-port`. Les harnais créent et nettoient leurs propres profils ; ils ne déplacent pas le profil réel. Les variables externes de démarrage WebView sont retirées avant de créer le moteur.

Référence d’infrastructure lue : [REPRISE.md](https://github.com/M4D135/domenia-d120/blob/v2/v2/REPRISE.md), branche v2, 22 octobre 2026. VM Windows 11 4 vCPU/8 Go, SDK/cache locaux et session de bureau à préserver. La CI Windows publique et cette VM privée sont deux environnements distincts.
