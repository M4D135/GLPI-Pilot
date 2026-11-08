# Bibliothèque de tickets et partage d’équipe

GLPI Pilot conserve une bibliothèque SQLite sur chaque poste. Elle réunit les tickets recueillis dans GLPI, leur contenu consultable, les références utiles à la recherche, les interventions et les contributions de l’équipe. Le partage est facultatif : un **dossier réseau commun** ou un **serveur PostgreSQL** permet de synchroniser les copies locales de plusieurs sessions Windows et de plusieurs machines. Toutes les fonctions restent disponibles en mode local, et le travail enregistré est conservé pendant une coupure du partage.

Cette fonction suit l’architecture C# et WebView de l’application : acquisition, stockage, règles de version et synchronisation en C# ; pages locales HTML/CSS/JavaScript pour la consultation. Les moteurs .NET, SQLite et WebView nécessaires au client sont embarqués dans les packs Windows. Le mode dossier utilise les droits Windows existants ; il ne demande pas de créer un compte dans GLPI Pilot. PostgreSQL reste une autre possibilité, avec son serveur configuré séparément.

## Choisir le mode de fonctionnement

Dans **Réglages → Partage entre postes**, choisir un des trois modes :

| Mode | Données de travail | Accès nécessaire |
|---|---|---|
| **Sur ce poste uniquement** | Copie locale du profil utilisateur, sans échange automatique. | Aucun service de partage. |
| **Dossier réseau partagé** | Copie locale et registre commun `GLPI-Pilot-Shared` dans le dossier choisi. | Même dossier accessible en lecture et en écriture par les sessions Windows concernées. |
| **Serveur PostgreSQL** | Copie locale et base centrale commune. | Connexion PostgreSQL préparée pour l’équipe. |

Le calendrier, les sites et les autres outils n’imposent pas de partage pour fonctionner. L’activation concerne les données métier communes ; les réglages privés et les sessions de navigateur restent propres au poste. Il n’existe pas de mode chef ou technicien qui limite les fonctions : l’identité personnelle choisie dans l’interface sert aux filtres.

La case **Synchroniser automatiquement** active ou désactive les échanges périodiques. Leur fréquence se règle avec **Intervalle de synchronisation (minutes)**, de **1 à 1 440 minutes** ; **5 minutes** est uniquement la valeur initiale. Une fois l’automatisme désactivé, **Synchroniser** permet encore un échange manuel. Pour désactiver aussi le partage, sélectionner **Sur ce poste uniquement**, puis enregistrer le réglage.

### Configurer un dossier commun

1. Préparer un dossier partagé existant, par exemple `\\serveur\equipe\GLPI-Pilot`, avec les droits de lecture et d’écriture pour les utilisateurs Windows concernés.
2. Sur chaque poste, ouvrir **Réglages → Partage entre postes**, choisir **Dossier réseau partagé**, puis sélectionner ou saisir ce même dossier. Un lecteur réseau monté est possible ; vérifier qu’il désigne bien le même emplacement pour toutes les sessions. Le chemin UNC évite les différences de lettres de lecteurs.
3. Conserver le **Dossier de la bibliothèque** sur le disque local du profil. Le dossier de partage est un réglage séparé.
4. Utiliser **Tester la connexion**. Le test contrôle l’ouverture et l’écriture du registre ; il prépare le sous-dossier `GLPI-Pilot-Shared` sans envoyer le corpus métier.
5. Choisir **Enregistrer le partage**, puis **Synchroniser**. Vérifier les compteurs et la dernière réussite. Sur le second poste, choisir le même dossier et synchroniser ; les données communes doivent apparaître.

L’application crée le sous-dossier dédié mais ne recrée pas un dossier parent absent. Un lecteur réseau démonté ou un partage indisponible ne doit pas être remplacé par un nouveau registre local portant un chemin ressemblant.

Le dossier commun contient des transactions JSON fermées, des identités de registre et des fichiers techniques de coordination. Les fichiers SQLite de travail restent sur chaque poste. Les écritures du registre utilisent un verrou interprocessus, des accusés rejouables et une validation atomique des fichiers. Ne pas modifier ou déplacer un fichier interne du registre isolément ; un contenu incomplet ou corrompu bloque l’échange en conservant la file locale.

