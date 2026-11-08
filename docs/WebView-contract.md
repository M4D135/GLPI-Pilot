# Contrat WebView — GLPI Pilot 3.5.0

Architecture : hôte WinForms minimal, WebView2 et interface locale ; règles et E/S en C#. Le modèle d’architecture initial provient de M4D135/domenia-d120, branche v2, commit 048627387efdc9e44d4da77121cd82d92b2d28e9. La refonte visuelle 3.4 étudie également l’Accueil et les styles de la branche v2 au commit 54b071d8cdecaa387a67d6c1a1e620af4674f2ba.

## C# partagé

`GlpiPilot.Runtime.PilotApplication` est une classe `public sealed partial`.
Le constructeur appelle `RegisterFoundationOperations()`, `RegisterAnalyticsOperations()`, `RegisterWorkplaceOperations()`, `RegisterTerrainOperations()` et `RegisterCalendarOperations()` ; chaque équipe définit sa méthode privée et ses handlers dans ses propres fichiers `Runtime/PilotApplication.<domaine>.cs`.

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

## Quatre espaces de travail et deux accès utilitaires

Le registre du shell expose quatre espaces principaux, puis deux accès utilitaires. Les fonctions personnelles et communes appartiennent à la même interface : aucun rôle applicatif ni authentification supplémentaire n’est introduit. L’identité choisie sert aux filtres de travail ; elle n’accorde pas de permission et ne prouve pas une identité GLPI. Les outils détaillés utilisent leur contexte, un panneau latéral ou un dialogue selon leur fonction.

| Identifiant stable | Libellé | Composition |
| --- | --- | --- |
| `home` | Accueil | Priorités, alertes, rendez-vous, reprises et accès au planning. |
| `records` | Tickets | Liste source et lecteur adjacent, travail personnel, actions et interventions liées. |
| `interventions` | Interventions | Planning commun, carte des interventions ou des tickets et tournées. |
| `pilot` | Pilotage | Météo, activité, vigilance, équipes, catégories, participants et personnalisation de la vue. |
| `data` | Données | Accès utilitaire : import, collecte, qualité, correspondances, bibliothèque et relevés partagés. |
| `settings` | Réglages | Accès utilitaire : présentation, règles, profils, partage entre postes, conservation et automatisations. |

Les modules exportent leurs entrées depuis `web/js/pages/<groupe>.js` :

