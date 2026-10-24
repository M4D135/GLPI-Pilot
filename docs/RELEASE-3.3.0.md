# GLPI Pilot 3.3.0 — pilotage natif et simulation complète

La version 3.3.0 intègre directement les rubriques du pilotage de service dans la navigation et transforme l’import local en environnement de simulation complet. Les modèles, les calculs et les parcours opérationnels sont reliés : un ticket ouvert depuis un chiffre peut être modifié dans la simulation, puis retrouvé dans la bibliothèque et dans les statistiques recalculées.

## Neuf rubriques natives

Le groupe **Pilotage GLPI** est disponible dès l’ouverture. Il ne demande pas l’import préalable d’une disposition de dashboard. Les tableaux personnalisés existants restent disponibles dans **Mes tableaux**.

| Rubrique | Présentation et détail |
| --- | --- |
| **Météo** | Grand total, catégories, équipes, pictogrammes et états, seuils, fraîcheur, analyse et actions en cours |
| **Pilotage** | Stock, attente, demandes/incidents, affectations, indicateurs complémentaires, graphiques et matrices de statuts |
| **Groupes** | Tickets par groupe et statut, totaux, périmètres d’équipe et accès aux lignes contributrices |
| **Catégories** | Répartition par catégorie, volumes, parts du stock et détail |
| **Plus anciens** | Dix plus anciens du périmètre et cinq par groupe, âge calendaire, informations de date et fiches |
| **Suivi agents** | Tickets rédigés distincts comparés aux tickets attribués |
| **Agents actifs** | Suivis, tâches, intersection, union et identité des participants |
| **Sites** | Stock localisé, sites connus ou à vérifier, identifiants et préparation d’interventions |
| **Paramètres du pilotage** | Règles météo, groupes et catégories, ordre, activation, visibilité, seuils, fraîcheur, notes et reprise d’un classeur |

Les périmètres et les sources de calcul restent consultables. Les détails ouvrent les lignes d’origine et leurs fiches ; les données disponibles dans le relevé alimentent les mêmes moteurs que les dashboards personnalisés.

### Règles météo et lecture du stock

La météo native suit le **volume du stock actif**. Les valeurs égales au seuil orange restent maîtrisées ; les valeurs égales au seuil rouge restent à surveiller. Un changement de couleur exige un dépassement strict. Les repères d’ancienneté, de qualité ou de risque existants restent des analyses distinctes.

Une catégorie peut inclure ses descendants délimités par `>`. Les alias d’une règle forment une union. Pour les cartes d’équipe, chaque ticket est affecté à la première équipe correspondante dans l’ordre configuré. Une équipe masquée conserve son rang de priorité ; ses tickets restent dans le total général. Dans la vue Groupes, le même ticket peut participer à plusieurs groupes : cette vue conserve sa définition et indique sa non-additivité.

La fraîcheur du relevé et le texte d’analyse restent visibles séparément des couleurs. Une carte désactivée garde son volume lorsqu’il est calculable. Une information absente, un statut inconnu, une identité douteuse ou une lecture limitée restent explicitement signalés. Le logiciel ne transforme pas ces inconnues en zéro maîtrisé.

### Ancienneté, agents et sites

Les vues dédiées rendent directement accessibles les calculs ajoutés à la version précédente : classement par ouverture puis identifiant numérique, cinq plus anciens dans chaque groupe, liste globale sans répéter les lignes multigroupes, dates absentes ou invalides distinguées. Les couleurs d’ancienneté représentent un âge relatif, sans seuil SLA implicite.

Les participants sont identifiés séparément des cellules qui les contiennent. L’union des tickets avec rédaction vaut **suivis + tâches − intersection**. L’attribution au technicien constitue une autre mesure. Les identités distinctes et les homonymes ne sont pas fusionnés arbitrairement ; une liste facultative de participants peut définir des alias explicites. Ces comptes ne mesurent ni le nombre d’actions ni la productivité.

