# Contrat WebView — GLPI Pilot 3.4

Architecture : hôte WinForms minimal, WebView2 et interface locale ; règles et E/S en C#. Le modèle d’architecture initial provient de M4D135/domenia-d120, branche v2, commit 048627387efdc9e44d4da77121cd82d92b2d28e9. La refonte visuelle 3.4 étudie également l’Accueil et les styles de la branche v2 au commit 54b071d8cdecaa387a67d6c1a1e620af4674f2ba.

## C# partagé

`GlpiPilot.Runtime.PilotApplication` est une classe `public sealed partial`.
Le constructeur appelle `RegisterFoundationOperations()`, `RegisterAnalyticsOperations()`, `RegisterWorkplaceOperations()` et `RegisterTerrainOperations()` ; chaque équipe définit sa méthode privée et ses handlers dans ses propres fichiers `Runtime/PilotApplication.<domaine>.cs`.

Membres communs fournis par le socle :

- `AppSettings Settings { get; private set; }`
- `ExtractionResult? Result { get; private set; }`
- `SnapshotDocument? HistoricalDocument { get; private set; }`
- `CityEnrichmentResult? Locations { get; private set; }`
- `BrowserService Browser { get; }`, `SnapshotStore Snapshots { get; }`, `BackgroundWorkScheduler Jobs { get; }`
- `IPilotHost Host { get; }`, `long Revision { get; private set; }`
- `void Register(string op, Func<JsonElement, CancellationToken, Task<object?>> handler)`
- `ExtractionResult RequireResult()` ; `void RequireCurrentResult()` vérifie le contexte requis par l’action. Les opérations de l’instance de simulation utilisent leurs adaptateurs locaux ; les actions GLPI ordinaires exigent leur propre source actuelle vérifiable.
- `void SetResult(ExtractionResult data, CityEnrichmentResult? locations = null, SnapshotDocument? historical = null)` invalide les vues/calculs par révision et publie session.changed.
- `void SaveSettings()` valide/enregistre puis publie settings.changed.
- `string DataPath(string fixedName)` uniquement pour des noms constants de l’application.
- `void Publish(string eventName, object? data)` ; `void Log(string message)`.
- Helpers `static string Text(JsonElement args, string name, int max = 8192, string fallback = "")`, `static int Number(JsonElement args, string name, int fallback = 0, int min = 0, int max = 1000000)`, `static bool Flag(JsonElement args, string name, bool fallback = false)`, `static T Read<T>(JsonElement args)`.
- `public static JsonSerializerOptions Json` : camelCase, propriétés en lecture/écriture insensibles à la casse ; enums numériques conformes aux modèles existants. Les handlers valident leurs modèles avant mutation.

`IPilotHost` : `Task<string?> OpenFileAsync(string title, string filter)`, `Task<string?> SaveFileAsync(string title, string filter, string suggestedName)`, `Task<string?> PickFolderAsync(string title)`, `Task SetClipboardAsync(string text)`, `Task OpenExternalAsync(string url)`, `Task CloseAsync()`, `Task<byte[]> CapturePngAsync()`, `Task PrintPdfAsync(string path)`, `Task<bool> NotifyAsync(string title, string message)`.

Les opérations sont fermées/enregistrées. Exécution sérialisée hors du thread de rendu ; annulation par identifiant de requête dans le pont, progression via événements. Les événements ne doivent jamais contenir de secret. Le système ne passe ni méthode CLR arbitraire ni JavaScript reçu au moteur.

Chaque handler retourne un objet sérialisable borné ; ne pas renvoyer de contexte Dashboard complet avec fonctions/cycles. Les moteurs C# restent responsables des calculs, de la pagination et des validations. Un événement `session.changed` invalide les résultats UI ; les pages abandonnées utilisent leur AbortSignal.

## Six espaces web, outils contextuels

Le registre visible du shell contient exactement six destinations. Ce sont des compositions propres ; les outils détaillés utilisent des panneaux contextuels dans l’espace concerné.