```js
export const pages = [
 { id: 'interventions', title: 'Interventions', group: 'Interventions', icon: 'calendar',
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
| `terrain-map` | `interventions`, `{tab:"map",ticketMap:true}` : ouvre la carte complète des tickets. |
| `pilot-sites`, `terrain-reference`, `terrain-review`, `populations` | `interventions`, `{tab:"map",ticketMap:true,panel:"sites"\|"reference"\|"review"\|"populations"}`. |
| `tours` | `interventions`, `{tab:"tours",panel:"tours"}`, ou `panel:"planning"` lorsqu’un `tourId` / `openDraft` est fourni. |
| `pilot-settings`, `automation`, `updates`, `operations`, `help` | Panneau de `settings`. |
| `day`, `alerts` | Outil de priorités ou d’alertes dans `home`. |

Les commandes ouvrant un outil exposent `data-open-panel`. Le dialogue résultant expose `data-workspace-panel`. `createWorkspacePanels(ctx)` gère les montages génériques ; la carte des tickets ajoute les vérifications propres au brouillon de tournée et au renouvellement de la carte. `interventions-workspace.js` compose les onglets `planning`, `map` et `tours` et monte les composants carte/tournées avec leurs cleanups. Les onglets exposent `data-calendar-tab`. Une fermeture attend le cleanup asynchrone utile avant de détruire le contexte. Les erreurs restent dans le dialogue concerné.

Les liens directs du calendrier acceptent `date`, `view`, `technicianKeys`, `teamKeys`, `mine`, `eventId` ou `interventionId`, `occurrenceKey`, `identity:true` et `conflicts:true`. Une préparation depuis un ticket utilise `{plan:true,rowIndex|indices|ticketKey,revision}` ; les indices sont résolus par `calendar.tickets` dans la révision annoncée. Le choix `ticketMap:true` est conservé lors des aliases et des retours au contexte cartographique ; la carte des interventions reste une sous-vue distincte du même onglet. La sous-vue, les filtres, le défilement et le cadrage sont mémorisés.

`work-context.js` ne transporte que des préférences de vue. Sa clé distingue usage ordinaire/simulation et identité personnelle sélectionnée ; `readWorkspaceView` et `saveWorkspaceView` n’enregistrent ni lignes source, ni notes, ni brouillons métier. Le stockage navigateur est borné à 24 000 caractères par préférence, avec repli mémoire s’il est indisponible.

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

`dashboard.get` conserve le `layout` brut pour l’édition. Le contexte calculé est exposé dans `weatherScope` et dans `widgets[].weatherContext` : rôle `hero`, `team` ou `other`, règle, applicabilité, représentation par la carte principale et explication. Les `widgets[].settings` peuvent contenir les seuils effectifs de cette lecture ; ils ne servent pas à remplacer les réglages persistés. Les cartes hors périmètre ou déjà représentées sont écartées en lecture, avec un emplacement explicite dans **Personnaliser la vue**. La comparaison des lignes et la météo restent calculées en C#, y compris pour les exports.

Les moteurs métier restent répartis par domaine, indépendamment de l’organisation du menu :

- Analytics : `PilotApplication.Analytics*.cs`, moteurs Dashboard, historique, requêtes et rapports ; composition dans `pilot-workspace.js` et composants `analytics-*.js` réutilisés.
- Workplace : `PilotApplication.Workplace*.cs`, lecteur, actions, notes, capacités et répartition ; `tickets-workspace.js` coordonne la liste et le lecteur sans navigation vers une seconde page.
- Calendrier : `PilotApplication.Calendar*.cs`, `InterventionCalendar*`, documents `CalendarWorkspaceDocuments` et transport atomique `TeamWorkspaceCalendarProtocol` ; `interventions-workspace.js` orchestre le planning et réutilise la carte et les tournées.
- Terrain et cartes : `PilotApplication.Terrain*.cs`, `PilotApplication.Maps*.cs` et `PilotApplication.MapWorkspace.cs` ; `terrain-map.js` compose la carte et les panneaux du référentiel, des corrections et des tournées. Les fonctions `renderReference`, `renderReview` et `renderTours` restent réutilisables dans ces panneaux.
- Données et réglages : `data-workspace.js`, `settings-workspace.js`, opérations du socle, collecte, import, sources, contrôles, automatisation et mise à jour.
- Bibliothèque et équipe : `PilotApplication.Library*.cs`, `PilotApplication.SharedWorkspace.cs`, `PilotApplication.SharedBusiness.cs`, moteurs `TicketLibrary*`, `TeamWorkspace*`, `SharedBusinessDocuments` et répliques de dossier ; composants de bibliothèque ouverts depuis Données ou le contexte métier. `shared-business.js` porte le comparateur des données communes, et `shared-records.js` la sélection d’un relevé commun.

Les sources WPF sont retirées du projet et de cette arborescence. Leur état avant migration reste dans le commit historique `d9c6434b462bb3ad700a206c93c70fd52142bd5e`. Aucun écran de la version 3 ne dépend de WPF.

## Calendrier commun — 3.5.0

### Documents et identités

`RegisterCalendarOperations()` enregistre les opérations ci-dessous. Elles fonctionnent sans extraction pour les événements libres et ressources déclarées. `CalendarIntervention` porte :

```text
id, revision, seriesId, occurrenceOffset, title,
kind: intervention | maintenance | meeting | unavailability,
startLocal, endLocal, timeZoneId, startOffsetMinutes?, endOffsetMinutes?, allDay,
participantKeys[], tickets[{key,installation,id,url,title}], siteId, location, address, remote,
state: planned | confirmed | inProgress | done | cancelled,
notes, contact, equipment, requiredSkills[], travelBeforeMinutes, travelAfterMinutes,
reminderMinutes?, recurrence?, exceptions[], history[]
```

Les dates locales sont `yyyy-MM-ddTHH:mm`, les bornes de jour `yyyy-MM-dd` et la fin est exclusive. Les instants UTC sont calculés en C#. Une heure inexistante est refusée ; une heure répétée demande le décalage UTC qui la désambiguïse. L’UI ne convertit pas une inconnue en horaire fictif. Les limites comprennent 300 caractères de titre, 8 000 de notes, 100 participants et 2 000 tickets par événement. Les références de tickets conservent leur installation ; un numéro seul n’est pas une identité inter-portails.

Une récurrence contient `{frequency:"daily"|"weekly"|"monthly",interval,weekDays:[1..7],until?,count?}`. Une exception contient `{originalStartLocal,cancelled,replacement?}` ; le remplacement est un événement complet sans récurrence ni exceptions propres. Les occurrences exposent `key`, `interventionId`, `revision`, `originalStartLocal`, horaires locaux/UTC, champs visibles, `source:"calendar"|"tour"`, `tourId`, `stopId` et `readOnly`. **`key` est opaque et stable** ; ne pas en déduire une date par découpage. Une mutation d’occurrence ou de suite transmet `originalStartLocal`.

Les ressources contiennent `{key,label,teamKeys,timeZoneId,active,workPeriods,capacityDays}` ; la réponse de catalogue ajoute identité de document, révision et disponibilité des capacités. Une période hebdomadaire déclare le jour 1–7, début/fin en minutes et capacité facultative. `calendar.identity` mémorise uniquement le filtre personnel. Tous les outils restent accessibles après cette sélection.

### Bridge de lecture et préparation

| Opération | Entrée et résultat |
| --- | --- |
| `calendar.get` | `{from,to,timeZoneId?,scope:"all"\|"mine",technicians?,teams?,ticketKey?,siteId?}`. Retourne occurrences, ressources, équipes, sites/communes, résumé, contraintes, file à planifier, identité, révision source, couverture et état de synchronisation. Vue de 1 à 93 jours, fin exclusive. |
| `calendar.summary` | Même périmètre ; résumé et rendez-vous proches, contraintes et état partagé pour l’Accueil. |
| `calendar.item` | `{id}` → `{intervention,revision,pending,conflicts,history,...}`. La lecture complète précède l’édition. |
| `calendar.tickets` | Recherche paginée par texte, `ticketKey`, `rowIndex` ou `indices`, avec révision source facultative, filtres et `unplanned/from/to`. Réponse `{tickets,rows,total,offset,limit,sourceRevision}` ; maximum 2 000 éléments par page ou sélection. |
| `calendar.forTicket` | `{ticketKey,from?,to?}` → prochaines occurrences (8), passées (5), total et nombre d’interventions liées. La lecture liée peut couvrir jusqu’à 732 jours. |
| `calendar.suggest` | Événement, bornes, `scope`, `originalStartLocal?`, `timeZoneId?`, limite. Propose des créneaux individuels `{startLocal,endLocal,startUtc,endUtc,score,reasons,warnings}` sur au plus 31 jours, jusqu’à 30 résultats. Une suggestion ne valide pas toute une série. |
| `calendar.reminders.check` | `{scope?,notify?}` → `{items,count,due}`. Notification locale facultative ; aucune notification système réelle en simulation et aucun envoi à un tiers. |
| `calendar.export` | Périmètre de lecture et `format:"ics"\|"html"` ; sélection native du fichier, occurrences et noms des participants. Pas de connexion à un calendrier externe. |

`TickAsync` vérifie automatiquement les rappels personnels au plus une fois par minute lorsque l’application reste ouverte. La vérification est suspendue pendant les plages silencieuses, le verrouillage ou une édition ; elle exige une identité sélectionnée et n’émet aucune notification système en simulation. Aucun service de rappel ne fonctionne après la fermeture de l’application.

Les calculs de contraintes incluent les réservations connues hors du filtre affiché. `unplanned` retire les tickets déjà réservés sur la période pour l’ensemble des ressources, puis applique le périmètre demandé. L’absence de relevé n’efface pas les rendez-vous et leurs références. L’absence d’identité en `scope:"mine"` retourne une vue personnelle vide et `identityRequired:true`.

Le résumé compte les occurrences une fois, même dans plusieurs lignes de participants. `linkedTickets` déduplique leurs références ; `personHours` multiplie durée par participants, hors indisponibilités. Les heures et disponibilités sont déclarées ou calculées depuis ces déclarations, sans mesure de présence réelle.

### Bridge de mutation et arbitrage

| Opération | Contrat |
| --- | --- |
| `calendar.preview`, `calendar.save` | `{intervention,expectedRevision,scope:"series"\|"occurrence"\|"following",originalStartLocal?,occurrenceKey?,allowConflicts?,conflictFingerprint?}`. La prévisualisation ne sauvegarde pas. Un enregistrement réussi rend le document et `undoToken`. |
| `calendar.remove` | `{id,expectedRevision,scope,originalStartLocal?,occurrenceKey?}` ; retrait de la portée et jeton d’annulation. |
| `calendar.undo` | `{token}` ; restaure le lot seulement si toutes ses versions résultantes correspondent encore. Jetons locaux limités aux vingt dernières opérations pendant une heure. |
| `calendar.shift` | Préparation `{items:[{id,expectedRevision,originalStartLocal?,occurrenceKey?}],minutes}` pour 1 à 50 sélections ; réponse avec `token`, aperçu, contraintes et période contrôlée. Application `{token,allowConflicts?,conflictFingerprint?}` après nouvelle vérification ; un seul lot et jeton d’annulation. |
| `calendar.resource.save` | `{resource,expectedRevision?,chooseAsMe?}` ; document de ressource versionné. Maximum 2 000 ressources enregistrées. |
| `calendar.identity` | `{key}` ; exige une ressource existante, mémorise l’identité personnelle de filtre et peut conserver sa fiche dans le registre. |

L’aperçu expose `issues`, `requiresConfirmation`, `requiresCorrection`, `conflictFingerprint`, `checkedFrom` et `checkedTo`. Les contraintes de gravité `warning`/`danger` demandent un arbitrage ; une information sur une disponibilité inconnue n’exige pas de forçage. `allowConflicts:true` doit accompagner l’empreinte de l’ensemble examiné. Une nouvelle contrainte invalide cet accord. Une heure inexistante ou ambiguë non résolue n’est jamais franchie par ce mécanisme. Le contrôle d’une série part d’une fenêtre de 366 jours, étendue pour inclure ses exceptions déplacées si nécessaire ; un lot couvrant plus de 732 jours avant les marges de lecture est refusé. La réponse annonce la période réellement examinée.

Les documents `calendar-intervention` et `calendar-resource` résident dans le store d’équipe local. Les séparations de série, déplacements groupés et annulations utilisent `SaveCalendarBatchAsync` : toutes les révisions sont vérifiées avant la transaction. `ITeamWorkspaceBatchReplica` conserve le lot indivisible lors du partage par dossier ou PostgreSQL. Une nouvelle partie de série refusée est conservée avec son tombstone et sa proposition ; aucun fragment isolé n’est publié. Les rejouages portent un identifiant et une empreinte de mutation, sans application répétée.

### Versions concurrentes du calendrier

| Opération | Contrat |
| --- | --- |
| `calendar.conflicts.list` | `{offset?,limit?}` ; groupes non résolus, total, pagination et `hasMore`, maximum 50 par page. Le compteur couvre aussi les tombstones et les événements hors période. |
| `calendar.conflicts.get` | `{id}` → `{id,recordedAt,reason,items:[{id,current,incoming}],token}`. Le jeton garde toutes les révisions actuelles et expire après 20 minutes. |
| `calendar.conflicts.resolve` | `{token,choice:"current"\|"incoming",allowConflicts?,conflictFingerprint?}`. Décision sur le groupe entier ; révisions et contraintes sont relues. Peut retourner une demande d’arbitrage avant toute mutation. |
| `calendar.conflicts.export` | `{token}` ; export natif des versions complètes du groupe. |

Les états, champs source et versions sont rendus par des éléments textuels. La revue ne permet pas de résolution partielle d’un lot. Une mutation générique de contribution ne remplace pas les opérations propriétaires du calendrier. Voir [Calendrier et interventions](Calendrier-et-interventions.md).

## Bibliothèque locale et partage facultatif — 3.5.0

Le cache SQLite reste local. `syncMode` vaut `local` (défaut), `folder` ou `postgres` ; `sharedFolder` contient le chemin absolu du dossier commun et `sharingEnabled` indique si un transport commun est actif. `postgresEnabled` reste exposé pour compatibilité ; un `syncMode` explicite prévaut. Le dossier réseau et PostgreSQL utilisent les protocoles versionnés des tickets et de l’espace d’équipe. Les fichiers, transactions, versions et connexions restent en C#. Les pages transmettent des objets bornés et ne reçoivent ni chaîne de connexion ni secret mémorisé.

| Famille d’opérations | Contrat |
| --- | --- |
| `library.get`, `library.search`, `library.ticket`, `library.conflicts` | État, recherche paginée, contenu du ticket et aperçus bornés des versions concurrentes. La recherche de solutions ne retient que les contenus complets et actuels de tickets résolus ou clos. |
| `library.capture` | `mode: incremental` réutilise les contenus dont la version est prouvée inchangée ; `mode: all` relit les contenus. Le bilan expose périmètre, limites, lectures, réutilisations et échecs. |
| `library.settings.save` | `{settings,password?,rememberPassword?,forgetPassword?}` ; mode, dossier partagé et autres réglages propres au poste. Le mot de passe saisi n’est jamais renvoyé. |
| `library.folder.pick`, `library.sharedFolder.pick` | Sélecteurs natifs distincts pour la copie locale et le dossier commun ; `{folder,cancelled}`. Le cache actif ne peut pas résider dans le dossier partagé. |
| `library.connection.test` | Teste le mode demandé sans enregistrer le choix. En mode dossier, initialise au besoin le registre et vérifie lecture/écriture ; en PostgreSQL, teste la connexion avec `SELECT 1`. |
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

### Réglages, transport par dossier et état visible

`library.get.sync` expose `mode`, `enabled`, `automatic`, `state`, `label`, `lastError`, `lastAttempt`, `lastSuccess`, `nextSync`, `pending`, `pendingTickets`, `pendingContributions`, `pushed`, `pulled`, `conflicts` et `simulated`. **`lastSuccess` est lié à la destination configurée et à un échange complet réussi** ; ne pas le remplacer par la date de synchronisation partielle du cache. Le frontend rafraîchit cet état sans reconstruire les champs d’un formulaire en cours.

`SharedFolderWorkspace` crée le registre `GLPI-Pilot-Shared` sous le dossier choisi, avec journaux versionnés tickets/équipe. Le cache actif reste dans son dossier local distinct. Verrou d’accès, écritures temporaires puis renommage, empreintes de chaîne et reçus de mutations protègent la reprise ; un registre incohérent ou indisponible arrête l’échange et conserve la file locale. Les fenêtres n’écrivent pas directement dans les fichiers du partage. L’accès dépend de la session Windows au dossier ou de la connexion technique PostgreSQL ; aucun compte d’application ni rôle de navigation n’est ajouté.

La simulation remplace les deux transports par des répliques isolées en mémoire. Le sélecteur de dossier et les paramètres sont rebasés dans son contexte ; test et synchronisation ne contactent ni dossier réseau réel ni PostgreSQL réel. `library.tick` garde les contrôles d’activité, d’éditeur et de session avant un échange périodique.

### Données métier communes et relevés complets

`RegisterSharedBusinessOperations()` enregistre les opérations ci-dessous. Les documents `glpi-pilot.shared-business`, version 1, couvrent `team-configuration`, `team-tour`, `team-technician`, `team-dispatch`, `team-dataset`, `team-location` et `team-order`. Ils représentent les données enregistrées communes et sont préparés/reçus lors de la synchronisation activée. Les publications ponctuelles `glpi-pilot.business-document` décrites plus haut restent un parcours distinct de reprise explicite.

| Opération | Contrat |
| --- | --- |
| `sharing.datasets` | Liste `{id,revision,sourceKey,title,observedAt,rows,columns,...}` et `{enabled,followedId,warnings}`. L’UI montre au plus 200 périmètres et l’indique. |
| `sharing.dataset.load` | `{id,expectedRevision}` ; charge la copie conservée et ses correspondances, mémorise le relevé suivi. Refuse une révision remplacée. |
| `sharing.business.status` | `{enabled,documents,conflicts:[{id,conflictId,revision,title,kind,label,recordedAt}],warnings,moreConflicts}`. Liste de conflits bornée à 100 ; les propositions non examinées restent conservées. |
| `sharing.business.conflict` | `{id,conflictId}` → `{id,conflictId,expectedRevision,title,kind,current,incoming}`. Chaque version expose auteur, date, retrait et données. Un relevé présente un résumé de dimensions/date/empreinte. |
| `sharing.business.resolve` | `{id,conflictId,expectedRevision,choice:"current"\|"incoming"}`. Garde de révision, application de la version entière et décision mise en file pour les autres postes. |
| `sharing.business.export` | `{id,conflictId,expectedRevision}` ; sélection native et export intégral des deux documents, y compris les données compressées d’un relevé. |

Le document de relevé conserve les en-têtes, lignes, valeurs et correspondances, pas seulement les colonnes utiles à une vue. Sa décompression est bornée et contrôlée par empreinte : au maximum 64 Mio décompressés, 12 Mio compressés, 1 000 000 de lignes et 512 colonnes. Un dépassement laisse la source locale avec une explication. La réception conserve la provenance d’observation et le marquage de copie partagée ; elle ne transforme pas un export partiel en collecte GLPI certifiée.

L’identité d’un relevé combine l’URL publique normalisée et son périmètre. `UrlKey` identifie l’URL ; `SourceKey` inclut **`ScopeFingerprint` seulement si `SearchVerified` et `ScopeVerified` sont vrais**. À défaut, il utilise **`RunId` comme identité d’observation**. Deux périmètres ne sont donc pas fusionnés au seul motif d’un portail commun. Les paramètres d’authentification/session et le paramètre de pagination `start` sont retirés de l’URL transportée. Une source explicitement choisie par l’utilisateur reste en place ; le suivi automatique concerne le relevé commun retenu, sans mélange arbitraire de candidats ambigus.

Les réglages de connexion, secrets, profil SSO, identité de filtre, présentation de l’interface et préférences privées restent locaux selon les exclusions des types dédiés. Les notes personnelles ne deviennent pas des données d’équipe sans leur publication explicite. Les sites/règles/tableaux de bord communs, tournées, capacités, compétences, répartitions, corrections et ordres de tournée utilisent leurs documents métier. Les mutations génériques `workspace.save/remove` ne servent pas à éditer ou retirer ces types propriétaires.

`shared-business.js` compare les champs par pages de 40. Chaque version est explorée sur au plus 5 000 nœuds et 18 niveaux, avec valeurs d’aperçu limitées à 2 000 caractères. Les parties non parcourues ne sont pas assimilées à des champs absents ; toute limite est annoncée et l’export complet reste disponible. La décision transmet la révision de la lecture examinée. Les contenus du référentiel et du relevé sont toujours insérés comme texte. Voir [Bibliothèque et partage](Bibliotheque-et-partage.md).

## Navigation et mouvement — 3.5.0

`navigationSpaces` définit les quatre espaces principaux et les deux utilitaires canoniques. Les entrées `utility:true` restent regroupées en bas ; les déplacements et épingles respectent ces deux parties. L’éditeur, la sidebar, le titre de page, le fil d’Ariane et la recherche de pages utilisent ce même contrat. Les préférences personnelles existantes sont relues en tenant compte des aliases ; les anciennes clés ne doivent pas réintroduire une liste d’écrans historiques. Accueil et Réglages restent accessibles.

`HiddenNavigation` conserve aussi les trois options `assistant`, `enrichment` et `populations`. `navigationVisible(id, preferences)` vérifie leur activation, y compris lors d’un lien direct. Elles sont réglées dans **Réglages → Fonctions facultatives** et apparaissent uniquement dans leur contexte. Une préférence de visibilité ne modifie pas les données métier conservées.

Le mouvement utilise la politique calculée par le shell : `data-motion-policy` vaut `full`, `system` ou `reduce` ; `data-domenia-motion` indique si l’animation est effectivement autorisée. Une règle CSS `prefers-reduced-motion` ne doit pas annuler le choix explicite `full`. Le contraste renforcé, le document caché, les scènes hors écran et l’édition d’un layout suspendent les effets concernés.

Les animations décoratives portent les noms `domenia-*` ou `pilot-weather-*`. Les scènes SVG de météo et les visuels de KPI utilisent les états et nombres calculés ; elles ne produisent aucune série ni gravité fictive. Les tableaux, champs et positions de widgets ne sont pas déplacés par les décors. Les ressources sont locales et la CSP de production reste appliquée.

## Révision et cycle de vie

Le calendrier publie `pilot:calendar-editor` avec `{open,dirty}`. Le shell combine cet état avec les dialogues et autres éditeurs pour `host.editor`, afin de différer les traitements automatiques. Le cleanup peut demander l’abandon d’une saisie : si l’utilisateur conserve le brouillon, il rejette avec `DOMException(...,"AbortError")`. La navigation, le changement de contexte ou la fermeture restent alors sur l’écran actuel ; le shell ne transforme pas cette annulation en notification d’échec. Le contrôleur et le DOM ne sont détruits qu’après réussite de la garde.


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

Un fichier entièrement lu n’est pas présenté comme une recherche GLPI vérifiée. Sa provenance garde `SourceKind=file`, `SearchVerified=false`, `ScopeVerified=false` et son reçu de lecture. Les adaptateurs des actions, des collectes et des partages utilisent le contexte simulé. Le fond de carte peut charger des images OSM après ouverture explicite d’une carte dans Interventions, selon le contrat suivant.

## Environnement Windows et profil de test

Le pack impose `wad/runtime/webview2/msedgewebview2.exe`. La vue locale est associée à `https://glpi-pilot.local`. La CSP refuse le code inline, eval, les cadres et les connexions de scripts. Les styles dynamiques passent par les propriétés CSSOM des éléments. Depuis 3.3.4, une seule exception réseau est admise : les images PNG du fond OpenStreetMap pendant l’affichage demandé d’une carte, selon le contrat ci-dessous.

