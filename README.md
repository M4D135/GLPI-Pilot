# GLPI Pilot 3.4.0 — Windows x64

**Un espace de travail pour exploiter les recherches GLPI accessibles par navigateur et SSO.** Extraction complète, dashboards global et personnel, bibliothèque de tickets, connaissances et espace d’équipe, techniciens, requêtes, lieux, tournées et historiques. Aucun accès API ni accès direct à la base de GLPI n’est nécessaire. Une base PostgreSQL indépendante peut partager les observations et les contributions entre les postes.

La version 3 utilise un **hôte C# WinForms avec WebView2** et une interface locale HTML/CSS/JavaScript. Les calculs, la pagination, les validations, la persistance et les accès au navigateur restent en C#. Le runtime WebView2 fixe est livré dans le ZIP ; les pages de l’application n’ont besoin ni d’un site hébergé ni d’un téléchargement au premier lancement. Le modèle de migration est documenté dans le [contrat WebView](docs/WebView-contract.md), à partir de Domenia 2.

## Télécharger le pack complet

Les packs publiés sont disponibles dans les [Releases](https://github.com/M4D135/GLPI-Pilot/releases). **La validation Windows de la version 3 dépend du résultat du workflow de son commit**, indiqué dans le [rapport de vérification](docs/Rapport-verification.txt) et les journaux de la livraison. La présence de sources, d’un EXE compilé ou d’un ZIP local ne signifie pas que cette validation a réussi.

| Pack | Pour qui | Contenu à la racine |
| --- | --- | --- |
| **PROD — Windows x64** | Utiliser le logiciel | `GLPI-Pilot.exe` + `wad/` |
| **DEV — Windows x64** | Utiliser le même logiciel et poursuivre son développement | `GLPI-Pilot.exe` + `wad/`, sources et tests dans `wad/sources/` |

Extraire **tout le ZIP** dans un dossier local puis ouvrir **GLPI-Pilot.exe**. Garder `wad` à côté. Aucun Python, Node.js, .NET ou installateur WebView2 à ajouter pour utiliser le pack. **Edge ou Chrome reste nécessaire pour la connexion au portail GLPI et son SSO** ; le runtime de l’interface est livré séparément. **Code → Download ZIP** fournit les sources, les packs exécutables sont dans les Releases. Les deux éditions utilisent le même EXE et conservent les empreintes SHA-256 de leurs fichiers.

| Emplacement dans le pack | Rôle |
| --- | --- |
| `GLPI-Pilot.exe` | Application C# autonome, bibliothèques .NET et chargeur natif WebView2 intégrés |
| `wad/web/` | Pages locales, styles, modules JavaScript et ressources visuelles |
| `wad/runtime/webview2/` | Runtime fixe Microsoft 154.0.4258.62 x64, complet et vérifié |
| `wad/.playwright/` | Pilote Windows de la connexion au navigateur GLPI |
| `wad/docs/`, `wad/support/`, `wad/licenses/` | Guides, diagnostics et licences des composants |
| `wad/version.json`, `wad/SHA256SUMS.txt` | Version, provenance, dépendances et inventaire d’intégrité |
| `wad/sources/` dans DEV | Sources C#, sources web, tests et scripts de reconstruction |

## Six espaces, une interface reconstruite

La version **3.4.0** remplace le catalogue d’écrans par six espaces conçus autour du travail à accomplir. Les outils détaillés s’ouvrent dans leur contexte ; le menu ne propose plus les anciennes vues comme une série d’onglets.

| Espace | Travail proposé |
| --- | --- |
| **Accueil** | Priorités personnelles, alertes, rendez-vous et tournées du jour ; accès direct aux tickets et à la carte |
| **Pilotage** | Météo, stock, tickets à traiter, répartition des équipes, catégories, ancienneté et contribution dans une seule composition |
| **Tickets** | Liste du relevé et dossier réunis, recherche, sélection, notes, rappels et actions assistées |
| **Terrain** | Carte centrale des tickets, affectations observées, visites prévues, sélection et préparation d’une tournée |
| **Données** | Import CSV/Excel, collecte GLPI, qualité, correspondances, bibliothèque, recherche de précédents et espace d’équipe |
| **Réglages** | Apparence, animations, météo et équipes, automatisations, stockage, mises à jour et diagnostic |

**Pilotage partage un même périmètre** entre ses sept sections : météo, stock, attention, équipes, demandes, contributions et notes. Cumuler plusieurs équipes et groupes ne compte pas deux fois un ticket commun. Les limites et la source des chiffres restent visibles ; chaque mesure ouvre les lignes qui la composent. **Composer** permet de modifier les cartes, leur taille, leur visibilité et leur ordre. Les compositions personnalisées restent disponibles.

**Tickets fusionne le relevé et le dossier.** Sur grand écran, la liste et la fiche sont côte à côte ; les fenêtres plus étroites disposent d’un retour à la liste. Les filtres, la sélection et la position de lecture sont conservés. Les notes personnelles se sauvegardent par leur action explicite ; le brouillon de suivi termine sa sauvegarde dans le contexte courant avant un changement de simulation.

**Terrain devient un outil de préparation.** Filtrer les équipes, techniciens, types et statuts, ouvrir les tickets d’un lieu, sélectionner un périmètre puis prévisualiser une tournée. Les affectations proviennent des tickets observés ; les visites affichées sont datées et prévues. Elles n’indiquent pas la position GPS actuelle d’une personne. Les lieux inconnus, ambigus ou sans coordonnées restent comptés et accessibles dans les listes. Une position de commune est indiquée comme approximative.

Les **rues OpenStreetMap se chargent à l’ouverture d’une carte, y compris en simulation**. Le fond vectoriel local prend le relais si elles sont indisponibles ; **Fond hors ligne** permet de le choisir. Tout reste intégré dans l’application, sans clé cartographique. Les marqueurs et les données métier sont locaux ; seules les images de rues sont demandées au fournisseur. Les pointillés montrent l’ordre des visites à vol d’oiseau.

Le style reprend la référence réelle de **Domenia v2** : surfaces translucides, orbes colorées, lumières, entrées et reflets, scènes météo animées et KPI illustrés. **Réglages → Animations** propose Expressives, Suivre Windows et Réduites. Les scènes hors écran sont suspendues ; les valeurs et tableaux restent stables. Les barres d’action ont des hauteurs communes et se réorganisent lorsque la fenêtre rétrécit.

Assistant, Lectures ciblées et Effectifs des sites sont **masqués par défaut**. Leur activation dans Réglages ajoute l’outil dans l’espace concerné. Aucun modèle de langage n’est nécessaire pour les fonctions de pilotage ou la recherche locale de précédents.

Voir les [notes 3.4.0](docs/RELEASE-3.4.0.md), le [guide actuel](docs/Guide-GLPI-Pilot.html) et le [rapport de vérification](docs/Rapport-verification.txt).

## Premier réglage, puis un clic

1. Dans **Données**, renseigner l’adresse complète de la recherche GLPI, filtres compris, puis choisir Edge ou Chrome. Les profils de sources et leurs réglages sont accessibles dans le même espace.
2. Cliquer **Lancer la collecte**. Le navigateur dédié reste réduit ; il s’affiche temporairement lorsqu’une validation SSO visible demande une intervention, puis la collecte reprend discrètement.
3. Vérifier les contrôles de données, puis ouvrir **Tickets**, **Pilotage** ou **Terrain**. Les chiffres utilisent le même relevé.
4. Dans **Pilotage → Analyser et organiser → Charge des techniciens**, choisir son identité avec **C’est moi**. Le référentiel se gère dans **Terrain → Gérer le référentiel**.
5. Réutiliser les sources et réglages enregistrés aux ouvertures suivantes. **Réglages → Seuils, équipes et règles météo** permet d’adapter le pilotage au service.

**Afficher le navigateur** redonne la main à tout moment ; cet affichage manuel reste visible après connexion. L’utilisateur actionne les formulaires, la case « Se souvenir de moi » et le bouton de connexion. Une interface GLPI personnalisée peut demander l’apprentissage initial du tableau ou des sections.

## Tester tout le logiciel avec un fichier local

Choisir **Importer en simulation** sur Accueil, **Importer CSV / Excel** dans Données, ou utiliser **Ctrl+K**. CSV, TSV, TXT, XLSX et XLSM sont acceptés. Pour Excel, choisir la feuille et la ligne d’en-têtes ; un profil compatible peut reprendre la météo, les équipes et les sites du classeur. Les valeurs enregistrées sont lues sans exécuter les macros ni les formules. L’ancien XLS doit être enregistré en XLSX ou CSV.

Les six espaces et leurs outils utilisent l’extrait importé dans un contexte isolé : fiches, actions simulées, météo, bibliothèque, requêtes, sites, tournées, partage simulé et mise à jour virtuelle. Les données absentes du fichier restent signalées comme absentes. Annuler le choix du fichier conserve le travail courant. **Quitter la simulation** retrouve le relevé et le profil habituels. Les rues demandent Internet ; le fond local conserve les repères hors ligne.

Les seuils météo initiaux sont **100/200** pour le stock, **50/100** pour incidents ou demandes et **10/20** par équipe ou catégorie. Le dépassement est strict. Les règles personnalisées, les cartes masquées et leur ordre de priorité restent conservés.

## Historique des versions précédentes

Les descriptions ci-dessous documentent leur version d’origine. Les nouveaux parcours et le comportement cartographique de **3.4.0** sont ceux présentés ci-dessus. Les [notes 3.3.4](docs/RELEASE-3.3.4.md) conservent le détail de la première carte intégrée.

## Nouveautés de la version 3.3.3

**Le mode simulation permet de travailler dans tous les écrans à partir d’un CSV.** Les effectifs des sites peuvent être saisis et datés dans cet espace ; les ratios sont recalculés sans modifier le profil habituel. Le référentiel du profil est repris au début de la simulation, avec ses communes, sites et adresses. Les valeurs absentes du CSV restent indiquées comme indisponibles.

**Le menu regroupe les rubriques dans des sections repliables.** Assistant et Lectures ciblées sont masqués par défaut, puis réactivables dans la personnalisation. Effectifs devient **Effectifs des sites**, accessible depuis les outils de Sites et masqué du menu par défaut. Les épingles, les préférences de classement, le clavier et les animations réduites sont conservés.

**Les sites peuvent maintenant recevoir leur commune, adresse et coordonnées depuis `wad/sites.csv`.** Pour un profil déjà initialisé, une nouvelle version géographique du fichier complète les champs vides sans écraser les valeurs locales ni recréer les sites supprimés. Le [complément de sites](distribution/sites/README.md) contient les données à ajouter à `wad` ; aucun organisme ni référentiel particulier n’est intégré au moteur.

La première intégration cartographique de 3.3.3 est remplacée en 3.3.4 par les cartes directement intégrées décrites ci-dessus.

**Les aperçus de lignes sont plus lisibles.** Les tableaux ouverts depuis les dashboards, requêtes et analyses utilisent une fenêtre large, des largeurs de colonne adaptées et des cellules alignées en haut. Les textes longs occupent au maximum trois lignes dans l’aperçu ; cliquer une valeur ouvre son contenu complet et son interprétation. Les exports conservent les données complètes.

**Sites distingue correctement une localisation reconnue d’un lieu à vérifier.** Un site unique est reconnu même sans commune ou coordonnées renseignées. Les titres absents, les lieux inconnus et les ambiguïtés restent dans le compteur à vérifier. Lorsque le repérage est disponible, ces deux compteurs couvrent ensemble le stock actif du périmètre. Une commune reconnue sans site précis reste dans la ligne des sites non renseignés du tableau de planification.

**Le filtre existant permet de cumuler plusieurs équipes et groupes.** Dans **Groupes**, **Plus anciens**, **Suivi agents**, **Agents actifs** et **Sites**, ouvrir **Équipes et groupes**, cocher les choix puis cliquer **Appliquer**. Les sélections sont réunies par **OU** : une même ligne commune à plusieurs choix est retenue une seule fois. Les cartes, répartitions, préconisations, listes de tickets et exports utilisent ce même périmètre. **Toutes** retire cette restriction ; chaque rubrique conserve sa sélection pendant la session, séparément dans l’espace habituel et la simulation.

Les règles de détection dans les titres sont conservées : noms et variantes configurés, mots entiers, normalisation des accents et de la ponctuation, ambiguïtés explicites. La correction porte sur les compteurs et le périmètre présenté. Voir les [notes 3.3.3](docs/RELEASE-3.3.3.md) et le [rapport de vérification](docs/Rapport-verification.txt) pour l’état de validation du commit livré.

## Nouveautés de la version 3.3.2

**Lieux et sites affiche tous les termes de détection d’un site : son nom et ses alias.** Le formulaire précise que le nom est reconnu automatiquement ; les variantes saisies le complètent. Après l’application d’un profil de classeur, un brouillon du référentiel qui n’a pas été modifié reprend automatiquement les nouveaux réglages. Un brouillon déjà modifié est conservé et son conflit reste signalé.

Un distributeur peut ajouter le fichier facultatif **`wad/sites.csv`**, au format UTF-8 **`Site;Termes de détection`**. Au premier lancement où ce fichier est présent et valide, GLPI Pilot reprend discrètement les sites dans `%LOCALAPPDATA%\GLPI-Pilot\settings.json`. Ils sont ensuite visibles dans **Lieux et sites** et modifiables comme ceux saisis manuellement. L’initialisation est assurée directement par l’application.

**L’import automatique ne se fait qu’une fois par profil utilisateur.** Les modifications et suppressions réalisées ensuite dans l’application sont conservées aux démarrages suivants. En l’absence de `wad/sites.csv`, le démarrage est normal et l’initialisation reste disponible si le fichier est fourni plus tard.

Le logiciel et le pack public restent génériques. Le tableau de sites est un fichier de données séparé, ajouté à la distribution selon les besoins. Un [complément indépendant](distribution/sites/README.md) est disponible. L’import CSV du référentiel dans l’interface reste disponible ; cette version n’ajoute pas de sélection Excel à cet écran. Le format initial est décrit dans les [notes 3.3.2](docs/RELEASE-3.3.2.md) et son enrichissement géographique dans les [notes 3.3.3](docs/RELEASE-3.3.3.md).

## Nouveautés de la version 3.3.1

La rubrique de travail personnel s’intitule désormais **Mon espace**. Ce libellé est repris dans la navigation, les actions, les notifications et les guides. Les fonctions et les données existantes sont conservées. Voir les [notes 3.3.1](docs/RELEASE-3.3.1.md).

## Nouveautés de la version 3.3.0

**Le pilotage inspiré des rapports Excel est intégré directement au menu.** Neuf rubriques sont disponibles dès l’ouverture, avec des modèles génériques et des calculs alimentés par le relevé courant. Les tableaux personnels existants restent accessibles dans **Mes tableaux**.

| Rubrique native | Informations et actions |
| --- | --- |
| **Météo** | Grand total, cartes par catégorie et par équipe, états soleil/nuage/pluie, seuils, fraîcheur et analyse en cours |
| **Pilotage** | Stock actif, attente, demandes/incidents, attribution, répartitions et matrices de statuts |
| **Groupes** | Volumes par groupe et statut, totaux, accès aux tickets et périmètres d’équipe |
| **Catégories** | Volumes, parts du stock et détail des catégories, avec filtres de branche explicites |
| **Plus anciens** | Dix plus anciens du périmètre et cinq par groupe, âges calendaires, dates indisponibles et fiches |
| **Suivi agents** | Comparaison des tickets rédigés et attribués, listes dynamiques ou participants configurés |
| **Agents actifs** | Suivis, tâches, intersection et union distincte par personne |
| **Sites** | Stock par site, identifiants, sites à vérifier et préparation des interventions |
| **Paramètres du pilotage** | Correspondances météo, ordre des équipes, seuils, visibilité, fraîcheur, notes et reprise d’un profil de classeur |

La météo de stock conserve les règles du rapport : dépassement **strict** des seuils, catégories et descendants délimités par `>`, première équipe correspondante dans l’ordre configuré, y compris lorsqu’une carte est masquée. Dans la vue Groupes, un ticket participe à chacun de ses groupes ; la météo d’équipe applique sa répartition exclusive. Le rappel de fraîcheur et le texte d’analyse restent distincts des couleurs de volume. Les valeurs inconnues ou une source insuffisamment fiable restent signalées.

### Ouvrir une simulation complète depuis Excel ou CSV

**Importer en simulation** est accessible depuis l’accueil, la collecte, le relevé et la palette **Ctrl+K**. Les formats acceptés sont CSV, TSV, TXT, XLSX et XLSM. Pour Excel, l’aperçu propose la feuille source et sa ligne d’en-têtes lorsqu’elles sont reconnues, notamment **Tickets / ligne 4** pour un rapport compatible. La feuille et la ligne restent modifiables. Le lecteur utilise les valeurs enregistrées, sans exécuter de formule, macro ni liaison externe. L’ancien format XLS demande une copie XLSX ou CSV.

Quand une feuille **Paramétrage** compatible est présente, l’aperçu indique les règles météo, les cartes visibles, les sites, les alias et la fraîcheur. L’option **Reprendre la météo, les équipes et les sites du classeur dans la simulation**, cochée par défaut, applique ces réglages dans la simulation. Elle peut être décochée. La page Météo s’ouvre ensuite avec les chiffres recalculés sur les lignes importées ; les anciens totaux des graphiques du classeur ne deviennent pas des résultats actuels.

**Toutes les rubriques sont utilisables dans la simulation.** Ouvrir une fiche depuis le relevé ou la météo, préparer un suivi, une tâche, une affectation ou un statut, relire l’aperçu puis confirmer modifie effectivement le ticket simulé. La fiche, la bibliothèque et les statistiques sont actualisées. Les sites, notes, rappels, requêtes, tableaux, tournées, répartitions, archives et alertes ont leur propre contexte de travail. La bibliothèque est alimentée par les données importées ; **Ctrl+K** retrouve ses tickets. Une colonne ou un contenu absent reste signalé comme indisponible.

La connexion et les collectes utilisent une source locale simulée ; l’inspection, le choix d’un tableau, la campagne et leur bilan sont testables. La synchronisation échange avec un central simulé. Les services externes et l’assistant produisent des résultats locaux explicitement signalés. Le parcours de mise à jour utilise des packs fictifs, une version virtuelle et un bilan consultable, avec possibilité de retour arrière. **Simulation avec données fictives** ouvre le même environnement complet, sans fichier à préparer.

Le bandeau **Simulation · toutes les rubriques** reste visible. La couverture réelle de GLPI n’est pas certifiée par un fichier local ; les lectures limitées, formules sans résultat enregistré, identifiants douteux et statuts inconnus restent pris en compte dans la qualité et la météo. Les actions de simulation n’atteignent ni le portail GLPI ni le central réel. Les adaptateurs de services restent simulés ; les cartes intégrées utilisent un fond local. **Afficher les rues** active uniquement les images OpenStreetMap dans la carte : cet accès facultatif demande Internet.

**Quitter la simulation** restitue le relevé et les réglages d’origine, ainsi que la bibliothèque, les archives et le travail habituel. Les modifications d’essai, y compris celles des requêtes et tableaux, restent dans la session isolée. Un nouvel import crée une nouvelle simulation ; une sélection annulée ou un import invalide conserve l’état courant. Les exports choisis explicitement peuvent être conservés avant de quitter.

Les [notes 3.3.0](docs/RELEASE-3.3.0.md) décrivent les parcours et les validations locales. Les modèles publics et les tests utilisent uniquement des données fictives ; les classeurs, captures, noms et configurations privés sont repris depuis les fichiers de leur propriétaire.

## Apports de la version 3.2.2

**La météo peut suivre le volume réel de tickets d’un service, d’une équipe ou d’une catégorie.** La présentation « Météo du stock » conserve le nombre cliquable et applique deux seuils configurables avec un dépassement strict : la valeur égale au seuil orange reste maîtrisée, celle égale au seuil rouge reste à surveiller. Les états soleil, nuage et pluie suivent les thèmes clair/sombre et la préférence de réduction des animations. Une réception partielle, un périmètre non confirmé ou un problème d’identité ne produit pas un faux état vert ; une simple différence de représentation HTML/CSV ne neutralise pas le comptage. Les indices de risque existants restent disponibles.

**Le modèle « Météo et pilotage du service »** ajoute un point de départ modifiable : stock actif, demandes et incidents, attente, absence de groupe, affectations multiples, rédacteurs renseignés, groupes × statuts, types × statuts, catégories, ancienneté et inactivité. Les matrices conservent les quatre statuts actifs, y compris leurs zéros connus, avec un total par ligne. Un ticket multigroupe compte une fois dans chacun de ses groupes ; ses affectations multiples ne multiplient pas le stock global. Les lignes portant un identifiant répété restent des occurrences signalées, sans déduplication silencieuse. Un filtre d’équipe peut réunir plusieurs groupes par **OU**, sans compter deux fois la même ligne commune.

**Les plus anciens ont deux vues : cinq tickets par groupe ou les dix premiers du périmètre.** Dans la seconde, une même ligne affectée à plusieurs groupes n’apparaît qu’une fois. Le classement utilise l’ouverture, puis l’identifiant numérique ; son échelle de couleur décrit l’âge relatif, sans seuil SLA. Les tickets sans date, avec une date illisible ou future restent dans le stock et sont signalés séparément, sans recevoir un âge inventé. Le détail retrouve les lignes du périmètre.

**Les participations distinguent rédaction et attribution.** Un widget compte, par personne, les tickets avec suivi, avec tâche, les deux et leur union ; un autre compare cette union aux tickets attribués au technicien. La liste est dynamique, avec une liste de personnes et des alias explicites facultatifs. Les homonymes ou identités incompatibles ne sont pas rapprochés arbitrairement. Ces comptes ne mesurent ni les actions effectuées ni la productivité.

**Sites · stock et planification** conserve les sites du référentiel même à zéro, ainsi que les tickets sans site reconnu. Les cinq premiers identifiants ouvrent leurs lignes ; **Copier les ID** reprend la liste complète du site dans le périmètre courant. Un site connu propose une intervention à partir de quatre tickets par défaut, seuil réglable ; un site inconnu demande une vérification dès le premier ticket. Aucune intervention n’est créée automatiquement.

Les filtres de catégorie peuvent inclure les **sous-catégories délimitées par `>`**. Un rappel de fraîcheur se règle par tableau ; le modèle de service le fixe à **48 heures**. Ce rappel demande une actualisation sans modifier les volumes ni les couleurs météo. Le parcours des précédents résolus restitue également le focus au contrôle de départ lorsque son dialogue se ferme.

**Les colonnes d’un rapport métier sont mieux reprises.** Suivis et tâches ont chacun leur rôle de rédacteur ; description, TTR avec progression, délai de prise en compte et compteurs déclarés sont reconnus séparément. Les âges et nombres déclarés restent distincts des mesures recalculées. Aucun pourcentage TTR n’est transformé en échéance et aucune unité de durée n’est inventée. Les widgets sans données utilisables restent masqués automatiquement, selon le réglage du tableau.

**Importer une disposition propose « Ajouter à mes tableaux ».** L’aperçu présente le nombre de tableaux et les éventuels renommages, préserve les tableaux existants et permet l’annulation. Un fichier de configuration partiel propose seulement les sections qu’il contient et conserve les réglages absents. Les sites peuvent être reconnus par leurs noms et variantes **sans commune renseignée**, puis rattachés ultérieurement sans changer d’identité ; aucun emplacement cartographique n’est inventé.

Les [notes 3.2.2](docs/RELEASE-3.2.2.md) précisent les règles de calcul et de reprise. Les configurations internes et les captures métier restent dans les fichiers d’import de leur propriétaire ; le dépôt contient uniquement le moteur, des modèles génériques et des jeux de vérification fictifs.

## Apports de la version 3.2.1

**Ctrl+K retrouve les tickets conservés**, en plus des écrans et actions. Les résultats précisent leur installation GLPI, leur état et la date de lecture. Un numéro exact est prioritaire ; le filtre d’installation distingue les tickets portant le même numéro. La lecture du contenu local garde la saisie, l’écran et le relevé en cours. Les flèches et Entrée permettent de choisir, Échap rend le focus au point de départ.

**Le lecteur recherche les précédents résolus du ticket.** Le moteur C# recherche dans la même installation, explique les termes et codes d’erreur communs et cite les passages exacts, avec priorité aux solutions recueillies. Les contenus partiels, périmés ou non confirmés sont exclus. Une catégorie identique seule ne suffit pas. La recherche peut être affinée manuellement et le retour garde la fiche de départ ainsi que le travail en cours. Jusqu’à huit résultats sont présentés avec les limites de la portion examinée ; aucun modèle distant n’est contacté.

**Les collectes conservent leur bilan sur le poste.** Après une annulation ou un redémarrage, **Dernière collecte → Détail de la collecte** retrouve les résultats par ticket, avec filtre et pagination. **Reprendre les tickets à relire** réactualise d’abord l’index et ne relit que les fiches encore accessibles dans le même contexte. Une lecture partielle reste comptée comme telle, même lorsqu’une copie complète antérieure est conservée.

La fraîcheur des nouveaux contenus reste à confirmer jusqu’au contrôle final de leur version GLPI et du périmètre. La confirmation est liée au contenu précis reçu, se synchronise avec les autres postes et ne lève pas un conflit historique. Un arrêt entre lecture et confirmation conserve les observations sans les présenter comme actuelles.

**Les conflits d’équipe se comparent champ par champ**, données structurées comprises : horaires, affectations, disponibilités ou configurations. Les paires archivées sont paginées. Les valeurs absentes, nulles et vides sont distinguées ; les textes conservent leur mise en ligne. L’aperçu annonce ses limites et **Exporter les deux versions** conserve les documents complets. Une contribution créée dans l’espace d’équipe est clairement indiquée comme partageable, même en brouillon ; les notes personnelles restent dans les fiches de l’écran Tickets.

Les [notes 3.2.1](docs/RELEASE-3.2.1.md) détaillent les parcours et les vérifications de cette mise à jour.

### Bibliothèque et interface introduites en 3.2.0

**Le style reprend directement Domenia 2** : palette bleu, violet, turquoise et rose, surfaces translucides, fond animé et transitions de navigation. Les thèmes clair/sombre, le contraste renforcé et la réduction des animations restent disponibles. **Personnaliser la navigation** permet de masquer ou épingler les pages, déplacer les entrées et réordonner leurs groupes ; Accueil et Réglages restent visibles, et la palette ouvre aussi les pages dont le raccourci est masqué. Les groupes suivent les tâches : collecter, consulter et résoudre, piloter l’équipe, travailler sur le terrain, puis administrer le logiciel.

**Bibliothèque de tickets** ajoute un cache SQLite local persistant et quatre vues : bibliothèque, résolution, équipe et connexion. **Aspirer tous les tickets** parcourt tous les statuts accessibles, y compris résolus et clos, hors corbeille et dans les entités actives de la session GLPI. Les descriptions, suivis, tâches, solutions, historiques et autres onglets consultables sont conservés avec leurs preuves de lecture ; les pièces jointes restent des références et métadonnées. Les limites du portail et les lectures partielles sont indiquées dans le bilan.

Les mises à jour réutilisent les contenus complets lorsque la date de modification GLPI, sa précision, la signature du lecteur et l’âge du cache le permettent. Renseigner le **fuseau d’affichage des dates GLPI** dans Connexion pour interpréter une date sans décalage explicite. Une date absente ou ambiguë entraîne une relecture ; elle ne permet pas de déclarer une version plus récente qu’une autre. **Relire tous les contenus** force une nouvelle lecture. Les versions divergentes sont conservées.

La vue **Résolution** recherche dans les tickets résolus ou clos dont le contenu est complet et encore actuel selon les observations conservées. Chaque résultat renvoie au ticket, à la section lue et à un extrait exact. **Exporter le corpus RAG** produit des documents complets et des fragments de texte sourcés en JSONL pour une indexation ultérieure. Cette recherche utilise l’index local de texte ; elle n’envoie pas les tickets à un modèle externe.

L’**espace d’équipe** conserve commentaires, articles de connaissance, notes, propositions d’affectation, configurations de pilotage et statistiques. La synchronisation PostgreSQL est bidirectionnelle : chaque client garde sa copie SQLite, envoie ses mutations et reprend le journal central. Les tickets sont ordonnés par leur version GLPI ; les contributions utilisent une révision centrale. Les conflits et les retraits circulent vers les autres postes. La file d’attente survit aux interruptions. La synchronisation peut être manuelle ou périodique tant que l’application est ouverte.

Les outils existants sont reliés à cet espace : **Enregistrer et partager la note** dans le lecteur, publication des **tournées**, des **répartitions** et des **compétences et capacités** des techniciens. **Examiner et reprendre** présente la comparaison avant application sur un autre poste. Les notes s’ajoutent au texte personnel, les tournées deviennent de nouveaux brouillons, et le scénario ou les capacités remplacés disposent d’une sauvegarde locale. Les favoris et notes privées des techniciens restent conservés. Ces échanges ne déclenchent pas d’affectation ou d’autre écriture GLPI.

**Utiliser le cache comme relevé** alimente les écrans de statistiques et de travail avec les métadonnées conservées. Le lecteur ouvre les détails déjà présents dans la bibliothèque, sans relancer automatiquement GLPI, en gardant l’installation d’origine et la date d’observation.

Le dossier du cache local est configurable ; un changement vers un dossier neuf utilise une copie cohérente et garde l’ancienne base. Un cache SQLite actif n’est pas partagé sur Samba. Les exports peuvent y être enregistrés ; la base commune est PostgreSQL. Le [guide bibliothèque et partage](docs/Bibliotheque-et-partage.md) décrit l’installation, les limites, le protocole de données et l’accès des autres outils. Les réglages personnels existants restent locaux tant qu’ils ne sont pas publiés explicitement dans l’espace d’équipe.

### Diagnostic et présentation introduits en 3.1.1

Le dashboard masque automatiquement les widgets sans données calculables, tout en conservant leur disposition. Le mode édition les réaffiche ; **Afficher les widgets indisponibles** mémorise le choix par tableau. Le widget Qualité devient un résumé compact et ses libellés sont en français. Les observations de présentation du CSV restent consultables dans **Détails des contrôles** et **Qualité du relevé**, sans bandeau permanent. Une réception réellement partielle, un problème d’identifiants ou de contexte reste signalé.

Les nouvelles collectes enregistrent les cellules divergentes dans un **diagnostic local dédié** : valeurs GLPI et CSV, ticket, colonnes, premier écart et codes Unicode. **Qualité du relevé → Voir les cellules divergentes / Exporter le diagnostic des écarts** permet de l’examiner ou de l’enregistrer en JSON. Ce fichier contient des données métier ; le journal général reste sans contenu de cellule. Les limites de capture et les contrôles conservés sont décrits dans les [notes 3.1.1](docs/RELEASE-3.1.1.md).

### Interface et sections introduites en 3.1.0

La version **3.1.0** réorganise l’interface suivant les principes de Domenia 2 : navigation dépliable ou réduite en rail, thèmes clair/sombre, surfaces mieux hiérarchisées, tableaux lisibles et animations désactivables. Les tickets n’affichent plus de `null` parasite et leurs identifiants restent entiers sur une ligne. Le dashboard dispose de **sections persistantes**, même vides, avec titre, description, couleur et visibilité ; widgets et sections se déplacent à la souris ou au clavier, avec redimensionnement visuel et annulation.

Les états de **réception**, d’**identité de l’échantillon** et de **concordance des valeurs** sont désormais séparés : toutes les lignes peuvent avoir été reçues alors que des valeurs restent à vérifier. Les équivalences HTML/CSV de texte riche exigent une version explicitement reconnue et une preuve issue de la cellule GLPI qui les explique exactement. Les données d’origine, les contrôles C1 et les réserves restantes sont conservés. Voir les [notes 3.1.0](docs/RELEASE-3.1.0.md) pour les conditions et la validation du commit livré.

### Migration et corrections précédentes

La version **3.0.2** corrige une régression d’usage par rapport à la version 2.0 : un CSV déjà téléchargé et lu correctement pouvait rester inutilisable dans l’application parce que certaines valeurs différaient de leur affichage HTML. Lorsque les identifiants de l’échantillon correspondent, les lignes sont désormais disponibles pour le relevé, Excel, les dashboards et les villes, avec l’état **À vérifier** si des écarts subsistent. Les contrôles de périmètre restent actifs et le CSV natif reste intact. La lecture des demandeurs multiples et des textes GLPI abrégés par `(...)` est également corrigée. Les [notes 3.0.2](docs/RELEASE-3.0.2.md) précisent les conditions et les vérifications restantes.

Les corrections de profils hérités de la version 2.0, de fermeture après panne WebView2 et de reconstruction depuis les sources DEV restent décrites dans les [notes historiques 3.0.1](docs/RELEASE-3.0.1.md).

- Interface locale dans WebView2 : navigation, thèmes, densité, zoom, formulaires, tableaux paginés et dialogues utilisent les modules web du dépôt. Les opérations autorisées sont exposées par un pont C# fermé, avec annulation et événements d’état.
- Paquet autonome avec runtime fixe Microsoft, contrôlé par une empreinte du CAB et les 257 empreintes de ses fichiers. La compilation peut utiliser exclusivement le SDK et les paquets NuGet déjà présents dans la VM hors ligne.
- Tests séparés pour les moteurs C#, leurs opérations exposées aux pages, les interactions web et le vrai hôte Windows. Après une mise à jour, le démarrage de l’application n’est confirmé qu’après l’initialisation et le premier affichage de l’interface.

Les fonctions métier de la version précédente sont raccordées aux nouvelles pages ; leur couverture et leurs limites sont détaillées dans le suivi des 200 points :

- Profils complets par installation GLPI, campagnes avec bilan autonome par source, délais séparés, contrôle d’espace libre et reprise de pagination après vérification des pages conservées. La reconnexion SSO ou réseau est bornée et refuse un périmètre différent.
- Assistant de correspondance avec aperçu brut/interprété, conventions de nombres et de dates, fuseau, séparateurs multiples et dictionnaires propres à la source. Changements de colonnes détectés, propositions de renommage soumises à validation, panier persistant et lecture universelle des cellules par F2.
- Analyses longitudinales : périodes sans observation, mouvements, transferts, cohortes, incertitude des durées par statut, scénarios de stock et anomalies. Cartes locales reliées aux widgets, taux par effectifs datés et mini-historiques. Exports PNG/PDF avec cartouche et rapport HTML complet autonome.
- Tâches, attentes, validations, relations et documents structurés à partir des seules informations lues. Lecture restaurée par ticket, briefing de session, enrichissement ciblé et brouillons de suivis. Les affectations, suivis et tâches peuvent être préparés puis confirmés individuellement dans les formulaires GLPI reconnus, avec reçu et relecture.
- Calendriers de présence et capacités par demi-journée, estimations par catégorie, hiérarchie jusqu’au bâtiment, horaires des sites, états des tournées et répartition sur plusieurs jours. Adaptateurs facultatifs OSRM et Open-Meteo, avec choix du service et consentement explicite à l’envoi des coordonnées.
- Horaires fixes multiples, recettes conditionnelles, alertes de variation et de fraîcheur, résolution motivée, import avec différences détaillées et arbitrage des conflits. Assistant de requêtes et de synthèse local ou raccordé à un fournisseur choisi, avec aperçu et références contrôlées.
- Concentration F11, navigation personnalisable, palettes de couleurs adaptées, placement sur un moniteur disponible, diagnostic relisible et mise à jour vérifiée avec sauvegarde et retour arrière.

Le [suivi des 200 points](docs/Suivi-200-ameliorations.md) indique le code livré et les prérequis restants. La validation du portail/SSO et du matériel de l’entreprise nécessite cet environnement ; la signature de confiance de l’EXE nécessite un certificat de l’éditeur. Aucun de ces deux prérequis n’est présenté comme accompli par les tests synthétiques.

## Fonctions disponibles dans la version actuelle

### Interface et relevé

- Thèmes clair/sombre complets, bouton soleil/lune, suivi facultatif du thème Windows, cinq couleurs d’accent, contraste renforcé et réduction des animations.
- Zoom **75–150 %**, boutons − / 100 % / + et raccourcis ; densité compacte, équilibrée ou confortable, colonnes figées et mémorisation de la fenêtre.
- Navigation par groupes repliables ou réduite en rail, tiroir sur fenêtre étroite, journal repliable, palette **Ctrl+K**, éditeurs de colonnes et de configuration intégrés à la fenêtre. Les commandes restent accessibles au clavier et respectent la réduction des animations de l’application ou du système.
- Recherche, filtres par colonne et tri multiple calculés en C# sur **toutes les lignes chargées**, avec pages d’affichage bornées. Vues nommées mémorisant filtres, recherche, tri et présentation pour la structure du relevé.
- Export complet ou export explicite de la vue filtrée ; import local CSV, TSV, TXT, XLSX et XLSM en simulation complète. Le fichier natif reçu reste intact.

### Dashboard global et espace personnel

- Dispositions indépendantes, modèles de départ, widgets déplaçables, redimensionnables, duplicables, masquables et verrouillables ; sections persistantes et repliables avec titre, description, couleur et visibilité, y compris lorsqu’elles sont vides.
- Poignées de déplacement et de redimensionnement à la souris ou au clavier, dimensions précises dans le formulaire, édition groupée et annulation/rétablissement. Les dispositions existantes au format 1 sont reprises sans supprimer leurs widgets ni leurs sections repliées.
- Catalogue avec favoris persistants, disponibilité réelle des sondes, filtres communs et par widget, filtres croisés, contextes nommés et comparaison A/B.
- Volumes, actifs, attentes, priorités, échéances, affectations, ancienneté, dates, lieux et couverture des données ; méthodes et effectifs accessibles.
- Cinq plus anciens par groupe ou dix du périmètre, participations des rédacteurs et comparaison avec les attributions ; listes dynamiques ou regroupements par alias explicites.
- Anneaux, jauges, histogrammes, matrices colorées, barres empilées, Pareto, boîtes statistiques, médiane/P90 et nuage âge × échéance.
- Météos de santé du relevé configurables : synthèse, échéances, qualité, localisation. Elles ne représentent pas la météo climatique.
- Courbes et mini-historiques sur archives comparables : identité personnelle, filtres communs et filtres de widget rejoués sur chaque archive, avec ruptures explicites. Export PNG/PDF avec date, filtres et complétude ; bilan CSV/Excel et rapport HTML autonome complet.

### Techniciens et travail personnel

- Identité personnelle mémorisée, tableaux de bord par technicien, gestion de l’équipe, favoris et objectifs locaux.
- Compétences, territoires, relais, absences, capacité déclarée et estimations de durée. Scénarios de répartition avec explications et comparaison avant/après.
- **Accueil et Tickets** : file ordonnée, sélection depuis le relevé, notes, checklists, modèles, rappels locaux, report et passation, dans des espaces intégrés.
- Jusqu’à 12 onglets de tickets, épingles et sections préférées ; chronologie du traitement, filtres et recherche, changements observés entre deux lectures.
- Cache de lecture facultatif, explicitement daté ; rapprochements locaux entre tickets et solutions réellement lues, avec leurs références.

### Villes, sites et tournées

- Plusieurs termes pour une ville : `MA ; Mars ; Marseille = Marseille`. Reconnaissance des mots entiers, accents et noms composés ; une ambiguïté reste visible.
- Régions, départements, secteurs, hiérarchie libre, sites et bâtiments ; adresses et coordonnées facultatives, priorités des sources, conditions et exclusions des règles, simulateur avec termes surlignés et preuves.
- File des lieux à vérifier, corrections locales par installation et identifiant unique ; import CSV du référentiel avec comparatif avant application.
- Colonnes enrichies, indicateurs de couverture et répartitions par ville/site/territoire.
- Initialisation facultative par `wad/sites.csv`, avec termes de détection, communes, adresses et coordonnées. Enrichissement des champs manquants sans écraser les corrections locales.
- **Terrain** : carte intégrée Leaflet des tickets, sites et visites prévues, sélection de tickets et création explicite de tournées. Rues OpenStreetMap par défaut, y compris en simulation ; repli local hors ligne, sans navigateur externe.
- Tableau des sites avec stock actif, zéros, inconnus, préconisations configurables et copie de tous les identifiants du périmètre.
- Tournées datées à partir d’un panier explicite, états et historique motivé, départ/retour, durées, calendriers de sites et étapes verrouillées. Répartition sur plusieurs jours et techniciens selon compétences, absences, plages de présence et budgets du matin et de l’après-midi ; les capacités inconnues restent exclues de la proposition.
- Rapprochement avec un nouveau relevé, reconstruction explicite des étapes, briefing HTML imprimable et calendrier ICS. Les trajets sont saisis ou indicatifs par défaut. Un service OSRM compatible peut fournir distances et durées routières après configuration et consentement ; sa provenance et sa date restent visibles.

### Atelier de requêtes

**80 modèles** disponibles, jusqu’à 128 définitions mémorisées. Les modèles manquants peuvent être ajoutés au brouillon sans remplacer les requêtes existantes.

Le constructeur propose expressions imbriquées **ET / OU / NON**, comparaison entre champs, listes et intervalles, paramètres typés, champs calculés d’un catalogue borné, trois niveaux de regroupement, périodes, sommes/moyennes/médianes/percentiles et explication du résultat par ticket. Les résultats alimentent les widgets. Aucune requête SQL ni aucun script arbitraire n’est exécuté.

### Historique, qualité, alertes et automatisation

- Relevés locaux datés avec provenance, contrôle d’intégrité, règles d’interprétation conservées, protection et rétention paramétrable.
- Comparaison de relevés compatibles : apparitions, absences, champs modifiés, résolutions observées, réouvertures et réaffectations. **Une absence ne signifie pas une résolution.**
- Diagnostic de qualité par colonne : couverture, valeurs illisibles, identifiants répétés, dates incohérentes et accès aux lignes à examiner.
- Alertes locales sur indicateurs et requêtes : seuil, effectif minimal, exigence de complétude, prise en compte et report. Une donnée devenue indisponible ne résout pas une alerte.
- Plusieurs recherches mémorisées et campagnes séquentielles. Programmation facultative **pendant que l’application reste ouverte**, plages silencieuses et suspension lorsque la session Windows est verrouillée.
- Journal d’opérations, détection des opérations interrompues, sauvegardes versionnées des réglages et restauration avec aperçu.
- Import de configuration par sections, comparatif et fusion facultative. Identité, SSO, dossiers, zoom, notes personnelles et planification locale restent sur le poste.

## Fiabilité de l’extraction

Le mode automatique privilégie le CSV **toutes les pages** proposé par GLPI. S’il est indisponible, il peut utiliser une option d’affichage complet existante puis, si autorisé, parcourir la pagination depuis la première page. **Un fichier corrompu ou un export ambigu n’entraîne pas de repli silencieux.**

Le moteur vérifie la recherche, le contexte observable, les totaux, les colonnes et les doublons de pages. Un échantillon de 25 identifiants au maximum et les valeurs des colonnes comparables sont rapprochés du CSV. Un identifiant attendu absent lors du rapprochement initial fait refuser l’import. Lorsque les identifiants correspondent mais que des valeurs diffèrent, le CSV valide est chargé avec `Complete=false` ; les réserves de concordance restent consultables dans **Qualité du relevé**, sans empêcher d’utiliser les données, les exports et les analyses. Aucun écart ni caractère de contrôle C1 n’est effacé pour obtenir une concordance.

Les textes complets présents dans les popups GLPI sont lus seulement lorsque leur structure et leur association au texte visible sont prouvées. Les abréviations non développées ne sont exclues du contrôle de valeur que dans les cas reconnus au préfixe compatible. Le contrôle final du contexte, du total et de l’échantillon reste actif ; une variation ou une vérification devenue indisponible empêche de certifier un relevé complet. Le classeur automatique d’un résultat à contrôler porte `a-verifier` dans son nom ; le CSV natif conserve son nom et ses octets. Un fichier local importé n’est pas certifié comme extraction exhaustive du portail.

La **réception complète** décrit la récupération du fichier au total annoncé, lorsque cette observation existe. Elle ne certifie ni toutes les valeurs ni l’ensemble de la recherche. Les rapprochements d’identifiants et de valeurs portent sur un échantillon. Les anciennes archives dépourvues de ces observations ne reçoivent pas de nouvelle preuve par interprétation de leurs avertissements.

La limite de lignes de l’application peut réduire le volume chargé ; elle ne tronque pas le CSV natif téléchargé. Une annulation ne déclenche pas d’annonce de réussite. Le diagnostic technique distingue les étapes sans inclure les cellules, cookies ou jetons de la session.

## Données et partage

Les données de travail résident par défaut dans `%LOCALAPPDATA%\GLPI-Pilot`, indépendamment du dossier de l’EXE : profil du navigateur, préférences, archives, notes et tournées. La bibliothèque SQLite réside dans le sous-dossier `Library`, ou dans le dossier local choisi dans **Bibliothèque → Connexion**. Les exports vont dans le dossier choisi. La configuration partageable contient les définitions d’équipe et les adresses autorisées, pas la session SSO. La connexion PostgreSQL et son mot de passe restent propres au poste ; leur mémorisation facultative utilise la protection du compte Windows.

Une base centrale est réservée à une équipe autorisée à consulter l’ensemble du corpus qu’elle reçoit. Elle ne reproduit pas automatiquement les permissions GLPI de chaque utilisateur. L’espace d’équipe partage ses propres commentaires et affectations ; publier une contribution ne modifie pas le ticket dans GLPI.

Dans **Mise à jour et retour arrière**, examiner la release officielle ou choisir un ZIP local, vérifier le pack puis confirmer son installation. L’assistant contrôle l’archive, son commit et chaque fichier, conserve une sauvegarde complète et exige un démarrage confirmé de l’interface. Après un échec, il restaure l’ancien pack ; si un fichier reste verrouillé, il conserve la sauvegarde et signale la récupération nécessaire. Aucun contrôle de release n’est lancé automatiquement au démarrage.

Le remplacement manuel reste possible : fermer l’application et extraire le nouveau ZIP dans un nouveau dossier. Ne pas mélanger ses fichiers avec un ancien `wad`. Une sauvegarde des préférences existantes précède la première ouverture d’une nouvelle version. Un second lancement rappelle la fenêtre déjà ouverte. Si aucune fenêtre n’apparaît, utiliser `wad/support/DIAGNOSTIC_DEMARRAGE.cmd` ; le dernier journal est `Logs/demarrage-dernier.log` dans les données locales.

## Documentation

- [Guide utilisateur](docs/Guide-GLPI-Pilot.html)
- [Bibliothèque, résolution, base locale et partage central](docs/Bibliotheque-et-partage.md)
- [Notes de version 3.4.0](docs/RELEASE-3.4.0.md)
- [Notes de version 3.3.4](docs/RELEASE-3.3.4.md)
- [Notes de version 3.3.3](docs/RELEASE-3.3.3.md)
- [Notes de version 3.3.2](docs/RELEASE-3.3.2.md)
- [Notes de version 3.3.1](docs/RELEASE-3.3.1.md)
- [Notes de version 3.3.0](docs/RELEASE-3.3.0.md)
- [Notes de version 3.2.2](docs/RELEASE-3.2.2.md)
- [Notes de version 3.2.1](docs/RELEASE-3.2.1.md)
- [Notes de version 3.2.0](docs/RELEASE-3.2.0.md)
- [Notes de version 3.1.1](docs/RELEASE-3.1.1.md)
- [Notes de version 3.1.0](docs/RELEASE-3.1.0.md)
- [Historique — version 3.0.2](docs/RELEASE-3.0.2.md)
- [Historique — version 3.0.1](docs/RELEASE-3.0.1.md)
- [Migration initiale vers WebView2 — version 3.0.0](docs/RELEASE-3.0.0.md)
- [Architecture et contrat des opérations WebView](docs/WebView-contract.md)
- [Compilation, caches hors ligne et chaîne Windows](docs/GitHub-et-compilation.txt)
- [Dashboards, indicateurs et extraction](docs/Dashboards-et-extraction.md)
- [Travail personnel, tickets et techniciens](docs/Travail-personnel-et-techniciens.md)
- [Requêtes, villes, sites et tournées](docs/Requetes-villes-tournees.md)
- [Historique, qualité et automatisation](docs/Historique-qualite-automatisation.md)
- [Protocole et résultats de vérification](docs/Rapport-verification.txt)
- [Suivi des 200 améliorations : couverture et reste à réaliser](docs/Suivi-200-ameliorations.md)

## Développer et vérifier

Projet **C# .NET 10, WinForms et WebView2**. Les sources d’interface se trouvent dans `web/` ; aucune chaîne npm n’est nécessaire pour construire le produit. Le SDK WebView2 est épinglé à `1.0.4258.31`, le runtime fixe à `154.0.4258.62` et Playwright à `1.63.0`.

### VM Windows hors ligne

Avec PowerShell 7.2 ou ultérieur, depuis la racine des sources, utiliser les chemins du laboratoire décrits dans [REPRISE.md de Domenia 2](https://github.com/M4D135/domenia-d120/blob/v2/v2/REPRISE.md) :

```powershell
$env:DOTNET_CLI_HOME = 'C:\DomeniaDev\dotnet-home'
$env:NUGET_PACKAGES = 'C:\DomeniaDev\nuget-packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

.\build.ps1 `
  -DotNetPath 'C:\DomeniaDev\dotnet\dotnet.exe' `
  -NuGetOfflineSource 'C:\DomeniaDev\cache\nuget' `
  -RuntimePath 'C:\DomeniaDev\cache\webview2\runtime' `
  -OutputDirectory 'C:\DomeniaDev\builds\glpi-pilot-3.3.2'
```

Le runtime doit être déjà extrait, soit directement dans `RuntimePath`, soit dans son unique sous-dossier. Tous les paquets nécessaires doivent être présents dans le cache NuGet. Une dépendance absente arrête la compilation ; aucun téléchargement de secours n’est tenté. Le script refuse d’écraser une distribution existante : choisir un nouveau dossier de sortie pour une nouvelle construction.

La VM de référence dispose de Windows 11, 4 vCPU et 8 Go. Conserver la session console ouverte et réserver les essais interactifs en VM à la fin du chantier. Cette documentation décrit les commandes à y exécuter ; elle n’atteste aucune connexion à cette VM depuis un autre atelier.

### Préparation connectée et CI

Sur une machine Windows autorisée à télécharger les dépendances :

```powershell
$runtime = .\build-tools\Get-WebView2Runtime.ps1 `
  -CacheDirectory 'C:\GLPI-BuildCache\webview2'

.\build.ps1 -RuntimePath $runtime -AllowOnlineRestore `
  -OutputDirectory '.\artifacts\release-3.3.2'
```

`Get-WebView2Runtime.ps1` utilise uniquement l’URL Microsoft épinglée, vérifie le CAB et chaque fichier extrait, puis réutilise le cache contrôlé. `-CabPath` permet de fournir le CAB par transfert local ; `-Offline` interdit son téléchargement. `build.ps1` ne télécharge jamais le runtime. L’accès NuGet en ligne nécessite `-AllowOnlineRestore` ; il est incompatible avec `-NuGetOfflineSource`.

Le [workflow Windows et PostgreSQL](.github/workflows/windows.yml) prévoit les moteurs C#, les pages branchées aux vrais services, les parcours navigateur synthétiques, puis les packs et le vrai hôte WinForms/WebView2. Les contrôles natifs vérifient thèmes, zoom, navigation, fermeture et mise à jour/rollback sur des copies temporaires. Un second job vérifie la synchronisation contre un vrai serveur PostgreSQL 17 temporaire. La promotion en Release n’utilise que les archives de ce run après réussite des deux jobs. Le détail des commandes, des caches et des prérequis figure dans [GitHub et compilation](docs/GitHub-et-compilation.txt).

Une compilation croisée ne prouve pas le fonctionnement de l’EXE sous Windows. Les résultats définitifs sont les journaux du [workflow associé au commit livré](https://github.com/M4D135/GLPI-Pilot/actions/workflows/windows.yml). Le portail d’entreprise et son SSO réel restent à confirmer dans cet environnement.

## Limites explicites

Les données absentes restent indisponibles. Les dates sont calendaires ; un stock ou une estimation ne mesure pas la productivité d’un technicien. Les analyses historiques décrivent des observations et leurs incertitudes : une disparition n’est pas une résolution, une projection n’est pas un engagement de résultat.

Les notes personnelles, tournées, corrections et scénarios existants restent locaux. Les contributions créées dans l’espace d’équipe et les configurations ou statistiques qui y sont publiées se synchronisent lorsque le central est activé. Seule une action GLPI préparée dans l’assistant dédié puis confirmée explicitement peut écrire dans le portail, selon les droits du compte. Un résultat d’envoi ambigu est signalé et n’est jamais relancé automatiquement.

Les services de routage, météo et génération externe sont désactivés tant qu’aucun fournisseur n’est configuré et autorisé. Le mode local de l’assistant repose sur des règles explicites et une synthèse extractive ; il n’est pas un modèle généraliste exécuté hors ligne. Une réponse externe doit respecter le schéma borné et citer des références présentes dans le contexte ; sa pertinence doit être relue avant usage.

Les packs conservent commit et empreintes. La chaîne Authenticode est prête, mais aucun certificat de confiance n’a été fourni pour cette livraison : les empreintes ne constituent pas une signature d’éditeur. Le comportement du portail privé, de son SSO et des configurations physiques de plusieurs écrans reste à vérifier sur les postes concernés. Outil indépendant, sans affiliation au projet GLPI.