| Identifiant stable | Libellé | Composition |
| --- | --- | --- |
| `home` | Accueil | Priorités, alertes, rendez-vous et reprises de travail. |
| `pilot` | Pilotage | Composition de météo, activité, vigilance, groupes, catégories et participants ; édition et exports. |
| `records` | Tickets | Liste source et lecteur adjacent, notes personnelles, observations et outils du ticket. |
| `terrain-map` | Terrain | Carte et tickets filtrés, affectations observées, visites prévues, référentiel et tournées. |
| `data` | Données | Import, collecte, contrôle de qualité, correspondances et bibliothèque. |
| `settings` | Réglages | Présentation, pilotage, connexions, conservation, automatisations, entretien et options. |

Les modules exportent leurs entrées depuis `web/js/pages/<groupe>.js` :

```js
export const pages = [
 { id: 'terrain-map', title: 'Terrain', group: 'Terrain', icon: 'map',
   async render(ctx) { /* DOM via ctx.root ; retourne éventuellement cleanup() */ } }
];
```

`ctx` : `{ root, api, state, signal, params, navigate, notify, refresh }`.
`api.call(op,args={}, {signal}={})` retourne la réponse métier ; `api.on(event,handler)` retourne une désinscription. `state` contient le bootstrap (settings, session, version, capabilities). `navigate(id, params={})` : la nouvelle page reçoit `ctx.params`. `refresh()` recharge la page courante.

Les anciens identifiants restent des aliases de `navigationTarget(id, params)`, définis dans `web/js/navigation.js`. Ils ne créent ni entrée de menu supplémentaire, ni ancienne vue. Les paramètres fournis restent prioritaires sur les valeurs par défaut de l’alias.

| Anciens liens | Destination et contexte |
| --- | --- |
| `pilot-weather`, `pilot-summary`, `pilot-groups`, `pilot-categories`, `pilot-oldest`, `pilot-followup`, `pilot-participants` | `pilot` avec `focusSection` vers la section concernée. |
| `dashboard`, `queries`, `history`, `technicians`, `planning`, `assistant` | Composition ou outil contextuel de `pilot`. |
| `tickets` | `records` ; `rowIndex`, `revision`, `ticketKey` et la section demandée sont conservés. |
| `collect`, `sources`, `quality`, `mapping`, `library`, `enrichment` | Panneau de `data`. Les paramètres de recherche ou de bibliothèque restent transmis. |
| `pilot-sites`, `terrain-reference`, `terrain-review`, `populations` | `terrain-map`, panneaux `sites`, `reference`, `review`, `populations`. |
| `tours` | Panneau `tours` de Terrain, ou `planning` lorsqu’un `tourId` / `openDraft` est fourni. |
| `pilot-settings`, `automation`, `updates`, `operations`, `help` | Panneau de `settings`. |
| `day`, `alerts` | Outil de priorités ou d’alertes dans `home`. |

Les commandes ouvrant un outil exposent `data-open-panel`. Le dialogue résultant expose `data-workspace-panel`. `createWorkspacePanels(ctx)` gère les montages génériques ; Terrain ajoute les vérifications propres au brouillon de tournée et au renouvellement de la carte. Une fermeture attend le cleanup asynchrone utile avant de détruire le contexte. Les erreurs restent dans le dialogue concerné.

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

## Moteurs et composants

### Périmètre d’équipes et météo — 3.4.1

`team-scope.js` compose le catalogue unique depuis les équipes configurées et les groupes observés. Les options de même ensemble de groupes partagent leurs clés d’alias. La comparaison suit `OrdinalIgnoreCase` du moteur, sans normalisation des accents ou de la ponctuation. Une clé inconnue reste signalée et ne doit pas élargir silencieusement le périmètre.

`dashboard.get` conserve le `layout` brut pour l’édition. Le contexte calculé est exposé dans `weatherScope` et dans `widgets[].weatherContext` : rôle `hero`, `team` ou `other`, règle, applicabilité, représentation par la carte principale et explication. Les `widgets[].settings` peuvent contenir les seuils effectifs de cette lecture ; ils ne servent pas à remplacer les réglages persistés. Les cartes hors périmètre ou déjà représentées sont écartées en lecture, avec un emplacement explicite dans Composer. La comparaison des lignes et la météo restent calculées en C#, y compris pour les exports.

