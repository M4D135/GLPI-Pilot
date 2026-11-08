# GLPI Pilot 3.5.0 — Interventions et données communes

Cette version réunit la préparation des interventions, le suivi des tickets et le pilotage dans une interface commune. Elle ajoute un calendrier avec des événements durables et un partage facultatif entre sessions Windows et machines, par dossier réseau ou PostgreSQL.

## Un même outil pour toute l’équipe

Le menu principal contient **Accueil, Tickets, Interventions et Pilotage**. **Données** et **Réglages** sont regroupés dans la partie utilitaire. Les anciens liens ouvrent directement l’outil concerné dans cette organisation.

Il n’existe pas de séparation d’accès entre technicien et chef d’équipe, ni de système supplémentaire de comptes et mots de passe. Les fonctions de planification, de répartition et d’analyse restent accessibles. **C’est moi** choisit une identité déclarée afin de filtrer ses tickets, ses interventions et ses statistiques. Cette sélection n’est pas une authentification GLPI ou Windows.

L’Accueil présente les tickets à traiter et les interventions à venir, avec des critères explicites : échéance dépassée, priorité, absence d’affectation, inactivité et ancienneté. Les compteurs ouvrent leur périmètre exact. Les filtres permettent de réunir plusieurs équipes ou techniciens sans multiplier une ligne commune.

L’état de réception du relevé reste visible dans l’en-tête. Les avertissements signalent notamment une collecte partielle ou un changement de périmètre ; les volumes ne sont pas présentés comme complets lorsque la source ne le permet pas.

## Calendrier des interventions

**Interventions** réunit Planning, Carte et Tournées. Le calendrier propose les vues jour, semaine, semaine par ressource, mois et agenda.

- Créer une intervention libre ou la préparer à partir d’une sélection de tickets.
- Associer plusieurs participants, plusieurs tickets, un site ou une adresse ; renseigner consignes, contact, matériel, compétences et trajets prévus.
- Prévoir des interventions, indisponibilités, réunions et maintenances, même sans relevé chargé.
- Déplacer et redimensionner un rendez-vous, annuler une modification ou prévisualiser le décalage d’un ensemble d’interventions.
- Définir des récurrences quotidiennes, hebdomadaires ou mensuelles, avec exceptions ; modifier une occurrence, les suivantes ou la série.
- Comparer les rendez-vous avec les tournées, disponibilités, capacités déclarées et ouvertures des sites ; rechercher des créneaux compatibles.
- Retrouver les interventions liées depuis un ticket ; conserver les références lorsqu’un ticket sort du relevé courant.
- Exporter les occurrences en ICS ou les fiches en HTML imprimable.

Les heures sont enregistrées avec leur fuseau. Une heure inexistante au changement d’heure est refusée ; une heure répétée exige un décalage explicite. Les récurrences conservent des identités d’occurrence stables, y compris après déplacement et séparation d’une série.

Une intervention à plusieurs participants compte une seule fois dans le volume ; les heures-technicien correspondent à la somme des durées des participants. Une indisponibilité, un temps de trajet ou une capacité non renseignée ne devient pas une donnée vérifiée. Les contraintes analysées restent bornées à la fenêtre annoncée. Un dépassement nécessitant un arbitrage est présenté avant validation.

Les états d’intervention appartiennent au planning GLPI Pilot. **Effectuée** ne clôture pas automatiquement un ticket et n’en change pas l’affectation dans GLPI. Les notifications locales supposent que l’application soit ouverte ; l’export ICS ne crée pas une connexion permanente avec un agenda externe.

## Partage facultatif entre machines

**Réglages → Partage entre postes** propose trois modes :

| Mode | Fonctionnement |
| --- | --- |
| Sur ce poste uniquement | Données et travail conservés localement ; aucun espace central requis. |
| Dossier réseau partagé | Les postes utilisent le même dossier accessible depuis leurs sessions Windows. Le registre commun est préparé par l’application. |
| Serveur PostgreSQL | Les copies locales échangent avec une base indépendante configurée et administrée séparément. |

Le cache SQLite et l’index de lecture du journal restent sur le disque local. Le dossier commun contient des transactions fermées, avec un identifiant d’espace, des empreintes, des reçus et un ordre de lecture. Le protocole protège la concurrence et permet de reprendre un échange interrompu sans répéter une modification déjà validée.

Les échanges automatiques ont lieu pendant l’utilisation de l’application, toutes les cinq minutes par défaut. L’intervalle est réglable de 1 à 1 440 minutes et la case **Synchroniser automatiquement** permet de désactiver les échanges périodiques. Une édition active peut les différer. **Synchroniser** reste disponible pour un passage manuel immédiat. L’interface indique les éléments en attente et la dernière réussite pour la destination configurée ; un échange échoué ne devient pas une réussite.

### Données communes

| Contenu | Effet sur les autres postes |
| --- | --- |
| Calendrier et ressources | Mêmes rendez-vous, participants, indisponibilités et révisions. |
| Tournées | Identités conservées, modifications et retraits répercutés ; aucune nouvelle copie créée à chaque réception. |
| Compétences, secteurs, capacités et répartition | Données d’organisation reprises dans les outils, avec conservation des champs personnels du profil. |
| Sites, communes, adresses et termes de détection | Référentiel commun appliqué sans importer le CSV séparément sur chaque machine. |
| Corrections de localisation et ordres de tournée | Corrections et choix enregistrés disponibles sur les autres postes. |
| Règles, seuils météo, tableaux de bord et requêtes | Configuration métier commune ; les seuils reçus recalculent aussi le relevé partagé déjà ouvert. |
| Relevés | Colonnes, lignes et date d’observation conservées ; ouverture depuis Données → Relevés partagés. |
| Bibliothèque et contributions | Fiches, contenus effectivement recueillis, connaissances et notes destinées à l’équipe. |

