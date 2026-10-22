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
- Bibliothèque et équipe : `PilotApplication.Library*.cs` et `PilotApplication.SharedWorkspace.cs`, page `library.js`, moteurs `TicketLibrary*` et `TeamWorkspace*`. `RegisterFoundationOperations()` enregistre ce domaine après les opérations du socle.

Les sources WPF sont retirées du projet et de cette arborescence. Leur état avant migration reste dans le commit historique `d9c6434b462bb3ad700a206c93c70fd52142bd5e`. Aucun écran de la version 3 ne dépend de WPF.

## Bibliothèque locale et partage central — 3.2

Le cache local SQLite et la base PostgreSQL commune possèdent des protocoles versionnés. Les accès aux fichiers, transactions, versions et connexions restent en C#. Les pages transmettent des objets bornés ; elles ne reçoivent ni chaîne de connexion ni secret mémorisé.

| Famille d’opérations | Contrat |
| --- | --- |
| `library.get`, `library.search`, `library.ticket`, `library.conflicts` | État, recherche paginée, contenu du ticket et aperçus bornés des versions concurrentes. La recherche de solutions ne retient que les contenus complets et actuels de tickets résolus ou clos. |
| `library.capture` | `mode: incremental` réutilise les contenus dont la version est prouvée inchangée ; `mode: all` relit les contenus. Le bilan expose périmètre, limites, lectures, réutilisations et échecs. |
| `library.settings.save`, `library.folder.pick`, `library.connection.test` | Réglages propres au poste, dossier local et essai de connexion sans création de schéma. Le mot de passe saisi n’est jamais renvoyé. |
| `library.sync`, `library.tick` | Échange explicite ou périodique lorsque l’application est ouverte ; file durable et reprise après interruption. Le tick évite les tâches au premier plan et la session verrouillée. |
| `library.loadRecords`, `library.export`, `library.open` | Relevé local marqué comme tel, corpus JSONL avec citations et ouverture du ticket d’origine. Plusieurs installations ne sont pas fusionnées sans sélection de leur contexte. |
| `workspace.list`, `workspace.get`, `workspace.save`, `workspace.remove`, `workspace.conflicts` | Liste paginée avec aperçu, lecture complète avant édition, mutations exigeant `expectedRevision`, retraits versionnés et conservation des conflits. |
| `workspace.publishConfiguration`, `workspace.configuration.preview` | Publication des indicateurs, dashboards, requêtes et villes ; prévisualisation puis application par le jeton existant de `configuration.apply`. |
| `workspace.publishStatistics`, `workspace.export` | Publication d’un état calculé et export des contributions. Les paramètres de connexion, le profil SSO et les préférences personnelles n’entrent pas dans ces documents. |
| `workspace.publishPersonalNote`, `workspace.publishTour`, `workspace.publishDispatch`, `workspace.publishTechnicians` | Publication explicite du document enregistré dans l’outil concerné : note de ticket, tournée, scénario de répartition ou compétences et capacités. Types de transport dédiés, installation et références validées. |
| `workspace.operation.preview`, `workspace.operation.apply` | Comparaison avec le document local, puis application avec un jeton à usage unique lié à la contribution, au contexte et à l’empreinte du fichier local. Le texte d’une note est ajouté ; une tournée devient une nouvelle copie brouillon ; un scénario ou des capacités remplacés disposent d’une sauvegarde locale. |

Les dates locales GLPI nécessitent un `sourceTimeZone` explicite. `sourceDateOrder` vaut `dmy`, `mdy`, `ymd` ou une chaîne vide ; sans choix, une date jour/mois ambiguë ne permet pas de conclure qu’un ticket est inchangé. Les heures ambiguës ou inexistantes au changement d’heure ne deviennent pas des instants inventés. Une réinterprétation d’un même texte de date ne constitue pas une nouvelle version GLPI.

L’événement `library.changed` invalide les vues de bibliothèque. Les publications de progression ne contiennent pas de titre, d’identifiant de ticket ou de contenu. Les pages conservent le bilan renvoyé après collecte, y compris lorsque l’opération démarre depuis la page Collecte GLPI.