Les moteurs métier restent répartis par domaine, indépendamment des six destinations visibles :

- Analytics : `PilotApplication.Analytics*.cs`, moteurs Dashboard, historique, requêtes et rapports ; composition dans `pilot-workspace.js` et composants `analytics-*.js` réutilisés.
- Workplace : `PilotApplication.Workplace*.cs`, lecteur, actions, notes, capacités et répartition ; `tickets-workspace.js` coordonne la liste et le lecteur sans navigation vers une seconde page.
- Terrain : `PilotApplication.Terrain*.cs`, `PilotApplication.Maps*.cs` et `PilotApplication.MapWorkspace.cs` ; `terrain-map.js` compose la carte et les panneaux du référentiel, des corrections et des tournées. Les fonctions `renderReference`, `renderReview` et `renderTours` restent réutilisables dans ces panneaux.
- Données et réglages : `data-workspace.js`, `settings-workspace.js`, opérations du socle, collecte, import, sources, contrôles, automatisation et mise à jour.
- Bibliothèque et équipe : `PilotApplication.Library*.cs`, `PilotApplication.SharedWorkspace.cs`, moteurs `TicketLibrary*` et `TeamWorkspace*` ; composants de bibliothèque ouverts depuis Données ou le contexte métier.

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

## Navigation et mouvement — 3.4

`navigationSpaces` définit les six destinations canoniques. L’éditeur, la sidebar, le titre de page, le fil d’Ariane et la recherche de pages utilisent ce même contrat. Les préférences personnelles existantes sont relues en tenant compte des aliases ; les anciennes clés ne doivent pas réintroduire une liste d’écrans historiques. Accueil et Réglages restent accessibles.

`HiddenNavigation` conserve aussi les trois options `assistant`, `enrichment` et `populations`. `navigationVisible(id, preferences)` vérifie leur activation, y compris lors d’un lien direct. Elles sont réglées dans **Réglages → Fonctions facultatives** et apparaissent uniquement dans leur contexte. Une préférence de visibilité ne modifie pas les données métier conservées.

Le mouvement utilise la politique calculée par le shell : `data-motion-policy` vaut `full`, `system` ou `reduce` ; `data-domenia-motion` indique si l’animation est effectivement autorisée. Une règle CSS `prefers-reduced-motion` ne doit pas annuler le choix explicite `full`. Le contraste renforcé, le document caché, les scènes hors écran et l’édition d’un layout suspendent les effets concernés.

Les animations décoratives portent les noms `domenia-*` ou `pilot-weather-*`. Les scènes SVG de météo et les visuels de KPI utilisent les états et nombres calculés ; elles ne produisent aucune série ni gravité fictive. Les tableaux, champs et positions de widgets ne sont pas déplacés par les décors. Les ressources sont locales et la CSP de production reste appliquée.

## Révision et cycle de vie

Les formulaires et mutations de données transmettent la révision reçue lors de leur ouverture. Le runtime refuse une révision remplacée ; le navigateur GLPI possède aussi une génération de sélection d’onglet/tableau distincte. Les résultats chargés gardent une copie de leurs règles. Un changement de recherche invalide un relevé GLPI actif ; il ne réinterprète pas un import ou une archive avec les règles d’un autre profil.

`operations.cancel` est une opération enregistrée qui contourne le sémaphore métier pour annuler les travaux admis, y compris les collectes programmées. Les autres opérations sont sérialisées. `DisposeAsync` ferme l’admission, annule puis draine les tâches avant de libérer le navigateur et les ressources.

La fermeture native émet `app.beforeClose` avec un nonce ; la page annule les travaux, attend son cleanup asynchrone, puis répond `app.closeReady`. Ce cleanup termine notamment les écritures déjà engagées, les positions de lecture et les écritures en attente du brouillon de suivi. Les notes personnelles restent enregistrées explicitement par leur commande ; il ne s’agit pas d’une sauvegarde automatique générale des formulaires. Une erreur ou un délai de 15 secondes sans confirmation maintient la fenêtre ouverte. Le démarrage n’est acquitté qu’après navigation locale, bootstrap C# et première peinture confirmée par la page.