Un index de lecture est conservé dans `SharedIndexes`, sous le dossier local de bibliothèque. Il permet de consulter les anciennes versions sans charger tout leur contenu en mémoire. Cet index ne fait pas autorité : s’il manque ou devient illisible, il est reconstruit depuis le journal commun validé. La première reconstruction peut prendre du temps et nécessite de l’espace sur le disque local ; un journal commun altéré reste signalé et n’est pas réparé silencieusement depuis ce cache.

La destination commune détermine les personnes pouvant consulter les données déposées. Ses droits Windows ne reproduisent pas les droits GLPI ticket par ticket. Choisir un dossier adapté aux données de l’équipe. Aucun mot de passe GLPI ou jeton de session n’est diffusé pour accorder cet accès.

Utiliser la **même version 3.5 de GLPI Pilot** sur les postes d’un même espace. Les lots du nouveau calendrier et les documents métier automatiques nécessitent un protocole compatible. Les tests locaux vérifient les transactions entre processus, la concurrence et la reprise ; ils ne constituent pas une qualification d’un serveur SMB particulier. Un essai avec deux postes sur le partage réel reste nécessaire avant usage courant.

## Commencer sur un poste

1. Ouvrir la session GLPI dans le navigateur piloté, avec le profil et les entités à consulter, puis afficher la liste des tickets.
2. Dans **Réglages → Partage entre postes**, vérifier le **Dossier de la bibliothèque**. Par défaut, le fichier est `%LOCALAPPDATA%\GLPI-Pilot\Library\glpi-pilot-library.db`.
3. Choisir le **Fuseau des dates GLPI**, correspondant au fuseau effectivement utilisé par GLPI, et l’ordre de ses dates si jour et mois peuvent être confondus. Ces réglages restent vides tant qu’ils n’ont pas été choisis ; ils ne sont pas déduits des préférences Windows.
4. Lancer **Aspirer tous les tickets**. Le premier passage lit l’index et les contenus accessibles ; les passages suivants peuvent réutiliser les contenus dont la version est encore suffisamment établie.
5. Consulter le bilan : tickets observés, contenus lus, réutilisés, lectures partielles, échecs et conflits. Les résultats déjà enregistrés restent conservés après une annulation.

Les paramètres de connexion restent dans `library.local.json`, dans le dossier de données de l’application. Changer le dossier de bibliothèque par l’interface utilise une sauvegarde SQLite vers un nouveau fichier ; la copie précédente est conservée. Une destination qui contient déjà une base n’est pas écrasée.

### Collecte et synchronisation

| Action | Source lue | Effet |
|---|---|---|
| **Aspirer tous les tickets** / **Mettre à jour le cache** | Session GLPI ouverte | Réénumération des tickets et lecture des contenus nouveaux, modifiés ou à recontrôler. |
| **Connexion → Relire tous les contenus** | Session GLPI ouverte | Relecture forcée des contenus, même si le jeton de modification n’a pas changé. |
| **Dernière collecte → Reprendre les tickets à relire** | Index GLPI réactualisé dans le contexte du bilan | Nouvelle tentative pour les fiches en échec, partielles ou non confirmées encore accessibles. |
| **Synchroniser** | Dossier partagé ou PostgreSQL configuré | Publication des éléments locaux en attente et récupération des versions communes. |

La synchronisation automatique concerne le mode de partage choisi. Elle ne lance pas une aspiration périodique de GLPI. Elle fonctionne pendant que GLPI Pilot est ouvert, à l’intervalle configuré, avec cinq minutes par défaut et un premier passage dès que l’application est disponible au démarrage. Une opération en cours, une session Windows verrouillée ou une édition active peut différer ce passage ; la file locale est conservée.

Le bloc de stockage affiche le mode, l’état, les éléments en attente, la dernière synchronisation complète réussie et le prochain passage prévu. La dernière réussite appartient à la destination actuelle : changer de dossier ou de base ne reprend pas abusivement l’heure d’une autre destination. Un échange partiel ne devient pas une réussite complète.

En cas de coupure, les modifications métier sont préparées dans la file locale avant la tentative d’échange. Au retour du réseau, **Synchroniser** ou le prochain passage automatique reprend les éléments non confirmés. Les pages reçoivent les changements après la réception ; une saisie ouverte est conservée et sa révision sera vérifiée à l’enregistrement. Il s’agit d’une synchronisation périodique, pas d’un affichage instantané entre postes.

## Données communes et données personnelles

