# Bibliothèque de tickets et partage d’équipe

GLPI Pilot conserve une bibliothèque SQLite sur chaque poste. Elle réunit les tickets recueillis dans GLPI, leur contenu consultable, les références utiles à la recherche et les contributions de l’équipe. Une base PostgreSQL commune peut synchroniser ces copies locales. La consultation du cache et la rédaction des contributions restent possibles lorsque GLPI ou PostgreSQL est indisponible.

Cette fonction suit l’architecture C# et WebView de l’application : acquisition, stockage, règles de version et synchronisation en C# ; pages locales HTML/CSS/JavaScript pour la consultation. Les moteurs .NET, SQLite et WebView nécessaires au client sont embarqués dans les packs Windows. Le serveur PostgreSQL se configure séparément.

## Commencer sur un poste

1. Ouvrir la session GLPI dans le navigateur piloté, avec le profil et les entités à consulter, puis afficher la liste des tickets.
2. Dans **Bibliothèque → Connexion**, vérifier le **Dossier de la bibliothèque**. Par défaut, le fichier est `%LOCALAPPDATA%\GLPI-Pilot\Library\glpi-pilot-library.db`.
3. Choisir le **Fuseau des dates GLPI**, correspondant au fuseau effectivement utilisé par GLPI, et l’ordre de ses dates si jour et mois peuvent être confondus. Ces réglages restent vides tant qu’ils n’ont pas été choisis ; ils ne sont pas déduits des préférences Windows.
4. Lancer **Aspirer tous les tickets**. Le premier passage lit l’index et les contenus accessibles ; les passages suivants peuvent réutiliser les contenus dont la version est encore suffisamment établie.
5. Consulter le bilan : tickets observés, contenus lus, réutilisés, lectures partielles, échecs et conflits. Les résultats déjà enregistrés restent conservés après une annulation.

Les paramètres de connexion restent dans `library.local.json`, dans le dossier de données de l’application. Changer le dossier de bibliothèque par l’interface utilise une sauvegarde SQLite vers un nouveau fichier ; la copie précédente est conservée. Une destination qui contient déjà une base n’est pas écrasée.

### Les trois opérations

| Action | Source lue | Effet |
|---|---|---|
| **Aspirer tous les tickets** / **Mettre à jour le cache** | Session GLPI ouverte | Réénumération des tickets et lecture des contenus nouveaux, modifiés ou à recontrôler. |
| **Connexion → Relire tous les contenus** | Session GLPI ouverte | Relecture forcée des contenus, même si le jeton de modification n’a pas changé. |
| **Synchroniser** | PostgreSQL configuré | Publication des éléments locaux en attente et récupération des versions centrales. |

La synchronisation automatique concerne les échanges avec PostgreSQL. Elle ne lance pas une aspiration périodique de GLPI. Elle fonctionne pendant que GLPI Pilot est ouvert, à l’intervalle configuré, avec cinq minutes par défaut. Une opération en cours ou une édition active peut différer ce passage ; la file locale est conservée.

## Ce que signifie « tous les tickets »

L’acquisition demande le critère natif **Tous** du champ **Statut**, après l’avoir trouvé dans le formulaire GLPI. Elle comprend les tickets nouveaux, en cours, résolus et clos. Elle conserve le profil et le périmètre d’entités actifs de la session ; elle exclut la corbeille. L’option, le formulaire GET et les liens de pagination doivent être reconnus dans la page. Un portail dont ces éléments ne peuvent pas être établis donne un refus ou un résultat partiel explicite.

La liste est parcourue jusqu’au total constaté ou à une limite de collecte. L’identité des tickets est tirée des liens de fiche du même portail, avec contrôle des doublons, des colonnes, des plages de pagination et du contexte. Une seconde lecture de l’index vérifie le périmètre après l’acquisition des détails. Ce parcours n’est pas un instantané transactionnel de GLPI : des changements pendant une longue collecte restent possibles.

La recherche de départ est conservée visuellement. Son GET observé est rejoué à la fin pour rétablir le filtre mémorisé par GLPI, y compris après une annulation, dans une tentative indépendante de trois secondes. Si cette restauration ne peut pas être confirmée, le bilan le précise.

### Contenus et couverture

Le lecteur parcourt les onglets de consultation reconnus et leurs paginations : description, champs, suivis, tâches, solutions, historique, liens et références de documents, selon ce que la session permet de consulter. Il vérifie l’association à la même fiche avant chaque navigation. Les boutons d’ajout, de modification, de suppression, de validation ou d’autres actions métier ne font pas partie du parcours.