## Import préparé et changement de contexte

Le frontend utilise le proxy `sessionApi` fourni par le shell. `records.test.import({prepareOnly:true})` choisit et inspecte le fichier avant tout démontage de la page courante. Le C# conserve le chemin choisi et remet un jeton lié à la révision, à l’empreinte du fichier et à une échéance de 20 minutes :

- CSV/TSV : `{readyToImport:true, token, fileName}` ; la seconde demande avec le jeton effectue la lecture.
- XLSX/XLSM : `{selectionRequired:true, token, sheets, suggestedSheet, suggestedHeaderRow, workbookProfile, warnings}` ; le commit précise `sheetName`, `headerRow` et `useWorkbookProfile`.
- Annulation : `{token, cancelled:true}` abandonne uniquement la préparation. Le relevé et le DOM actifs restent en place.

Le chemin ne provient jamais d’un texte arbitraire envoyé par le navigateur. L’empreinte est contrôlée avant et après lecture. Les feuilles masquées et l’ancien format XLS ne sont pas importés. Seules les valeurs de cellules enregistrées sont lues ; macros, formules et liaisons externes ne sont pas exécutées.

Le commit d’import, `app.demo` et `records.test.exit` passent par `replaceContext`. Le shell bloque les changements concurrents, attend un rendu en cours puis `flushCurrentPage()`, et termine les cleanups **dans l’ancien contexte** avant d’appeler le runtime qui effectue la bascule. Les écritures du brouillon de suivi en attente sont ainsi vidées vers leur stockage d’origine ; les notes personnelles déjà explicitement enregistrées y restent. Aucun mécanisme général n’enregistre automatiquement toutes les notes ou tous les champs à cette occasion.

Si le commit échoue après démontage, le shell remonte la page précédente et restaure sa position. Le runtime initialise et valide la nouvelle instance avant de remplacer l’état visible. L’instance de simulation possède son propre stockage sous `AppPaths.EnterSimulation`, ses services et ses jetons. La sortie restaure l’instance ordinaire ; un jeton de carte ou une révision de simulation ne permet aucune mutation dans l’autre contexte.

Un fichier entièrement lu n’est pas présenté comme une recherche GLPI vérifiée. Sa provenance garde `SourceKind=file`, `SearchVerified=false`, `ScopeVerified=false` et son reçu de lecture. Les adaptateurs des actions, des collectes et des partages utilisent le contexte simulé. Le fond de carte peut charger des images OSM après ouverture explicite de Terrain, selon le contrat suivant.

## Environnement Windows et profil de test

Le pack impose `wad/runtime/webview2/msedgewebview2.exe`. La vue locale est associée à `https://glpi-pilot.local`. La CSP refuse le code inline, eval, les cadres et les connexions de scripts. Les styles dynamiques passent par les propriétés CSSOM des éléments. Depuis 3.3.4, une seule exception réseau est admise : les images PNG du fond OpenStreetMap pendant l’affichage demandé d’une carte, selon le contrat ci-dessous.

## Cartes intégrées et espace Terrain — 3.4

Leaflet 1.9.4 et le fond terrestre Natural Earth sont livrés dans `web/vendor`. `maps.js` fournit le même renderer pour le grand canevas de Terrain et les dialogues contextuels. Toutes les cartes restent dans le DOM de la vue principale ; aucune iframe ou fenêtre secondaire n’est créée. Les anciennes opérations de clé et d’ouverture Google ne sont plus enregistrées.

### Aperçus de lieux et de tournées

`maps.preview({kind,id?,ids?,revision?,draftVersion?,referenceDraft?})` accepte `site`, `city`, `sites`, `projection` et `tour`. Il retourne `{token,title,routes,notes,simulation,notice}`. Chaque route contient `{id,title,ordered,places}` ; chaque lieu contient `{id,label,address,precision,latitude,longitude,sequence}`. Les identifiants d’agents, tickets et notes ne figurent pas dans ce contrat d’aperçu de lieux.

