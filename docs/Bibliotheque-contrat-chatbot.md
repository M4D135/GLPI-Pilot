# Bibliothèque : contrat SQL pour une future intégration de chatbot

## Ce qui est disponible

Les données de tickets recueillies par GLPI-Pilot restent dans **la base SQLite locale existante**. Lorsque le partage PostgreSQL est configuré, les documents sont répliqués dans **la base centrale existante**. Le contrat décrit ici expose leurs champs, leurs textes et leur provenance sous forme de vues SQL consultables directement. Aucun chatbot, modèle, fournisseur IA, service réseau supplémentaire ou base vectorielle n’est connecté par cette fonctionnalité.

Le document de ticket reste la source de référence : les vues ne créent pas une seconde copie métier et ne décident pas quelle version doit remplacer une autre. Elles reflètent les écritures, les confirmations, les importations et la synchronisation qui utilisent déjà les règles de la bibliothèque. Le protocole de documents et le schéma de synchronisation restent en version 1.

Le périmètre est **ce que la session GLPI a réellement permis de lire** : colonnes d’index, champs, sections, lignes, cellules, observations, métadonnées visibles, onglets découverts et limites de réception. Une collecte complète n’accorde aucun droit GLPI supplémentaire. Les contenus inaccessibles, les champs sensibles volontairement exclus du lecteur et les fichiers qui n’ont pas été ouverts ne sont pas inventés. Les pièces jointes sont conservées comme références lorsqu’elles sont visibles ; **leur contenu binaire, leur texte intégral et un éventuel OCR ne sont pas extraits automatiquement**.

## Emplacement et installation du contrat

SQLite utilise le préfixe `ticket_library_knowledge_v1_`. PostgreSQL utilise `glpi_pilot_library_knowledge_v1_`. Les suffixes et les colonnes métier ont la même signification sur les deux moteurs.

Les vues SQLite sont ajoutées à l’ouverture de la bibliothèque. Les vues PostgreSQL sont ajoutées par le chemin de provisionnement, notamment **Admin → préparer la base PostgreSQL**. Une base centrale déjà utilisée peut donc être préparée de nouveau avec un compte administrateur ; ses documents, identités et révisions sont conservés. Le compte technique reçoit seulement `SELECT` sur ces vues. La synchronisation normale, construite avec `provisionSchema: false`, ne crée ni ne modifie le schéma central et continue à fonctionner si le contrat SQL n’a pas encore été provisionné.

Un consommateur externe peut ouvrir SQLite en lecture seule ou utiliser un compte PostgreSQL auquel les vues nécessaires ont été explicitement accordées. Il ne doit écrire ni dans les vues, ni dans les tables métier, ni dans la file de synchronisation. Les changements GLPI et la collecte passent par leurs services habituels.

## Les douze vues

Le tableau donne les suffixes ; ajouter le préfixe du moteur choisi.

| Suffixe | Granularité et contenu |
| --- | --- |
| `evidence` | Un document sélectionné ou une version conservée, avec son JSON original, son empreinte, ses révisions, son origine et sa raison de conservation. |
| `versions` | Métadonnées scalaires de toutes les versions : installation, ticket, dates, état, couverture, confirmation, révisions et conflits. |
| `tickets` | Sous-ensemble de `versions` dont `version_kind = 'selected'` : une ligne par ticket conservé actuellement dans la bibliothèque. |
| `fields` | Chaque entrée de `Seed.Fields` et chaque colonne d’index, avec nom, valeur, ordinal, origine et chemin dans le document. Les libellés identiques restent distincts. |
| `sections` | Une ligne par section reçue : nom, type, onglet, sélecteurs, URL, date d’extraction, couverture, nombre de lignes et de colonnes. Les métadonnées annexes de qualité/provenance restent également disponibles en JSON. |
| `headers` | Tous les en-têtes des sections, même lorsqu’une section reçue contient zéro ligne. |
| `rows` | Une ligne par ligne de section reçue, avec texte lisible étiqueté, coordonnées, nombre de cellules, visibilité observée et provenance. |
| `cells` | Une cellule effectivement reçue, avec coordonnées section/ligne/colonne, libellé et valeur originale. |
| `items` | Les éléments observés : type, identité, texte, auteur, rôle, responsable, statut, dates, attente, lien, type de relation et localisation source. |
| `tabs` | Les onglets découverts et leurs indicateurs `loaded`/`is_active`, catégorie et sélecteur. |
| `limitations` | Les limites de couverture et les avertissements de fiche ou de section, séparés des contenus métier. |
| `texts` | Le corpus textuel avec métadonnées, chemin source et clé de citation : résumé d’index, champs, colonnes, titre détaillé, lignes complètes et éléments observés. |