Les pièces jointes sont conservées sous forme de métadonnées et de liens accessibles. Leur contenu binaire n’est pas téléchargé automatiquement. Cette limite est affichée sans présenter un PDF ou une image comme déjà analysé.

Chaque fiche garde le nombre d’onglets découverts et chargés, la couverture de ses sections et ses limites. Une pagination inconnue, un onglet non reconnu, des éléments encore masqués, une limite de lignes ou un changement de contexte empêchent de déclarer cette lecture complète. Les valeurs recueillies sont conservées telles que reçues ; une lecture partielle ne remplace pas silencieusement une copie complète antérieure.

## Mise à jour rapide et dates de modification

La colonne native de dernière modification est identifiée par son option GLPI `date_mod` ; sa valeur brute et sa précision sont conservées. L’ordre peut être choisi explicitement : jour/mois/année (`dmy`), mois/jour/année (`mdy`) ou année/mois/jour (`ymd`). Sans ce choix, une valeur comme `05/06/2026` reste ambiguë. Un ordre configuré ne retombe pas silencieusement sur un autre ordre et n’autorise pas une date impossible. Une date absente, encore ambiguë ou sans fuseau interprétable ne permet pas de sauter une lecture. La date de lecture du poste ne devient jamais la date de modification du ticket.

Le cache peut être réutilisé si le ticket, le jeton de modification, la précision et la version du lecteur correspondent, que le contenu précédent est complet et encore courant, et que cette lecture a eu lieu après la fin de l’intervalle représenté par la date, avec une marge de deux minutes. Une date affichée à `12:34` couvre la minute `12:34` ; une lecture à `12:35` est trop proche pour cette réutilisation. Une lecture suffisamment tardive doit aussi avoir moins que l’âge maximal configuré, sept jours par défaut.

Cette politique exploite la précision réellement disponible. Elle ne garantit pas de détecter chaque modification cachée derrière le même affichage à la minute. La relecture périodique et **Relire tous les contenus** permettent la réconciliation. Si deux lectures de la même version apparente donnent des contenus différents, les deux copies restent conservées comme conflit.

| Situation constatée | Traitement |
|---|---|
| Date source plus récente, comparable | Les métadonnées nouvelles invalident la fraîcheur de l’ancien détail ; celui-ci reste disponible jusqu’à une lecture appropriée. |
| Copie source plus ancienne, même si elle a été lue plus tard | Elle ne remplace pas la version plus récente. |
| Même date, contenus différents | Versions conservées et fraîcheur à recontrôler. |
| Dates inconnues ou intervalles de précision qui se chevauchent | Aucune priorité inventée à partir des horloges des postes. |
| Ticket absent du dernier index | La copie n’est pas supprimée : filtre, droits ou entités peuvent avoir changé. |

## Recherche, résolution et corpus RAG

**Bibliothèque** recherche localement dans les titres et les contenus conservés. **Résolution** limite les résultats aux tickets résolus ou clos dont le contenu est complet et courant. Une solution est signalée comme telle lorsqu’elle a été effectivement recueillie dans la rubrique correspondante. Chaque extrait garde son ticket, son installation, sa rubrique, sa ligne, son URL et sa date d’observation.

Le moteur utilise l’index plein texte SQLite FTS5 et un classement BM25. Le découpage du contenu produit des passages citables de 3 000 caractères au maximum. **Exporter le corpus RAG** écrit un JSONL UTF-8 avec un manifeste de protocole, les documents et leurs passages sourcés. Il peut alimenter un autre outil de recherche ou une chaîne RAG autorisée. Cette version fournit la recherche et le corpus ; elle n’envoie pas automatiquement ces contenus à un modèle distant et ne calcule pas d’index vectoriel.

**Utiliser le cache comme relevé** rend les métadonnées conservées disponibles pour les statistiques et les autres moteurs locaux. Si plusieurs installations sont présentes, en sélectionner une évite de confondre des numéros de tickets identiques. Le relevé garde sa provenance locale et ses observations de dates différentes ; il ne certifie pas l’exhaustivité actuelle de GLPI et ne remplace pas un relevé validé pour les comparaisons historiques strictes.

## Contributions de l’équipe

L’onglet **Équipe** conserve notes, commentaires, connaissances et affectations. Une contribution peut être liée à un ticket de la bibliothèque. Ces documents appartiennent à GLPI Pilot ; leur enregistrement ne modifie pas le ticket dans GLPI. Les états et l’auteur saisi permettent l’organisation de l’équipe, sans constituer une authentification d’identité GLPI ou Active Directory.