Pour `sites`, l’absence de `ids` signifie tout le référentiel ; un tableau vide reste une sélection vide. Les parcours conservent chaque étape et sa séquence, même sans coordonnées. Un site sans coordonnées ne reçoit pas celles de sa commune. Les pointillés représentent l’ordre et ne relient jamais deux étapes séparées par une étape non localisée. Ils ne constituent pas un trajet routier.

### Carte des tickets : opérations locales

Le runtime utilise l’enrichissement géographique existant, le Dashboard, le roster des techniciens et les services de tournées. Il ne refait ni appariement par une nouvelle expression régulière, ni géocodage externe. Les calculs portent sur les occurrences source ; les appartenances multiples ne dupliquent pas une occurrence.

| Opération | Entrée et résultat |
| --- | --- |
| `maps.workspace.get` | `revision?`, `focusKeys?`, `technicianKeys?`, `mine?`, `type?`, `statusCodes?`, `visitDate?`, `includeEmptySites?`. Retourne un jeton de vue, sa révision, les choix disponibles, les filtres retenus, les compteurs, les localisations agrégées, les visites datées et la provenance. |
| `maps.workspace.rows` | `token`, `placeKey?`, `search?`, `offset`, `limit` ; total complet et page de `{ticket,selected}`, avec indices source. Limite de page maximale : 200. |
| `maps.workspace.select` | `token` et `rows?`, `placeKeys?`, `all?`, `selected?`, `clear?`. Union des indices source ; la réponse contient le nombre sélectionné et les indices retenus. Un dépassement de 2 000 est refusé avant mutation. |
| `maps.workspace.copyIds` | `token`, `placeKey?` ; copie de tous les IDs exploitables du périmètre, dans l’ordre source. Les occurrences répétées restent répétées. Une liste vide est refusée avant écriture du presse-papiers. |
| `maps.workspace.tour.preview` | `token`, `name`, `date`, `technicianKey` ; construit un aperçu avec étapes, tickets non localisés et état du brouillon existant, sans changer le planning. |
| `maps.workspace.tour.apply` | `token`, `previewToken`, `discardDraft?` ; valide révision, règles, sélection et version de planning, puis crée le brouillon local. L’enregistrement passe ensuite par `terrain.tours.save`. |

Le stock actif est le défaut ; `statusCodes` est une sélection non vide des codes 1 à 4. `type` vaut `all`, `incident`, `request` ou `unknown`. Les équipes/groupes se cumulent par OR, de même que les techniciens ; ces deux familles de filtres s’intersectent ensuite avec type et statut. Chacune accepte jusqu’à 64 choix. `@unassigned` désigne l’absence d’attribution. `mine` exige une identité personnelle effectivement présente dans le roster courant. Un choix disparu est refusé explicitement.

`totals.tickets`, `placed`, `sites`, `approximate`, `review`, `withoutCoordinates` et `unassigned` mesurent des occurrences source du périmètre. `unassigned` est indisponible lorsque la colonne d’attribution n’est pas interprétable. `places` mesure les regroupements de sites/communes ; il ne doit pas être présenté comme un nombre de tickets. `sourceUnknownStatus` décrit le **relevé entier**, avant filtres, et reste explicitement séparé du stock actif. Le `grain` indique si les occurrences peuvent être nommées tickets ou doivent rester des lignes.

La vue conserve les titres absents, ambiguïtés, lieux inconnus et sites sans coordonnées dans ses listes. Les lieux reconnus uniquement par commune sont approximatifs. `includeEmptySites` ajoute les sites à stock zéro sans ajouter de tickets. Les cartes, détails, sélection, copie d’IDs et préconisations utilisent le même jeton de périmètre. Le seuil d’intervention vient du profil.

`visits` est une couche séparée de visites **PRÉVUES**, issues des tournées enregistrées non marquées effectuées, pour la date choisie. Les noms sont des affectations déclarées. Ils ne représentent jamais des positions actuelles de personnes. Un registre de tournées illisible produit un avertissement et laisse les tickets consultables.