Lorsque le partage est activé, les données métier suivantes sont reprises automatiquement. Elles gardent des identités stables pour que les utilisateurs travaillent sur les mêmes objets.

| Donnée | Comportement partagé |
|---|---|
| Interventions et ressources du calendrier | Rendez-vous, participants, liens de tickets, récurrences, exceptions et historique. Les opérations de série ou de groupe restent indivisibles. |
| Référentiel des lieux | Sites, communes, adresses, coordonnées, termes de détection et ouvertures déclarés dans la configuration commune. |
| Règles de pilotage | Correspondances d’équipes, indicateurs et seuils météo, tableaux de bord, requêtes et règles de calcul de la configuration métier. |
| Tournées enregistrées | Même identité de tournée, étapes, tickets et horaires ; une modification reçue met à jour cet objet commun. |
| Données métier des intervenants | Compétences, secteurs, capacités, plages de présence, indisponibilités et estimations déclarées. Les notes et préférences personnelles restent séparées. |
| Scénario de répartition | Propositions et organisation du scénario enregistré ; aucune affectation GLPI n’est envoyée par cette synchronisation. |
| Corrections de localisation et ordre des tournées | Corrections manuelles connues et ordres enregistrés, avec leurs références. |
| Relevés de tickets | Instantanés du tableau source avec leurs colonnes, valeurs, identité de source et règles d’interprétation. Le catalogue de relevés permet de choisir la source à ouvrir. |
| Corpus de bibliothèque | Index et contenus effectivement collectés dans GLPI, références de source, couverture, versions et conflits. |
| Contributions publiées | Notes, commentaires, connaissances et documents explicitement déposés dans l’espace d’équipe. |

Les **relevés partagés** et le **corpus de bibliothèque** ont deux usages distincts. Un relevé conserve le tableau exact qui alimente les chiffres et les vues de pilotage. Le corpus assemble des observations de tickets et de fiches recueillies à des dates potentiellement différentes ; il ne prouve pas à lui seul l’exhaustivité d’un relevé à une date donnée. La synchronisation ne transforme pas les contenus absents d’un fichier CSV en détails GLPI observés.

Un poste sans tableau ouvert peut reprendre le relevé commun correspondant à sa source ou l’unique relevé disponible. Le catalogue permet une sélection explicite lorsqu’il existe plusieurs sources. Un relevé partagé choisi suit ses prochaines versions ; un tableau de travail différent déjà ouvert n’est pas remplacé silencieusement. Les données historiques restent des observations datées.

Restent propres au profil : l’identité choisie pour **Mes interventions**, les notes et listes personnelles non publiées, les épingles, rappels personnels, positions de lecture et préférences d’interface. Les profils de navigateur, cookies SSO, mots de passe, paramètres de connexion, dossiers de travail et réglages techniques des fournisseurs externes restent locaux. Une note personnelle peut toujours être publiée volontairement par l’action dédiée.

La configuration reçue conserve ces champs privés. Les fichiers métier remplacés sont sauvegardés dans `%LOCALAPPDATA%\GLPI-Pilot\SharedImports` avant application. Une modification concurrente est conservée pour comparaison ; elle ne disparaît pas parce qu’un autre ordinateur avait une heure plus récente.

Pour le calendrier, **Versions à examiner** montre et résout le lot complet, y compris les nouveaux fragments de série refusés. Pour les autres données communes, l’interface de partage propose la comparaison et la résolution des versions métier. Une confirmation reste liée aux révisions examinées. Les règles de calendrier sont détaillées dans [Calendrier et interventions](Calendrier-et-interventions.md).

### Simulation à partir d’un import

Le mode simulation issu d’un fichier Excel ou CSV dispose de sa bibliothèque, de ses interventions et de ses données métier isolées. Le partage, ses paramètres, son test et ses répliques y sont simulés. Le dossier partagé est remplacé par un chemin du profil temporaire et les échanges passent par des répliques en mémoire. Un serveur ou un chemin réseau réel saisi pendant la simulation n’est pas contacté. Quitter ce mode retrouve le profil habituel.

### Comprendre le bilan et reprendre

Le bloc **Dernière collecte** survit au redémarrage. **Détail de la collecte** affiche les compteurs, les limites du périmètre et les résultats ticket par ticket, par pages de 25. Le filtre **Afficher uniquement les tickets à relire** retrouve les tentatives non abouties. Les motifs restent des messages fixes sans contenu de cellule.