Les configurations partagées comprennent les indicateurs, tableaux de bord, requêtes et villes. Chaque poste examine les différences et choisit les conflits à conserver avant application. Les dossiers locaux, connexions PostgreSQL, mots de passe et profils de navigateur ne sont pas publiés dans ce transfert. Les statistiques partagées sont des observations datées, avec leur périmètre et leur qualification de complétude.

### Partager le travail depuis les outils

Les pages existantes proposent des actions dédiées. Elles publient la version enregistrée dans l’espace d’équipe local ; la prochaine synchronisation la transmet au serveur si PostgreSQL est activé.

| Action dans l’outil | Contenu publié | Effet d’**Examiner et reprendre** sur un autre poste |
|---|---|---|
| **Enregistrer et partager la note** | Texte enregistré et référence exacte du ticket ; pas de liste personnelle, rappel, épingle ni brouillon de suivi. | Ajout du texte à la note existante sans l’effacer. Un ticket non encore conservé peut être ajouté sans aspiration préalable de la bibliothèque. |
| **Enregistrer et partager la tournée** | Tournée enregistrée, tickets, étapes, horaires et données de planification. | Nouvelle identité et état **Brouillon** ; les tournées existantes sont conservées. Les validations, trajets routiers et observations météo précédents sont retirés de la copie. |
| **Enregistrer et partager la répartition** | Scénario enregistré avec les références de tickets univoques du relevé courant, intervenants proposés, durées et notes. | Sauvegarde exacte du scénario local précédent, puis reprise explicite du scénario partagé. |
| **Partager les compétences et capacités** | Compétences, secteurs, capacités, plages de présence, indisponibilités et estimations des identités présentes dans le relevé courant. | Remplacement de ces champs métier pour les identités nommées ; conservation des autres techniciens et des favoris, objectifs personnels, notes personnelles et notes de disponibilité du poste. |

Le document partagé porte une installation GLPI explicite. Une contribution d’une autre installation ne peut pas être appliquée dans le contexte courant. Les références de notes et de propositions sont validées par leurs fiches ; un numéro de ticket seul ne suffit pas. Les identités de techniciens restent celles du relevé : les homonymes et la validité des disponibilités déclarées doivent être vérifiés par l’équipe.

L’aperçu ne modifie aucun fichier local. Sa confirmation est liée à la version de la contribution, à l’empreinte du document local et au contexte de l’application ; elle expire après dix minutes et n’est utilisable qu’une fois. Un changement oblige à relancer la comparaison. L’aperçu détaille au maximum 80 propositions ou identités et annonce explicitement le nombre total lorsque cette limite est dépassée ; la reprise garde le document partagé entier. Une note qui dépasserait sa limite est refusée sans troncature.

Les sauvegardes précédant le remplacement d’un scénario ou l’application de données techniciens sont conservées dans `%LOCALAPPDATA%\GLPI-Pilot\SharedImports`. Elles contiennent la copie exacte du fichier local avant application. Une tournée reçue doit être rapprochée du relevé et de la configuration de villes du poste avant validation. Aucune de ces reprises n’envoie d’affectation dans GLPI ni ne déclenche l’exécution d’une tournée.

Les notes restent privées jusqu’à leur publication explicite. Une fois publiées, elles sont accessibles aux lecteurs de l’espace d’équipe et de sa base centrale selon les droits accordés. La publication ne confère pas de nouveaux droits de consultation ou de modification dans GLPI.

Les modifications exigent la révision du document ouvert. Un éditeur devenu ancien ne peut pas écraser la version déjà enregistrée. La suppression d’une contribution crée une marque de suppression qui se propage aux autres clients. Les versions concurrentes sont conservées dans la base centrale et transmises aux autres postes, y compris à un troisième client arrivé plus tard ; elles ne sont pas fusionnées automatiquement.

### Organiser les accès aux outils

**Personnaliser**, en bas de la barre de navigation ou dans la palette, permet de réordonner les groupes et les pages, de déplacer un raccourci dans **Épinglés** et de masquer des entrées. Les commandes clavier accompagnent le glisser-déposer. **Accueil** et **Réglages** restent disponibles dans le menu ; les pages masquées restent accessibles par la palette.

Les changements ne sont appliqués qu’avec **Enregistrer le menu**. **Réinitialiser le menu** modifie le brouillon de l’éditeur, puis demande cet enregistrement. Cette organisation reste propre au poste et n’est pas diffusée avec les configurations métier de l’équipe.

## Installer PostgreSQL pour une équipe

### Prérequis administrateur