Les jetons de vue restent liés à la révision et aux règles effectives. Un changement de sélection invalide son aperçu de tournée ; un autre brouillon modifié exige un remplacement explicite. Les coordonnées, contacts et étapes du brouillon viennent du référentiel et des services existants. Les sélections ambiguës du point de vue des identifiants restent soumises aux validations de `ScheduledTourService`.

### Panneaux de Terrain

`terrain-map` accepte `panel=sites|reference|review|populations|tours|planning|projection|services`. `planning` reprend `tourId` ou `openDraft` lorsque fournis. Les filtres d’équipes et techniciens transmis depuis Pilotage sont repris à l’ouverture. Les panneaux n’affichent pas la navigation d’onglets des anciens écrans.

Fermer un panneau de planning applique d’abord les champs valides de son en-tête au brouillon local. Un champ invalide garde le panneau ouvert avec son erreur et préserve le brouillon précédent. La fermeture renouvelle ensuite la carte. Les mutations du référentiel suivent toujours leur aperçu et leur application explicite. Les effectifs ne s’ouvrent que si l’option `populations` est activée.

### Images OSM, permissions et cycle de vie

À l’ouverture explicite d’une carte avec coordonnées, le renderer demande les rues **dans les deux modes, ordinaire et simulation**. `maps.tiles({token,enabled:true})` doit réussir avant d’ajouter la couche. **Fond hors ligne** ou une erreur de tuile détache cette couche et conserve le fond vectoriel et les repères. Une permission refusée laisse la vue locale.

L’autorisation d’images est liée au jeton d’aperçu, à la révision, au référentiel et aux versions des brouillons. Un aperçu non activé expire après 30 minutes ; une carte active reste utilisable jusqu’à sa fermeture ou un changement de contexte. Le nettoyage détache la couche puis appelle `maps.tiles({token,enabled:false})`, même après annulation de la page. Une activation arrivée après fermeture est révoquée. L’instance de simulation possède ses propres permissions et ne rétablit aucune permission ordinaire à sa sortie.

Le garde natif accepte uniquement des requêtes GET de type Image vers `https://tile.openstreetmap.org/z/x/y.png`, avec bornes de zoom et de coordonnées valides, sans paramètre, fragment, identifiant ni port alternatif. Scripts, documents, cadres et géocodage externes restent interdits. Les métadonnées HTTP normales sont conservées, le User-Agent identifie GLPI-Pilot et le cache WebView reste actif. Aucun préchargement géographique n’est proposé. Seuls les indices des tuiles visibles et les métadonnées HTTP usuelles sont transmis ; les marqueurs, adresses, tickets et affectations restent locaux.

Le fond Natural Earth est dans un pane sous les tuiles pour ne pas les masquer. Les contenus de popup et de liste sont des éléments DOM textuels, jamais du HTML construit avec le contenu du référentiel. Les dimensions Leaflet sont recalculées après ouverture et redimensionnement ; la fermeture détruit l’instance. L’attribution OpenStreetMap reste visible avec la couche active et ouvre la notice locale de licence.

Implémentations : [runtime de carte](../src/GlpiPilot/Runtime/PilotApplication.MapWorkspace.cs), [gestion des tuiles](../src/GlpiPilot/Runtime/PilotApplication.Maps.cs), [espace Terrain](../web/js/pages/terrain-map.js), [renderer partagé](../web/js/pages/maps.js), [navigation](../web/js/navigation.js).

## Vérification native isolée

Le port CDP de vérification est disponible uniquement avec `GLPI_PILOT_TEST_MODE=1`, une racine temporaire `GLPI-Pilot-Checks-*` sans lien et `--test-port`. Les harnais créent et nettoient leurs propres profils ; ils ne déplacent pas le profil réel. Les variables externes de démarrage WebView sont retirées avant de créer le moteur.

Référence d’infrastructure lue : [REPRISE.md](https://github.com/M4D135/domenia-d120/blob/v2/v2/REPRISE.md), branche v2, 22 octobre 2026. VM Windows 11 4 vCPU/8 Go, SDK/cache locaux et session de bureau à préserver. La CI Windows publique et cette VM privée sont deux environnements distincts.