## Cartes intégrées dans Interventions — 3.5.0

Leaflet 1.9.4 et le fond terrestre Natural Earth sont livrés dans `web/vendor`. `maps.js` fournit le même renderer pour les canevas d’Interventions et les dialogues contextuels. Toutes les cartes restent dans le DOM de la vue principale ; aucune iframe ou fenêtre secondaire n’est créée. Les anciennes opérations de clé et d’ouverture Google ne sont plus enregistrées.

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

### Panneaux de la carte des tickets

L’alias `terrain-map` transmet `ticketMap:true` à `interventions`. Le composant de carte des tickets accepte `panel=sites|reference|review|populations|tours|planning|projection|services`. `planning` reprend `tourId` ou `openDraft` lorsque fournis. Les filtres d’équipes et techniciens transmis depuis Pilotage sont repris à l’ouverture. Les panneaux n’affichent pas la navigation d’onglets des anciens écrans.

Fermer un panneau de planning applique d’abord les champs valides de son en-tête au brouillon local. Un champ invalide garde le panneau ouvert avec son erreur et préserve le brouillon précédent. La fermeture renouvelle ensuite la carte. Les mutations du référentiel suivent toujours leur aperçu et leur application explicite. Les effectifs ne s’ouvrent que si l’option `populations` est activée.