Le stock par site conserve les sites connus à zéro et distingue les tickets sans site reconnu. Les identifiants sont consultables et copiables. Le seuil d’intervention est configurable ; la valeur par défaut propose une intervention à partir de quatre tickets sur un site connu, tandis qu’un site inconnu demande une vérification dès le premier ticket. Les noms et alias peuvent être utilisés avant qu’une commune ou des coordonnées soient renseignées.

## Import de l’extrait et de son profil de pilotage

**Importer en simulation** accepte CSV, TSV, TXT, XLSX et XLSM. Pour un fichier Excel, l’aperçu propose une feuille source et une ligne d’en-têtes à partir des colonnes reconnues. Un rapport compatible peut ainsi suggérer **Tickets / ligne 4**. L’utilisateur peut changer la feuille ou la ligne ; la suggestion de ligne n’est pas imposée à une autre feuille.

Lorsqu’une feuille **Paramétrage** compatible est détectée, l’aperçu présente :

- Le nombre de règles météo et de cartes visibles.
- Les sites et le nombre d’alias reconnus.
- Le rappel de fraîcheur, l’analyse et les éventuelles réserves de lecture.
- L’option **Reprendre la météo, les équipes et les sites du classeur dans la simulation**, cochée par défaut et désactivable.

Le profil choisi s’applique à la nouvelle simulation. Les sites sont repris avec leurs noms et variantes ; leurs coordonnées ou rattachements ne sont pas inventés. La page Météo s’ouvre avec les chiffres recalculés depuis les incidents importés. **Paramètres du pilotage** permet également d’examiner puis d’appliquer un profil de classeur dans l’espace de travail choisi.

Le lecteur est passif : il utilise les valeurs enregistrées dans le fichier, sans exécuter de macro, formule ou liaison externe. Les anciens totaux des graphiques et des synthèses ne remplacent pas les calculs sur les lignes source. Une formule sans résultat enregistré reste une valeur indisponible et empêche de présenter la lecture comme fidèle et complète. L’ancien format XLS exige une copie XLSX ou CSV.

Les dix-neuf champs d’un extrait métier restent consultables : ID, titre, statut, modification, ouverture, priorité, demandeur, technicien, catégorie, rédacteurs des suivis et des tâches, TTR avec progression, délai de prise en compte, type, groupe, âge déclaré, nombres déclarés de groupes et de rédacteurs, description. Les compteurs déclarés restent distincts des mesures recalculées. Les textes du fichier sont affichés comme du texte.

## Toutes les rubriques dans une simulation isolée

Un bandeau permanent **Simulation · toutes les rubriques** indique l’environnement actif. L’import crée une instance de travail séparée avec ses réglages, sa bibliothèque, ses archives, ses notes, ses sites, ses tournées et ses contributions. Les données de l’espace habituel sont conservées jusqu’au retour.

### Des actions qui produisent un résultat local

| Parcours | Résultat dans la simulation |
| --- | --- |
| Ouvrir une ligne ou un chiffre | Fiche du ticket simulé, lecture de ses informations disponibles et accès au travail personnel |
| Suivi, tâche, affectation ou statut | Aperçu puis confirmation, modification locale, reçu explicite et actualisation de la fiche et des statistiques |
| Connexion et collecte | Source simulée, inspection de tableau, choix, extraction, campagne, historique et bilan |
| Bibliothèque et précédents | Index des tickets importés, contenus disponibles, recherche et rapprochements locaux |
| Palette Ctrl+K | Recherche des écrans, actions et tickets de la bibliothèque simulée, avec accès au lecteur |
| Notes, techniciens, répartition et tournées | Enregistrement et comparaison des scénarios dans cet espace |
| Sites et localisations | Brouillon, aperçu d’impact, application et recalcul du relevé |
| Partage et synchronisation | Contributions locales et échange avec un central simulé |
| Archives, alertes et recettes | Observations, règles et opérations conservées dans la session isolée |
| Assistant et services externes | Résultats locaux signalés comme simulés, issus des données et options du scénario |
| Mise à jour | Pack fictif, version virtuelle, résultat d’installation affiché et retour arrière testable |