Le journal retient jusqu’à 20 bilans, en préservant celui utilisé par une reprise en cours. Cette rotation ne supprime pas les contenus des tickets. Le journal appartient au poste ; les documents de ticket et leurs confirmations ont leur propre synchronisation.

Une lecture partielle dans ce bilan décrit cet essai, même lorsqu’une ancienne fiche complète reste dans le cache. Une annulation conserve les lectures terminées et les tickets restant à traiter. Après une fermeture brutale, une tentative abandonnée est indiquée comme interrompue.

La reprise est liée au contexte d’origine. Elle renouvelle l’index avant toute lecture de fiche. Les tickets qui n’apparaissent plus dans cet index sont indiqués comme absents ; leur contenu local n’est ni supprimé ni déclaré encore accessible. Changer d’installation ou de configuration rend cette reprise incompatible : lancer une nouvelle collecte depuis le contexte souhaité.

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

Une nouvelle lecture conserve d’abord le contenu avec une fraîcheur non confirmée. Le contrôle final doit confirmer la version et le périmètre pour la tentative correspondante. Cette confirmation se synchronise séparément avec les autres postes et ne relève pas un état issu d’un conflit. Les anciennes copies restent consultables ; la signature de lecture et les preuves introduites en 3.2.1 nécessitent une relecture avant leur réutilisation comme contenu confirmé. Mettre les clients d’une même base à jour pour appliquer les mêmes règles.

| Situation constatée | Traitement |
|---|---|
| Date source plus récente, comparable | Les métadonnées nouvelles invalident la fraîcheur de l’ancien détail ; celui-ci reste disponible jusqu’à une lecture appropriée. |
| Copie source plus ancienne, même si elle a été lue plus tard | Elle ne remplace pas la version plus récente. |
| Même date, contenus différents | Versions conservées et fraîcheur à recontrôler. |
| Dates inconnues ou intervalles de précision qui se chevauchent | Aucune priorité inventée à partir des horloges des postes. |
| Ticket absent du dernier index | La copie n’est pas supprimée : filtre, droits ou entités peuvent avoir changé. |

## Recherche, résolution et corpus RAG

**Bibliothèque** recherche localement dans les titres et les contenus conservés. **Résolution** limite les résultats aux tickets résolus ou clos dont le contenu est complet et courant. Une solution est signalée comme telle lorsqu’elle a été effectivement recueillie dans la rubrique correspondante. Chaque extrait garde son ticket, son installation, sa rubrique, sa ligne, son URL et sa date d’observation.

**Ctrl+K** permet la même recherche depuis les autres écrans. La palette sépare écrans, actions et tickets conservés, donne priorité au numéro exact et affiche jusqu’à huit tickets. Le filtre d’installation distingue les numéros identiques de portails différents. Ouvrir un contenu conservé laisse en place la saisie, le focus de retour et le relevé ; **Ouvrir la bibliothèque filtrée** rejoint les résultats complets. Les flèches et Entrée choisissent une proposition, Échap ferme le dialogue.

Depuis une fiche, la recherche de **précédents résolus** utilise les termes distinctifs du titre conservé et reste dans la même installation. Elle exclut la fiche de départ ainsi que les tickets actifs, les contenus partiels, périmés, en conflit ou non confirmés. Une correspondance automatique exige deux termes utiles communs ou un code d’erreur alphanumérique exact ; la catégorie ne fait que départager des propositions déjà pertinentes. La recherche manuelle permet de préciser le symptôme ou l’équipement, y compris avec un seul terme choisi explicitement.

Les propositions expliquent les termes communs et présentent les extraits exacts conservés, en privilégiant les solutions recueillies. La lecture d’un précédent et le retour au ticket initial conservent le travail de l’écran d’origine. Un titre trop général appelle une recherche manuelle ; l’ancien contenu non confirmé du ticket source ne sert pas à enrichir automatiquement la requête. Le moteur examine au maximum 100 candidats, recontrôle jusqu’à 16 documents avec leurs preuves et restitue huit propositions au plus. L’interface indique les limites rencontrées. Cette recherche aide à retrouver une procédure à vérifier ; elle ne génère pas de solution ni de score de confiance.

Dans l’écran **Tickets**, **Chercher des solutions similaires** retrouve la fiche de la bibliothèque à partir de son numéro et de son adresse GLPI validés ensemble, puis ouvre le même lecteur de précédents. Si elle n’est pas encore dans ce cache, le dialogue l’indique sans ouvrir une fiche homonyme d’une autre installation, sans lancer de collecte et sans quitter le travail en cours.

