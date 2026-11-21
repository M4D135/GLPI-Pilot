# Fichier d’export pour Relais — version 1

Un fichier **`.relais`** est une base SQLite autonome, créée de zéro par GLPI-Pilot. Il contient les connaissances admissibles d’une seule installation GLPI et les métadonnées nécessaires à leur vérification et à leurs citations. Il ne contient ni copie de la base active, ni profil, paramètres de connexion, session, données d’équipe, archives ou versions en conflit.

Le fichier conserve du contenu métier confidentiel. `access_basis = 'collecting-session'` indique son origine, sans attester des droits actuels d’un destinataire. Les fragments restent `private` ou `unknown` ; l’absence de marque privée ne devient jamais une autorisation publique. Ce format n’est pas un procédé d’expurgation des secrets éventuellement écrits dans les tickets.

## Sélection et instantané

Le périmètre est **toute la bibliothèque locale admissible de l’installation canonique choisie**. Les filtres d’écran, une pagination, le dernier journal de collecte et le dernier périmètre de collecte ne limitent pas l’export.

Les tickets respectent toutes les conditions suivantes dans le contrat SQL v1 existant :

```sql
installation = $installation
AND version_id = 'selected'
AND version_kind = 'selected'
AND detail_complete = 1
AND content_confirmed = 1
AND confirmation_pending = 0
AND has_content = 1
AND conflict_count = 0
```

Seuls les fragments dont `body <> ''` sont copiés. Un ticket n’entre dans le fichier que s’il possède au moins un fragment exporté. Ses métadonnées et celles de ses fragments sont celles de la même version sélectionnée.

Une connexion SQLite source en lecture seule et **une seule transaction de lecture différée** couvrent les compteurs et les fragments. Les écritures validées encore présentes dans le WAL sont donc prises en compte sans copie, checkpoint, réparation ou migration de la source. Aucune preuve de confirmation, révision ou donnée de la bibliothèque n’est réécrite.

## Objets SQLite autorisés

Le fichier contient quatre tables métier et deux vues publiques :

| Objet | Type | Rôle |
| --- | --- | --- |
| `ticket_library_schema` | table | Ligne unique `singleton = 1`, `version = 1`. |
| `relais_manifest` | table | Manifeste décrit ci-dessous. |
| `relais_tickets` | table | Les dix métadonnées de chaque ticket exporté. |
| `relais_texts` | table | Les trente-trois colonnes de chaque fragment exporté. |
| `ticket_library_knowledge_v1_tickets` | vue | Projection explicite, sans transformation, des dix colonnes de `relais_tickets`. |
| `ticket_library_knowledge_v1_texts` | vue | Projection explicite, sans transformation, des trente-trois colonnes de `relais_texts`. |

Des index SQLite ordinaires et les auto-index des clés primaires sont autorisés. Aucun déclencheur, table virtuelle, FTS, vue d’historique, `document_json`, `seed_json` ou table opérationnelle de GLPI-Pilot n’est copié. Les deux vues publiques n’exécutent que des `SELECT` sur leurs tables respectives.

### Manifeste

`relais_manifest` contient exactement une ligne. Tous ses champs sont obligatoires et non nuls.

| Colonne | Type SQLite | Valeur ou définition |
| --- | --- | --- |
| `singleton` | INTEGER | `1`, clé primaire. |
| `protocol` | TEXT | `glpi-pilot-relais` |
| `version` | INTEGER | `1` |
| `knowledge_version` | INTEGER | `1` |
| `application_version` | TEXT | Version de GLPI-Pilot ayant créé le fichier. |
| `exported_at` | TEXT | Date UTC au format ISO 8601 avec décalage UTC explicite. |
| `installation` | TEXT | Installation GLPI canonique unique. |
| `scope` | TEXT | `installation-admissible` |
| `complete` | INTEGER | `1` : le fichier est complet pour sa sélection, sans troncature. |
| `ticket_count` | INTEGER | Nombre de tickets distincts ayant au moins un fragment exporté. |
| `text_count` | INTEGER | Nombre de fragments exportés. |
| `source_ticket_count` | INTEGER | Nombre de tickets sélectionnés de cette installation dans l’instantané source. |
| `excluded_ticket_count` | INTEGER | `source_ticket_count - ticket_count`. |
| `missing_content_count` | INTEGER | Tickets source dont `has_content` n’est pas `1`. |
| `incomplete_count` | INTEGER | Tickets source dont `detail_complete` n’est pas `1`. |
| `unconfirmed_count` | INTEGER | Tickets source dont `content_confirmed` n’est pas `1`. |
| `pending_count` | INTEGER | Tickets source dont `confirmation_pending` n’est pas `0`. |
| `conflict_count` | INTEGER | Tickets source dont `conflict_count` est strictement positif ; nombre de tickets, non de dossiers de conflit. |