### Carte du calendrier et conservation du contexte

Le calendrier obtient l’autorisation de fond via `maps.preview({kind:"sites"})`, puis projette les occurrences visibles comme `visits` : `id`, `interventionId`, `occurrenceKey`, `source`, `place`, `date`, horaires, libellé, participants et références de tournée. Un repère ouvre la même intervention. Une commune peut servir de repère de tournée ou d’approximation explicite ; elle ne devient pas une coordonnée précise du site. Les interventions à distance et lieux non localisés restent listés.

`mountMap` accepte `options.view` et fournit `getView()` pour le centre, le zoom et le fond. Les sous-vues planning/tickets gardent leur propre cadrage et leurs filtres. La carte et les visites représentent des réservations et affectations observées, jamais la position réelle d’une personne.

### Images OSM, permissions et cycle de vie

À l’ouverture explicite d’une carte avec coordonnées, le renderer demande les rues **dans les deux modes, ordinaire et simulation**. `maps.tiles({token,enabled:true})` doit réussir avant d’ajouter la couche. **Fond hors ligne** ou une erreur de tuile détache cette couche et conserve le fond vectoriel et les repères. Une permission refusée laisse la vue locale.

L’autorisation d’images est liée au jeton d’aperçu, à la révision, au référentiel et aux versions des brouillons. Un aperçu non activé expire après 30 minutes ; une carte active reste utilisable jusqu’à sa fermeture ou un changement de contexte. Le nettoyage détache la couche puis appelle `maps.tiles({token,enabled:false})`, même après annulation de la page. Une activation arrivée après fermeture est révoquée. L’instance de simulation possède ses propres permissions et ne rétablit aucune permission ordinaire à sa sortie.