Le moteur utilise l’index plein texte SQLite FTS5 et un classement BM25. Le découpage du contenu produit des passages citables de 3 000 caractères au maximum. **Exporter le corpus RAG** écrit un JSONL UTF-8 avec un manifeste de protocole, les documents et leurs passages sourcés. Il peut alimenter un autre outil de recherche ou une chaîne RAG autorisée. Cette version fournit la recherche et le corpus ; elle n’envoie pas automatiquement ces contenus à un modèle distant et ne calcule pas d’index vectoriel.

**Utiliser le cache comme relevé** rend les métadonnées conservées disponibles pour les statistiques et les autres moteurs locaux. Si plusieurs installations sont présentes, en sélectionner une évite de confondre des numéros de tickets identiques. Le relevé garde sa provenance locale et ses observations de dates différentes ; il ne certifie pas l’exhaustivité actuelle de GLPI et ne remplace pas un relevé validé pour les comparaisons historiques strictes.

## Contributions de l’équipe

L’onglet **Équipe** conserve notes, commentaires, connaissances et affectations. Une contribution peut être liée à un ticket de la bibliothèque. Ces documents appartiennent à GLPI Pilot ; leur enregistrement ne modifie pas le ticket dans GLPI. Les états et l’auteur saisi permettent l’organisation de l’équipe, sans constituer une authentification d’identité GLPI ou Active Directory.

Les publications explicites de configuration comprennent les indicateurs, tableaux de bord, requêtes et villes. Elles conservent leur aperçu de comparaison et leur application volontaire, en complément du partage automatique décrit plus haut. Les dossiers locaux, connexions PostgreSQL, mots de passe et profils de navigateur ne sont pas publiés dans ce transfert. Les statistiques publiées sont des observations datées, avec leur périmètre et leur qualification de complétude.

### Publier une contribution depuis les outils

Les outils proposent également des actions de publication explicite. Elles déposent une version dans l’espace d’équipe local ; la prochaine synchronisation la transmet au dossier ou au serveur choisi. Ces contributions servent notamment à la passation et à la reprise volontaire. Le planning commun et les tournées canoniques n’exigent pas cette publication supplémentaire pour se synchroniser.

| Action dans l’outil | Contenu publié | Effet d’**Examiner et reprendre** sur un autre poste |
|---|---|---|
| **Enregistrer et partager la note** | Texte enregistré et référence exacte du ticket ; pas de liste personnelle, rappel, épingle ni brouillon de suivi. | Ajout du texte à la note existante sans l’effacer. Un ticket non encore conservé peut être ajouté sans aspiration préalable de la bibliothèque. |
| **Enregistrer et partager la tournée** | Tournée enregistrée, tickets, étapes, horaires et données de planification. | Nouvelle identité et état **Brouillon** ; les tournées existantes sont conservées. Les validations, trajets routiers et observations météo précédents sont retirés de la copie. |
| **Enregistrer et partager la répartition** | Scénario enregistré avec les références de tickets univoques du relevé courant, intervenants proposés, durées et notes. | Sauvegarde exacte du scénario local précédent, puis reprise explicite du scénario partagé. |
| **Partager les compétences et capacités** | Compétences, secteurs, capacités, plages de présence, indisponibilités et estimations des identités présentes dans le relevé courant. | Remplacement de ces champs métier pour les identités nommées ; conservation des autres techniciens et des favoris, objectifs personnels, notes personnelles et notes de disponibilité du poste. |

Le document partagé porte une installation GLPI explicite. Une contribution d’une autre installation ne peut pas être appliquée dans le contexte courant. Les références de notes et de propositions sont validées par leurs fiches ; un numéro de ticket seul ne suffit pas. Les identités de techniciens restent celles du relevé : les homonymes et la validité des disponibilités déclarées doivent être vérifiés par l’équipe.

L’aperçu ne modifie aucun fichier local. Sa confirmation est liée à la version de la contribution, à l’empreinte du document local et au contexte de l’application ; elle expire après dix minutes et n’est utilisable qu’une fois. Un changement oblige à relancer la comparaison. L’aperçu détaille au maximum 80 propositions ou identités et annonce explicitement le nombre total lorsque cette limite est dépassée ; la reprise garde le document partagé entier. Une note qui dépasserait sa limite est refusée sans troncature.