Les compteurs de motifs se recoupent : leur somme ne donne pas le nombre d’exclus. Pour les compteurs source, un indicateur `NULL` signifie une preuve absente : contenu manquant, incomplet ou non confirmé ; un `confirmation_pending` nul est compté parmi les états non dégagés. Les statistiques source sont descriptives : Relais réapplique sa validation aux contenus reçus et ne s’appuie pas sur ces compteurs pour les admettre.

### Tickets

Ordre des colonnes dans la table et la vue :

```text
installation, ticket_key, version_id, version_kind, access_basis,
detail_complete, content_confirmed, confirmation_pending,
has_content, conflict_count
```

Les cinq premières colonnes sont `TEXT`, les cinq suivantes `INTEGER`. Elles sont non nulles. `ticket_key` est la clé primaire. Les indicateurs restent exactement ceux de la source admissible : `1`, `1`, `0`, `1`, `0`. `version_id` et `version_kind` valent `selected`, `access_basis` vaut `collecting-session`.

### Fragments

Ordre contractuel des colonnes dans la table et la vue :

```text
ticket_key, installation, ticket_id, ticket_url, title,
version_id, version_kind, content_hash, local_revision, central_revision,
read_at, last_modified_raw, last_modified_at, observed_at,
detail_modified_raw, detail_modified_at,
detail_complete, content_confirmed, confirmation_pending, has_content,
coverage_complete, conflict_count,
source_path, source_kind, section, section_key, row_index,
source_url, source_observed_at, text_visibility, access_basis,
body, citation_key
```

Les colonnes suivantes sont `INTEGER` : `detail_complete`, `content_confirmed`, `confirmation_pending`, `has_content`, `coverage_complete`, `conflict_count`, `row_index`. Toutes les autres sont `TEXT`. **Les identifiants de ticket et les deux révisions sont du texte décimal exact, sans conversion par un flottant.**

`citation_key` est la clé primaire ; `ticket_key` référence `relais_tickets.ticket_key`. Les valeurs absentes restent `NULL`, les valeurs vides restent vides. Les champs nécessaires à l’identité, à l’admission et à la citation ne peuvent pas être absents dans un fragment exporté. `ticket_key` est limité à 8 192 caractères, `source_path` à 32 768 ; ils ne contiennent pas de caractère nul. Les révisions sont nulles ou des chaînes de 1 à 20 chiffres décimaux. Les corps, chemins, clés de citation, dates, empreintes et valeurs de visibilité ne sont ni réécrits, ni tronqués, ni réétiquetés.

Le lien canonique `ticket_url` doit rester une fiche de ticket HTTP(S), de la même installation, avec un unique paramètre `id` correspondant exactement à l’identité numérique du ticket, strictement positive. L’installation est limitée à 2 048 caractères et les URL de métadonnées à 32 768 ; ces adresses ne contiennent ni espace brut, ni contrôle C0, ni DEL. Les URL de métadonnées contenant des identifiants intégrés ou des paramètres sensibles connus sont refusées ; les messages d’erreur n’en révèlent pas la valeur. Ce contrôle ne réécrit pas les références ni les preuves.

## Bornes et publication

| Limite | Valeur maximale |
| --- | --- |
| Fragments | 200 000 |
| JSON des fragments normalisés | 256 Mio ; un décompte UTF-8 conservateur peut refuser plus tôt. |
| Corps d’un fragment | 2 Mio UTF-8 |
| Fichier SQLite final | 512 Mio |

Tout dépassement fait échouer l’export entier. Aucun fichier tronqué n’est publié. GLPI-Pilot refuse aussi un export sans texte admissible et explique les compteurs disponibles. Un lecteur reste néanmoins tenu de traiter de manière sûre un fichier vide structurellement valide reçu d’une autre provenance.

Le fichier de destination doit être nouveau : une destination existante, des journaux déjà présents pour cette destination, la base source ou l’un de ses journaux sont refusés. Ces fichiers préexistants ne sont jamais supprimés. Le nom proposé par l’interface est horodaté.

GLPI-Pilot crée exclusivement un temporaire SQLite sur disque local, en mode journal `DELETE`, le ferme et le vérifie. Il transfère ensuite le fichier fermé vers un temporaire créé exclusivement à côté de la destination, puis le renomme **sans écrasement**. Ce transfert permet de choisir un dossier d’export réseau sans y exploiter une base SQLite active. Une annulation ou une erreur nettoie uniquement les temporaires appartenant à cette opération ; la source et les fichiers préexistants restent conservés.

Le SHA-256 du fichier livré peut accompagner le bilan d’export. Il permet de vérifier un transfert du fichier ; il ne remplace pas la validation des contenus et ne constitue pas une signature d’authenticité.

## Vérification croisée

La suite `RelaisExportChecks` produit son fichier à partir des classes réelles de GLPI-Pilot et de scénarios synthétiques. Une fixture textuelle accompagnée de sa provenance permet à la suite Relais de reconstruire le résultat du véritable export C# sans versionner une base binaire ni des données utilisateur.