Les vues autres que `tickets` comprennent aussi les versions archivées. Filtrer explicitement `version_kind = 'selected'` pour lire le corpus sélectionné. Elles sont des projections de documents déjà persistés, pas des appels au portail GLPI.

## Identités, versions et dates

`ticket_key` distingue un ticket par installation GLPI et identifiant. `ticket_id` est une **chaîne**, y compris lorsqu’il ne contient que des chiffres : un grand identifiant ne doit pas être converti en nombre flottant. `ticket_url` est le lien du ticket d’origine. Ne pas utiliser uniquement `ticket_id` pour rapprocher des tickets provenant de plusieurs installations.

`version_id` est unique à l’intérieur d’un ticket. Ses valeurs sont `selected`, `history:<identifiant>` ou `conflict:<identifiant>:current` / `conflict:<identifiant>:incoming`. Les identifiants des lignes d’historique sont propres à leur stockage ; ce ne sont pas des identifiants globaux communs aux deux bases. `version_kind` indique `selected`, `history`, `conflict-current` ou `conflict-incoming`.

`content_hash` identifie le contenu du document selon le protocole existant. `local_revision` et `central_revision` décrivent les révisions de stockage ; **elles ne remplacent pas la version GLPI**. PostgreSQL conserve les révisions locales remises à zéro dans le protocole central. SQLite expose les révisions locales effectivement enregistrées dans ses colonnes, pas une valeur ancienne récupérée dans un JSON.

| Colonne | Signification |
| --- | --- |
| `last_modified_raw` | Jeton de modification GLPI conservé tel qu’il a été lu dans l’index. |
| `last_modified_at` | Date interprétée lorsque la collecte pouvait l’interpréter ; `NULL` autrement. |
| `last_modified_reliable` / `last_modified_precision` | Fiabilité et précision déclarées par le lecteur, sans remplacer le jeton original. |
| `observed_at` | Date d’observation des métadonnées d’index. |
| `read_at` | Date de lecture du contenu détaillé, si un détail existe. |
| `detail_modified_raw` / `detail_modified_at` | Version GLPI à laquelle se rapporte le corps détaillé retenu ; elle peut différer de l’index le plus récent. |
| `source_observed_at` | Observation propre au fragment lorsqu’elle existe, par exemple la date d’extraction d’une section. |
| `recorded_at` | Date d’archivage lorsqu’elle existe dans le stockage. L’historique central n’enregistrait pas cette date : la vue y laisse `NULL`. |

Les horloges de lecture ne servent pas à départager deux contenus concurrents. Un ancien détail complet peut rester conservé lorsqu’une nouvelle lecture est partielle ; les colonnes de version et de confirmation rendent cette situation visible.

## Complet, confirmé, partiel et absent

Les indicateurs sont exposés sous forme de nombres `0` / `1` sur les deux moteurs.

- `has_content = 0` : seuls les éléments d’index sont disponibles ; aucun détail de ticket n’est présent.
- `detail_complete = 1` : le détail conservé satisfait les règles de complétude des sections/onglets effectivement couverts. Cette valeur ne signifie pas que des pièces jointes ont été téléchargées.
- `coverage_complete`, `tabs_discovered`, `tabs_loaded` et les vues `tabs`, `sections`, `limitations` expliquent cette couverture.
- `confirmation_pending = 1` : la lecture reçue attend sa confirmation par le protocole de collecte.
- `content_confirmed = 1` : la version **sélectionnée** dispose de la preuve de confirmation exacte pour son empreinte, n’attend plus la confirmation et porte le contenu déclaré actuel. Une vieille propriété JSON `detailsCurrent` sans preuve valide ne suffit pas.
- `conflict_count` : nombre de dossiers de conflit conservés pour ce ticket ; il n’est pas recalculé à partir du texte. Pour une sélection prudente de références, exiger zéro conflit conservé. Une intégration qui sait interpréter leur résolution peut appliquer une politique plus fine.