Les sauvegardes précédant le remplacement d’un scénario ou l’application de données techniciens sont conservées dans `%LOCALAPPDATA%\GLPI-Pilot\SharedImports`. Elles contiennent la copie exacte du fichier local avant application. Une tournée reçue doit être rapprochée du relevé et de la configuration de villes du poste avant validation. Aucune de ces reprises n’envoie d’affectation dans GLPI ni ne déclenche l’exécution d’une tournée.

Les notes personnelles de l’écran **Tickets** restent privées jusqu’à leur publication explicite. Une contribution créée directement dans **Bibliothèque → Équipe** est destinée au partage dès son enregistrement, même avec l’état **Brouillon** ; cet état sert à l’organisation du travail. Une fois synchronisés, ces documents sont accessibles aux lecteurs de l’espace d’équipe et de sa base centrale selon les droits accordés. La publication ne confère pas de nouveaux droits de consultation ou de modification dans GLPI.

Les modifications exigent la révision du document ouvert. Un éditeur devenu ancien ne peut pas écraser la version déjà enregistrée. La suppression d’une contribution crée une marque de suppression qui se propage aux autres clients. Les versions concurrentes sont conservées dans la base centrale et transmises aux autres postes, y compris à un troisième client arrivé plus tard ; elles ne sont pas fusionnées automatiquement.

**Examiner les versions → Comparer tous les champs** détaille les différences des champs métier et des données structurées, y compris lorsque le résumé est identique. Les champs absents, valeurs nulles et textes vides sont distingués. L’aperçu peut être filtré ; il annonce ses limites. **Exporter les deux versions** conserve les deux documents entiers et leurs empreintes en JSON. Ces deux actions restent en lecture seule. Les paires plus anciennes sont accessibles par la pagination.

### Organiser les accès aux outils

**Personnaliser**, en bas de la barre de navigation ou dans la palette, permet de réordonner les six espaces à l’intérieur de leurs deux parties : **Accueil, Tickets, Interventions et Pilotage** d’une part, **Données et Réglages** d’autre part. Une étoile place un accès en tête de sa partie ; les flèches et le glisser-déposer permettent d’en modifier l’ordre. Les entrées peuvent être masquées, sauf **Accueil** et **Réglages**, qui restent disponibles dans le menu. Les espaces masqués restent accessibles par la palette.

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

Dans **Réglages → Partage entre postes**, choisir **Serveur PostgreSQL** et renseigner le même hôte, port, base et utilisateur applicatif sur les postes concernés. Conserver **VerifyFull**, le mode par défaut : le certificat du serveur et son nom doivent être validables par le poste. Installer l’autorité de certification interne dans le magasin de confiance utilisé par Windows lorsque nécessaire. **Require** chiffre la connexion sans apporter la même vérification d’identité ; **Disable** est destiné à un environnement local de test explicitement maîtrisé.

Le bouton de test vérifie l’ouverture de la connexion et une requête simple. Il ne certifie pas les droits de création du schéma, qui sont exercés lors de la synchronisation. Enregistrer les réglages, lancer **Synchroniser**, puis vérifier sur un second poste que les mêmes documents deviennent consultables.

Le mot de passe peut rester en mémoire pour la session. La mémorisation facultative utilise la protection du compte Windows et le lie à la destination choisie. Il n’est pas retourné à la WebView ni inclus dans les configurations partagées. Changer de serveur ou d’utilisateur nécessite le secret approprié à la nouvelle connexion.

Chaque poste garde **son fichier SQLite sur un disque local**. Un chemin UNC, un lecteur réseau ou une liaison de fichiers est refusé pour ce cache. Le mode **Dossier réseau partagé** échange des transactions fermées dans un emplacement distinct ; il ne place pas un fichier SQLite ouvert simultanément par plusieurs clients sur Samba.

### Synchronisation et reprise

Les écritures locales deviennent des mutations identifiées, conservées dans une file d’envoi transactionnelle. Un accusé perdu peut être rejoué sans créer une seconde modification. Les curseurs sont enregistrés avec les données reçues, et les révisions centrales sont publiées dans l’ordre de validation des transactions. Une interruption laisse les éléments non confirmés disponibles pour le passage suivant.