Prévoir un serveur PostgreSQL 17 maintenu, une base dédiée, un nom DNS correspondant à son certificat TLS, un rôle applicatif limité à cette base, les sauvegardes et une règle de pare-feu limitée aux postes ou au réseau de l’équipe. Aucun serveur privé n’est préconfiguré dans les packs.

L’accès central partage les documents déposés par les clients autorisés. **Les autorisations PostgreSQL ne reproduisent pas les ACL de GLPI.** Le compte qui lit un ticket dans GLPI et les personnes qui peuvent lire la base commune peuvent avoir des périmètres différents. Choisir les membres de la base et le périmètre collecté en conséquence ; séparer les bases lorsque les groupes n’ont pas le droit de consulter les mêmes données.

Exemple à exécuter par l’administrateur dans `psql`, avec des noms à adapter. `\password` demande le secret au terminal ; aucun mot de passe de production ne doit être enregistré dans ce document ou dans le dépôt.

```sql
CREATE ROLE glpi_pilot_client LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
\password glpi_pilot_client
CREATE DATABASE glpi_pilot OWNER glpi_pilot_client;
REVOKE ALL ON DATABASE glpi_pilot FROM PUBLIC;
GRANT CONNECT ON DATABASE glpi_pilot TO glpi_pilot_client;
\connect glpi_pilot
REVOKE ALL ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO glpi_pilot_client;
GRANT USAGE, CREATE ON SCHEMA public TO glpi_pilot_client;
ALTER ROLE glpi_pilot_client IN DATABASE glpi_pilot SET search_path = public;
```

Le client crée ses tables versionnées lors de la première synchronisation ; le rôle doit donc disposer des droits de création dans son schéma dédié. Les tables `glpi_pilot_library_*` et `gp_shared_*` ne doivent pas être modifiées par un outil tiers pendant la synchronisation.

Activer TLS côté serveur, installer le certificat et sa clé avec les droits de fichiers appropriés, puis limiter `listen_addresses` et le pare-feu aux interfaces nécessaires. Dans `pg_hba.conf`, utiliser une règle `hostssl` sur cette base, ce rôle et le CIDR réel de l’équipe, avec `scram-sha-256`. Éviter qu’une règle plus large antérieure autorise ce même accès sans TLS : PostgreSQL retient la première règle correspondante. Les références officielles en fin de document détaillent ces paramètres.

### Configurer chaque client

Dans **Bibliothèque → Connexion**, activer PostgreSQL et renseigner le même hôte, port, base et utilisateur applicatif sur les postes concernés. Conserver **VerifyFull**, le mode par défaut : le certificat du serveur et son nom doivent être validables par le poste. Installer l’autorité de certification interne dans le magasin de confiance utilisé par Windows lorsque nécessaire. **Require** chiffre la connexion sans apporter la même vérification d’identité ; **Disable** est destiné à un environnement local de test explicitement maîtrisé.

Le bouton de test vérifie l’ouverture de la connexion et une requête simple. Il ne certifie pas les droits de création du schéma, qui sont exercés lors de la synchronisation. Enregistrer les réglages, lancer **Synchroniser**, puis vérifier sur un second poste que les mêmes documents deviennent consultables.

Le mot de passe peut rester en mémoire pour la session. La mémorisation facultative utilise la protection du compte Windows et le lie à la destination choisie. Il n’est pas retourné à la WebView ni inclus dans les configurations partagées. Changer de serveur ou d’utilisateur nécessite le secret approprié à la nouvelle connexion.

Chaque poste garde **son fichier SQLite sur un disque local**. Un chemin UNC, un lecteur réseau ou une liaison de fichiers est refusé pour ce cache. Samba peut recevoir un export ou une sauvegarde fermée ; il ne doit pas héberger un fichier SQLite ouvert simultanément par plusieurs clients.

### Synchronisation et reprise

Les écritures locales deviennent des mutations identifiées, conservées dans une file d’envoi transactionnelle. Un accusé perdu peut être rejoué sans créer une seconde modification. Les curseurs sont enregistrés avec les données reçues, et les révisions centrales sont publiées dans l’ordre de validation des transactions. Une interruption laisse les éléments non confirmés disponibles pour le passage suivant.

Les tickets sont arbitrés selon leur version GLPI comparable. Les contributions d’équipe utilisent leur révision centrale attendue. Les deux mécanismes conservent les conflits. Changer d’identité de base centrale réinitialise la progression de lecture pour cette destination et remet les documents à publier ; cela ne transforme pas une copie ancienne en version source plus récente.

## Accès depuis d’autres outils