Deux périmètres GLPI distincts ne sont pas fusionnés parce qu’ils utilisent la même URL. Leur identité tient compte du contexte vérifié de collecte. Lorsque plusieurs périmètres correspondent à une recherche, le choix du relevé reste explicite. Un relevé choisi peut suivre ses nouvelles versions ; une autre source ouverte volontairement reste en place.

Les données personnelles restent locales : identité choisie, notes privées, favoris et rappels personnels, préférences d’interface, dossiers du poste, mots de passe et profils de navigateur GLPI/SSO. Les pièces jointes de la bibliothèque restent des références et métadonnées ; cette version ne télécharge pas leurs fichiers binaires.

### Éditions concurrentes et reprise

Les changements reposent sur les révisions des documents, pas sur l’heure des ordinateurs. Deux éditions concurrentes conservent les deux propositions. La comparaison expose les champs ; un export permet de conserver les versions complètes. La décision est refusée si les données ont changé depuis leur lecture, y compris lorsqu’une modification locale n’a pas encore été synchronisée.

Une modification groupée du calendrier est transmise comme un lot indivisible. Une séparation de série ou un déplacement collectif ne peut pas être reçu à moitié. La revue peut conserver la version commune ou les propositions du lot entier ; les décisions se propagent aux autres postes.

Les registres locaux remplacés font l’objet d’une sauvegarde. Un échec d’application après réception n’autorise pas un ancien fichier local à écraser une version distante jamais appliquée. Un registre local absent ne constitue pas une preuve de suppression des éléments communs.

## Une interface plus vivante

La météo du stock possède des scènes SVG animées dans les cartes : soleil plus dégagé avec rayons tournants, halo et éclats, nuages sur deux plans, pluie à plusieurs rythmes et petites ondulations. Les couleurs et les contrastes sont adaptés aux thèmes clair et sombre. Les scènes occupent la même place et conservent les mêmes seuils et volumes.

L’Accueil retrouve un en-tête illustré compact. Les indicateurs, y compris ceux du calendrier, utilisent des médaillons animés, des accents distincts et des halos. Les barres réelles reçoivent un reflet sans changement de leur longueur ; les cartes apparaissent en cascade et les contrôles répondent au survol et au clic. Aucun faux graphique ni variation artificielle des chiffres n’est ajouté.

**Réglages → Confort et présentation → Animations** conserve les choix **Expressives**, **Suivre Windows** et **Réduites**. Les effets sont suspendus hors écran, quand le document passe en arrière-plan, en contraste renforcé et pendant les éditions concernées. Le mode réduit garde toutes les informations et commandes.

## Lecture, carte et simulation

La pagination réutilise un cache borné d’index de lignes. Les recherches devenues anciennes sont annulées et les exports utilisent le même périmètre que la liste. Les vues conservent leurs filtres et leur position, séparément entre environnement habituel et simulation.

Les cartes intégrées montrent les tickets localisés et les visites prévues. Le fond de rues reste proposé par défaut, avec repli vectoriel local. Une position de commune reste identifiée comme approximative ; les lieux sans coordonnées restent accessibles. La carte ne prétend pas mesurer la position réelle d’un technicien.

Le choix du fond de carte est conservé séparément de son chargement : quitter une carte pendant l’ouverture des rues ne remplace plus cette préférence par le fond hors ligne. Un repli temporaire dû au réseau conserve aussi le choix initial ; le fond hors ligne sélectionné volontairement reste mémorisé.

Le calendrier, les données communes et leurs conflits s’utilisent aussi en simulation depuis un import CSV/Excel. Les adaptateurs de simulation ne contactent pas le véritable dossier réseau ni le serveur configuré et n’enregistrent aucun mot de passe. Les champs absents restent absents ; aucune macro ou formule Excel n’est exécutée.

Les déduplications d’équipes, la météo du périmètre, les seuils initiaux et les compositions existantes sont conservés. L’import de configuration accepte les versions 3.4.x et 3.5.0 ; une version future non reconnue reste refusée.

## Installation et limites pratiques

Extraire tout le pack 3.5.0, avec `GLPI-Pilot.exe` et `wad`, après fermeture de l’ancienne instance. Les préférences du profil sont conservées. Le fichier facultatif privé `wad/sites.csv` reste une donnée de distribution indépendante ; le moteur demeure générique.

Tous les postes d’un même espace partagé doivent utiliser une version compatible 3.5.0 ou ultérieure. Le dossier commun est destiné à un partage de fichiers qui respecte les verrous et renommages nécessaires ; les contrôles automatisés ne qualifient pas le serveur SMB d’une installation particulière. Les droits Windows du dossier ou de la base centrale définissent ses lecteurs : ils ne reproduisent pas les droits GLPI.

Le logiciel échange les données qu’il a effectivement recueillies ou que l’équipe a saisies. Il ne garantit pas que tous les tickets de GLPI ont été observés. Les limites de source, de couverture, de mémoire et de prévisualisation restent explicites. Un relevé partagé est limité à 64 Mio décompressés et 12 Mio compressés ; au-delà, le relevé reste local et demande un périmètre plus réduit.

Le [rapport de vérification](Rapport-verification.txt) distingue les résultats locaux de la validation du pack Windows. La publication exige aussi les tests PostgreSQL réels, le démarrage natif, la mise à jour et le retour arrière, l’intégrité des archives et la reconstruction DEV. Voir le [guide du calendrier](Calendrier-et-interventions.md), le [guide du partage](Bibliotheque-et-partage.md) et le [guide de prise en main](Guide-GLPI-Pilot.html).