Les opérations de simulation n’ouvrent pas de session GLPI réelle, ne synchronisent pas le central réel et ne contactent pas les fournisseurs externes. L’installation simulée conserve l’application ouverte et actualise son bilan. Les exports demandés explicitement peuvent être enregistrés à l’emplacement choisi.

Un extrait importé conserve ses lignes ; il ne reçoit pas de tickets d’exemple supplémentaires. Une colonne, une solution, une position ou une information d’historique absente reste indiquée comme telle. Les données fictives nécessaires à un scénario peuvent être renseignées dans les outils de simulation. **Simulation avec données fictives** fournit également un point de départ et utilise le même mécanisme d’isolation.

### Retour à l’espace habituel

**Quitter la simulation** restaure le relevé d’origine, ses réglages, ses tableaux, ses requêtes, sa bibliothèque et son travail personnel. Les essais de configuration ne sont pas appliqués à cet espace. Un nouvel import crée une nouvelle simulation ; la sortie revient toujours à l’espace habituel conservé. Une annulation de sélection ou un import refusé préserve l’état courant.

La qualité de la source reste distincte du fonctionnement de la simulation : lire tout un fichier ne certifie pas qu’il contient toute la recherche GLPI. Une lecture limitée, des doublons ou des statuts inconnus restent visibles, y compris dans les analyses filtrées.

## Collecte réelle et connexion initiale

Le fonctionnement discret de la collecte réelle est conservé. Le navigateur dédié reste réduit pendant les opérations ordinaires ; un challenge de connexion visible peut être présenté lorsqu’une intervention est nécessaire. L’utilisateur peut alors cocher **Se souvenir de moi** ou utiliser le bouton de connexion du portail. La collecte reprend après authentification. **Afficher le navigateur** reste disponible pour reprendre la main ; les identifiants ne sont pas renseignés automatiquement.

## Vérification et publication

Les validations locales établies pour cette reprise sont :

- **62/62 contrôles du parcours WebView de simulation et de reprise du profil**, dans Chromium avec les modules de production et le véritable backend C#. Les 35 routes sont parcourues, puis les mutations locales et la restauration du contexte d’origine sont vérifiées.
- **21/21 contrôles de reprise du profil de classeur**, couvrant l’inspection et la lecture passive des paramètres.

Le parcours vérifie notamment l’aperçu sans mutation, le changement de statut confirmé, le stock météo recalculé, les fiches depuis le relevé et la météo, l’édition d’un site, la bibliothèque, la synchronisation, Ctrl+K, la collecte inspectée et l’installation virtuelle. Après la sortie de simulation, il vérifie aussi l’aperçu du profil dans les paramètres de l’espace habituel, puis son application explicite, sa persistance et le recalcul météo sur le relevé d’origine. Il ne produit aucune requête externe de la WebView ni erreur JavaScript.

Les packs sont publiés après réussite de la **CI Windows sur leur commit final**, avec les suites et contrôles du vrai hôte WebView2, les packs extraits et leurs inventaires d’intégrité. Le workflow de ce même commit fait foi. Le [rapport de vérification](Rapport-verification.txt) conserve l’état de cette validation ; le commit inscrit dans `wad/version.json` et le workflow associé identifient la livraison.

Les modèles, fixtures et captures publiés utilisent des données fictives. Les classeurs, captures, noms de personnes et correspondances internes restent dans les fichiers privés de leur propriétaire. Les [notes 3.2.2](RELEASE-3.2.2.md) conservent la description historique de cette version ; les restrictions de son ancien mode test sont remplacées par le fonctionnement 3.3.0 décrit ici.