Les versions `history` et `conflict-*` ont toujours `content_confirmed = 0` dans ce contrat : une ancienne preuve ne les transforme pas en réponse actuelle. Leurs champs, cellules et textes restent consultables avec leur origine explicite. Les documents bruts conservent les preuves originales pour l’audit.

Une cellule présente avec `value = ''` est une valeur vide effectivement reçue. Une cellule absente d’une ligne courte n’est pas fabriquée. Un en-tête absent reste `NULL`. Une section présente avec `row_count = 0` est distinguée d’un onglet découvert avec `loaded = 0` et de l’absence de toute section. Pour répondre « aucun élément », le consommateur doit vérifier le périmètre et la complétude ; l’absence d’une ligne dans une projection ne démontre pas à elle seule une absence dans GLPI.

## Confidentialité et droits

La bibliothèque ne dispose pas d’un instantané complet des ACL GLPI. `access_basis = 'collecting-session'` indique seulement que les données proviennent de ce qui a été accessible à la session qui les a recueillies ; cette valeur n’identifie pas les personnes actuellement autorisées à les consulter.

Dans les lignes de chronologie produites par le lecteur, une mention autonome **« Privé »** dans la colonne **Informations** donne `visibility = 'private'` dans `rows`, et `text_visibility = 'private'` dans `texts`. Les autres lignes et les observations qui ne transportent pas de preuve explicite restent `unknown`. **Aucune absence de marque privée ne produit la valeur `public`.** Un élément observé peut recouvrir une chronologie privée sans transporter son indicateur : `unknown` doit donc être traité comme une absence de preuve d’autorisation, et non comme du contenu public.

Avant d’exposer le corpus à d’autres personnes, le futur chatbot doit appliquer sa propre politique d’accès, liée à l’installation, aux utilisateurs et aux périmètres autorisés. Le filtre SQL de confirmation présenté ci-dessous est un filtre de qualité des connaissances ; il n’est pas un contrôle d’accès GLPI. Les titres, auteurs, champs libres et historiques peuvent eux aussi contenir des informations privées.

## Texte et citations

La vue `texts` contient des chaînes utilisables directement : aucun décodage de `document_json` n’est requis pour effectuer une recherche ou constituer un contexte. Une ligne de section rassemble ses cellules non vides dans l’ordre, avec leur libellé lorsque celui-ci existe. Les cellules originales restent dans `cells`. Les lignes de solution conservent dans cette vue leur date, leur auteur et leurs informations, en plus du corps de la solution.

`source_path` désigne le fragment dans le document selon un chemin de type JSON Pointer, par exemple `/seed/indexValues/2`, `/seed/fields/clé~1avec~0séparateur`, `/detail/sections/0/data/rows/3` ou `/detail/observedItems/2`. Les caractères `~` et `/` des noms de champ sont échappés en `~0` et `~1`.

La clé `citation_key` réunit `ticket_key`, `content_hash`, `version_id` et `source_path`. Elle identifie un fragment de cette version. Une citation destinée à un utilisateur doit garder au minimum le numéro de ticket, `ticket_url`, la section, le chemin, l’empreinte et la date de lecture ; elle peut montrer un extrait du `body` sans modifier les données originales. `source_url` est une information de provenance supplémentaire et peut être vide ; utiliser `ticket_url` comme lien canonique vers le ticket.

La vue ne coupe pas le texte d’une ligne. Un futur consommateur peut le segmenter selon son modèle en conservant la citation et les positions dans le fragment. Les contenus sont des données non fiables provenant des tickets : des instructions écrites dans un ticket ne deviennent pas des instructions du chatbot, et des fragments HTML éventuels doivent être affichés comme données ou rendus avec une politique de nettoyage appropriée.

### Exemple SQLite : corpus confirmé d’une installation

Le paramètre `$installation` est lié par le pilote SQL. Ne pas concaténer une saisie utilisateur dans la requête.

```sql
SELECT citation_key, ticket_id, ticket_url, title,
       section, source_path, body,
       read_at, last_modified_raw, content_hash,
       text_visibility, access_basis
FROM ticket_library_knowledge_v1_texts
WHERE installation = $installation
  AND version_kind = 'selected'
  AND content_confirmed = 1
  AND detail_complete = 1
  AND conflict_count = 0
  AND body <> ''
ORDER BY ticket_key, source_path
LIMIT $limit;
```