Privilégier les exports JSONL pour un corpus autonome. Pour une consultation SQL, créer un rôle de lecture et lui donner uniquement accès aux vues choisies. La vue `glpi_pilot_shared_items` est fournie par le protocole d’équipe. L’administrateur peut ajouter la vue suivante après la première synchronisation :

```sql
CREATE VIEW public.glpi_pilot_ticket_documents AS
SELECT ticket_key, revision,
       document_json::jsonb ->> 'installation' AS installation,
       document_json::jsonb #>> '{seed,ticket,id}' AS ticket_id,
       document_json::jsonb #>> '{seed,title}' AS title,
       document_json::jsonb #>> '{seed,status}' AS status,
       document_json::jsonb AS document
FROM public.glpi_pilot_library_tickets;

CREATE ROLE glpi_pilot_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
\password glpi_pilot_reader
GRANT CONNECT ON DATABASE glpi_pilot TO glpi_pilot_reader;
GRANT USAGE ON SCHEMA public TO glpi_pilot_reader;
GRANT SELECT ON public.glpi_pilot_ticket_documents,
                public.glpi_pilot_shared_items TO glpi_pilot_reader;
```

Le lecteur peut interroger les documents et leurs champs JSON sans droit d’écriture sur le protocole. La vue d’équipe inclut les marques de suppression ; filtrer `WHERE NOT deleted` pour une liste courante. Les documents portent un format versionné : vérifier ce format avant d’interpréter leur structure dans un autre logiciel. Un export RAG n’est pas une sauvegarde complète de l’historique, des conflits et des files de synchronisation.

## Sauvegardes et exploitation

Sauvegarder la base centrale avec `pg_dump` dans un format d’archive et tester sa restauration sur une base séparée. Par exemple, après adaptation du nom DNS et de l’utilisateur :

```sh
pg_dump --dbname="host=db.example.invalid dbname=glpi_pilot user=glpi_pilot_client sslmode=verify-full" --format=custom --file=glpi-pilot.dump --password
pg_restore --list glpi-pilot.dump
```

La liste de l’archive confirme qu’elle est lisible ; un essai de restauration reste nécessaire pour valider la procédure de reprise. Sauvegarder aussi les rôles et paramètres du serveur selon la procédure d’administration PostgreSQL.

Pour un cache local, fermer GLPI Pilot puis copier le dossier de bibliothèque complet, avec les fichiers SQLite auxiliaires s’ils subsistent. Une copie du seul `.db` pendant qu’il est ouvert peut omettre des transactions présentes dans le journal WAL. Les sauvegardes et exports contiennent les contenus métier recueillis : appliquer les mêmes droits d’accès que pour la base.

Le cache central ou local peut conserver des tickets devenus inaccessibles dans GLPI. Une disparition de l’index n’est pas traitée comme une instruction de suppression. La politique de conservation, de purge et les sauvegardes relèvent donc de l’exploitation de la bibliothèque partagée.

## Vérifications et sources

Les suites `TicketLibraryChecks`, `SharedWorkspaceChecks`, `TicketLibraryAcquisitionChecks`, `RuntimeSessionChecks` et le parcours `WebViewChecks --library-only` couvrent le stockage, les versions, le navigateur et les interactions. Le workflow exige les validations Windows et un serveur PostgreSQL 17 éphémère avant de publier les packs. Un parcours synthétique ou une CI réussie ne prouve pas la couverture d’une installation privée ; le bilan de chaque collecte conserve les limites réellement observées.

Sources primaires utilisées pour le protocole de navigation et l’installation :

- [GLPI 10.0.22 : statuts et recherche des tickets](https://github.com/glpi-project/glpi/blob/10.0.22/src/Ticket.php), [champs de recherche et date de modification](https://github.com/glpi-project/glpi/blob/10.0.22/src/CommonITILObject.php), [pagination native](https://github.com/glpi-project/glpi/blob/10.0.22/templates/components/pager.html.twig).
- [PostgreSQL 17 : authentification et ordre des règles](https://www.postgresql.org/docs/17/auth-pg-hba-conf.html), [configuration TLS](https://www.postgresql.org/docs/17/ssl-tcp.html), [rôles](https://www.postgresql.org/docs/17/sql-createrole.html), [sauvegarde pg_dump](https://www.postgresql.org/docs/17/app-pgdump.html).
- [Npgsql : sécurité et modes de connexion](https://www.npgsql.org/doc/security.html).
- [SQLite : sauvegarde en ligne](https://www.sqlite.org/backup.html), [journal WAL](https://www.sqlite.org/wal.html).