Le garde natif accepte uniquement des requêtes GET de type Image vers `https://tile.openstreetmap.org/z/x/y.png`, avec bornes de zoom et de coordonnées valides, sans paramètre, fragment, identifiant ni port alternatif. Scripts, documents, cadres et géocodage externes restent interdits. Les métadonnées HTTP normales sont conservées, le User-Agent identifie GLPI-Pilot et le cache WebView reste actif. Aucun préchargement géographique n’est proposé. Seuls les indices des tuiles visibles et les métadonnées HTTP usuelles sont transmis ; les marqueurs, adresses, tickets et affectations restent locaux.

Le fond Natural Earth est dans un pane sous les tuiles pour ne pas les masquer. Les contenus de popup et de liste sont des éléments DOM textuels, jamais du HTML construit avec le contenu du référentiel. Les dimensions Leaflet sont recalculées après ouverture et redimensionnement ; la fermeture détruit l’instance. L’attribution OpenStreetMap reste visible avec la couche active et ouvre la notice locale de licence.

Implémentations : [runtime de carte](../src/GlpiPilot/Runtime/PilotApplication.MapWorkspace.cs), [gestion des tuiles](../src/GlpiPilot/Runtime/PilotApplication.Maps.cs), [carte des tickets](../web/js/pages/terrain-map.js), [espace Interventions](../web/js/pages/interventions-workspace.js), [renderer partagé](../web/js/pages/maps.js), [navigation](../web/js/navigation.js).

## Vérification native isolée

Le port CDP de vérification est disponible uniquement avec `GLPI_PILOT_TEST_MODE=1`, une racine temporaire `GLPI-Pilot-Checks-*` sans lien et `--test-port`. Les harnais créent et nettoient leurs propres profils ; ils ne déplacent pas le profil réel. Les variables externes de démarrage WebView sont retirées avant de créer le moteur.

Référence d’infrastructure lue : [REPRISE.md](https://github.com/M4D135/domenia-d120/blob/v2/v2/REPRISE.md), branche v2, 22 octobre 2026. VM Windows 11 4 vCPU/8 Go, SDK/cache locaux et session de bureau à préserver. La CI Windows publique et cette VM privée sont deux environnements distincts.