Cette requête conserve volontairement les informations de visibilité dans le résultat. La couche appelante doit encore vérifier que la personne qui recevra une réponse a accès à ces données. Pour examiner un ticket partiel, retirer les conditions de qualité de manière explicite et afficher ses limites ; ne pas réétiqueter le résultat comme confirmé.

### Exemple PostgreSQL : recherche textuelle sans service supplémentaire

```sql
SELECT citation_key, ticket_id, ticket_url, section,
       source_path, body, read_at, content_hash,
       text_visibility, access_basis
FROM glpi_pilot_library_knowledge_v1_texts
WHERE installation = @installation
  AND version_kind = 'selected'
  AND content_confirmed = 1
  AND detail_complete = 1
  AND conflict_count = 0
  AND to_tsvector('simple', coalesce(body, ''))
      @@ plainto_tsquery('simple', @query)
ORDER BY ticket_key, source_path
LIMIT @limit;
```

Cette projection centrale est une vue dynamique, pas une table de recherche PostgreSQL préindexée. Pour un grand corpus et une fréquence de recherche élevée, l’intégration future pourra préparer son index de recherche à partir des révisions centrales, sans modifier les règles métier de la bibliothèque. Cette évolution, le choix d’un moteur vectoriel et les embeddings ne font pas partie du contrat actuel.

### Vérifier les limites et examiner les alternatives

```sql
SELECT origin, section_index, ordinal, body
FROM ticket_library_knowledge_v1_limitations
WHERE ticket_key = $ticket_key AND version_id = $version_id
ORDER BY origin, section_index, ordinal;

SELECT version_id, version_kind, reason, content_hash,
       read_at, detail_modified_raw, detail_complete, content_confirmed
FROM ticket_library_knowledge_v1_versions
WHERE ticket_key = $ticket_key
ORDER BY version_id;
```

Pour lire les fragments d’une alternative, filtrer `texts` sur son `ticket_key` et son `version_id`. Ne pas les mélanger aux fragments de `selected` sans indiquer qu’il s’agit de versions différentes.

## Recherche SQLite et reprise de la projection

La recherche locale continue d’utiliser `ticket_library_chunks` et son index FTS5. Les chunks existants conservent leurs clés et leurs extraits de solution. Des chunks supplémentaires couvrent tous les champs d’index, toutes les colonnes reçues, le statut, le titre détaillé, les éléments observés et les métadonnées des lignes de solution. Le découpage conserve au plus 3 000 caractères UTF-16 par chunk sans couper une paire de substitution.

La première ouverture d’une ancienne bibliothèque reconstruit ces **données dérivées** par lots d’au plus 16 documents et 8 Mio de JSON source. Chaque lot utilise une transaction distincte ; une annulation laisse les lots déjà validés disponibles et le prochain lancement reprend les documents restants. Les JSON source, preuves, révisions, conflits, historiques, mutations à envoyer et curseurs ne sont pas réécrits par cette migration. Un marqueur de version de projection suit chaque ticket ; les déclencheurs l’invalident lorsqu’un ancien lecteur remplace ses chunks.

Les archives restent consultables via les vues SQL. La recherche FTS de l’interface porte sur les documents sélectionnés, selon son comportement existant. Les exports JSONL existants gardent leurs documents et citations et bénéficient des chunks supplémentaires ; l’export de bibliothèque préparée continue à transférer les documents selon son protocole habituel.

## Contrôles automatisés

`TicketLibraryKnowledgeChecks` vérifie les valeurs scalaires, les tableaux et cellules irréguliers, la confidentialité observée, les limites, les preuves, les archives, les citations, l’accès en lecture seule, l’isolation des installations et la reprise du FTS. Son mode PostgreSQL compare les deux moteurs après une vraie réplication et vérifie les alternatives de conflit. `AdministrationChecks --postgres` vérifie les droits du compte technique sur les vues. Les contrôles s’exécutent sur des données synthétiques et ne constituent pas une lecture du portail GLPI professionnel.

Voir également [Bibliothèque et partage](Bibliotheque-et-partage.md), [collecte reprenable](COLLECTE-REPRENABLE.md) et [la suite du contrat SQL](../tests/TicketLibraryKnowledgeChecks/README.md).