Un relevé issu de `ticket-library` conserve l’URL de liste de son installation et la colonne ID canonique dans ses réglages effectifs. Ces réglages de lecture ne modifient pas le portail actif de l’application. L’ouverture du lecteur charge les détails disponibles dans SQLite et suspend la lecture automatique GLPI pour ce relevé. Les opérations GLPI explicites conservent leurs contrôles de source ; une copie locale n’autorise pas une écriture dans le portail.

Les documents opérationnels utilisent `glpi-pilot.business-document`, format 1, avec des structures distinctes pour `personal-note`, `tour`, `dispatch` et `technicians`. Les champs inconnus sont refusés. Les favoris, objectifs personnels, notes de technicien et notes de disponibilité sont exclus du transport de capacités et conservés à la réception. Les tournées reçues ne reprennent pas une validation d’exécution, les trajets ou la météo d’un autre poste. La réception de ces documents ne lance aucune action métier GLPI.

## Navigation et thème Domenia — 3.2

Les identifiants de route restent stables. Les préférences personnelles pilotent `NavigationOrder`, `HiddenNavigation`, `PinnedNavigation`, `NavigationGroupOrder` et `NavigationGroups`. Les clés de groupe sont `pinned`, `workspace`, `collect`, `analyze`, `work`, `terrain` et `admin`. Le C# normalise les identifiants et garde `home` et `settings` visibles. Une page masquée reste ouvrable dans la palette de commandes ; masquer son raccourci ne désactive pas une capacité métier.

Le thème adapte les fichiers `v2/web/css/tokens.css`, `motion.css`, `components.css` et `shell.css` de Domenia 2 : palette bleu/violet/turquoise/rose, surfaces translucides, fond aurora et transitions. Les ressources restent locales et la CSP de production est conservée. Les effets respectent la réduction des animations de l’application ou du système, le contraste renforcé et la suspension lorsque le document est caché. Les animations décoratives ne pilotent ni la logique métier ni les déplacements de widgets.


## Révision et cycle de vie

Les formulaires et mutations de données transmettent la révision reçue lors de leur ouverture. Le runtime refuse une révision remplacée ; le navigateur GLPI possède aussi une génération de sélection d’onglet/tableau distincte. Les résultats chargés gardent une copie de leurs règles. Un changement de recherche invalide un relevé GLPI actif ; il ne réinterprète pas un import ou une archive avec les règles d’un autre profil.

`operations.cancel` est une opération enregistrée qui contourne le sémaphore métier pour annuler les travaux admis, y compris les collectes programmées. Les autres opérations sont sérialisées. `DisposeAsync` ferme l’admission, annule puis draine les tâches avant de libérer le navigateur et les ressources.

La fermeture native émet `app.beforeClose` avec un nonce ; la page annule les travaux, attend son cleanup asynchrone (sauvegarde des brouillons), puis répond `app.closeReady`. Une erreur ou un délai de 15 secondes sans confirmation maintient la fenêtre ouverte. Le démarrage n’est acquitté qu’après navigation locale, bootstrap C# et première peinture confirmée par la page.

## Environnement Windows et profil de test

Le pack impose `wad/runtime/webview2/msedgewebview2.exe`. La vue locale est associée à `https://glpi-pilot.local` ; aucune ressource externe n’est admise. La CSP refuse le code inline et eval. Les styles dynamiques passent par les propriétés CSSOM des éléments.

Le port CDP de vérification est disponible uniquement avec `GLPI_PILOT_TEST_MODE=1`, une racine temporaire `GLPI-Pilot-Checks-*` sans lien et `--test-port`. Les harnais créent et nettoient leurs propres profils ; ils ne déplacent pas le profil réel. Les variables externes de démarrage WebView sont retirées avant de créer le moteur.

Référence d’infrastructure lue : [REPRISE.md](https://github.com/M4D135/domenia-d120/blob/v2/v2/REPRISE.md), branche v2, 22 octobre 2026. VM Windows 11 4 vCPU/8 Go, SDK/cache locaux et session de bureau à préserver. La CI Windows publique et cette VM privée sont deux environnements distincts.