Les tickets sont arbitrés selon leur version GLPI comparable. Les contributions d’équipe utilisent leur révision commune attendue. Les deux mécanismes conservent les conflits. Changer d’identité de registre partagé ou de base centrale réinitialise la progression de lecture pour cette destination et remet les documents à publier ; cela ne transforme pas une copie ancienne en version source plus récente.

Une séparation de série et un déplacement groupé de calendrier sont transmis en un seul lot métier. Si une révision d’un membre est devenue ancienne, le lot entier est refusé et ses propositions sont conservées. La comparaison et la résolution concernent tous les membres ensemble. Les mêmes règles s’appliquent aux transports dossier, PostgreSQL et à la réplique de simulation.

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

Pour le **dossier partagé**, arrêter les échanges des postes pendant la copie et sauvegarder le sous-dossier `GLPI-Pilot-Shared` entier, avec son identité et ses journaux. Restaurer l’ensemble dans un emplacement de test et vérifier une réception sur un profil séparé. Ne pas mélanger des fragments de sauvegardes de dates différentes. Le cache local peut contenir des changements encore en attente qui ne sont pas dans la dernière sauvegarde commune.

Pour **PostgreSQL**, sauvegarder la base centrale avec `pg_dump` dans un format d’archive et tester sa restauration sur une base séparée. Par exemple, après adaptation du nom DNS et de l’utilisateur :

```sh
pg_dump --dbname="host=db.example.invalid dbname=glpi_pilot user=glpi_pilot_client sslmode=verify-full" --format=custom --file=glpi-pilot.dump --password
pg_restore --list glpi-pilot.dump
```

La liste de l’archive confirme qu’elle est lisible ; un essai de restauration reste nécessaire pour valider la procédure de reprise. Sauvegarder aussi les rôles et paramètres du serveur selon la procédure d’administration PostgreSQL.

Pour un cache local, fermer GLPI Pilot puis copier le dossier de bibliothèque complet, avec les fichiers SQLite auxiliaires s’ils subsistent. Une copie du seul `.db` pendant qu’il est ouvert peut omettre des transactions présentes dans le journal WAL. Les sauvegardes et exports contiennent les contenus métier recueillis : appliquer les mêmes droits d’accès que pour la base.

Le cache central ou local peut conserver des tickets devenus inaccessibles dans GLPI. Une disparition de l’index n’est pas traitée comme une instruction de suppression. La politique de conservation, de purge et les sauvegardes relèvent donc de l’exploitation de la bibliothèque partagée.

## Vérifications et sources

Les suites `TicketLibraryChecks`, `TicketResolutionChecks`, `SharedWorkspaceChecks`, `SharedFolderChecks`, `SharedBusinessChecks`, `CalendarChecks`, `CalendarBridgeChecks`, `TicketLibraryAcquisitionChecks`, `RuntimeSessionChecks` et les parcours WebView couvrent le stockage, les versions, les lots, le navigateur et les interactions. Le workflow exige les validations Windows et un serveur PostgreSQL 17 éphémère avant de publier les packs. Les contrôles de dossier utilisent notamment plusieurs processus et vérifient les refus de corruption, les accès absents et la reprise. Un parcours synthétique ou une CI réussie ne prouve pas la couverture d’une installation privée ou le comportement de son serveur SMB ; le bilan de chaque collecte et l’état de partage conservent les limites réellement observées.

Sources primaires utilisées pour le protocole de navigation et l’installation :

- [GLPI 10.0.22 : statuts et recherche des tickets](https://github.com/glpi-project/glpi/blob/10.0.22/src/Ticket.php), [champs de recherche et date de modification](https://github.com/glpi-project/glpi/blob/10.0.22/src/CommonITILObject.php), [pagination native](https://github.com/glpi-project/glpi/blob/10.0.22/templates/components/pager.html.twig).
- [PostgreSQL 17 : authentification et ordre des règles](https://www.postgresql.org/docs/17/auth-pg-hba-conf.html), [configuration TLS](https://www.postgresql.org/docs/17/ssl-tcp.html), [rôles](https://www.postgresql.org/docs/17/sql-createrole.html), [sauvegarde pg_dump](https://www.postgresql.org/docs/17/app-pgdump.html).
- [Npgsql : sécurité et modes de connexion](https://www.npgsql.org/doc/security.html).
- [SQLite : sauvegarde en ligne](https://www.sqlite.org/backup.html), [journal WAL](https://www.sqlite.org/wal.html).
