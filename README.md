# GLPI Pilot 1.5.0 — Windows x64

Application Windows autonome avec interface graphique pour extraire une recherche GLPI au moyen du navigateur et du SSO du portail. Aucun accès API, base de données ou serveur n’est nécessaire.

## Télécharger : PROD ou DEV

Les deux packs complets sont proposés dans les [Releases du dépôt](https://github.com/M4D135/GLPI-Pilot/releases).

| Pack | Usage | Contenu à la racine du ZIP |
| --- | --- | --- |
| **PROD — Windows x64** | Utiliser GLPI Pilot, sans installation de .NET | `GLPI-Pilot.exe` et `wad/` |
| **DEV — Windows x64** | Utiliser le même logiciel et reprendre son développement | `GLPI-Pilot.exe` et `wad/` ; sources et tests dans `wad/sources/` |

Extraire le ZIP entier dans un dossier, puis ouvrir **GLPI-Pilot.exe**. Le dossier **wad** accompagne toujours l’exécutable. Le pack DEV inclut également les scripts de compilation, le workflow Windows et la documentation technique. La commande GitHub **Code → Download ZIP** télécharge les sources du dépôt ; les exécutables se trouvent dans les deux packs des Releases.

## Démarrage en un clic

1. Extraire entièrement le ZIP, puis ouvrir `GLPI-Pilot.exe`. Garder le dossier `wad` à côté.
2. Une première fois, coller **l’adresse complète de la recherche GLPI**, avec ses paramètres et ses filtres, choisir Edge ou Chrome et le dossier des exports dans Réglages si nécessaire.
3. Garder le mode **Export GLPI — toutes les pages**, puis cliquer **Extraire tout**, en haut. Le logiciel enregistre les réglages, ouvre la source avec le navigateur derrière l’application, attend le SSO, retrouve la recherche, choisit le CSV de toutes les pages et attend le téléchargement complet. Il charge ce fichier dans l’aperçu et crée un Excel lorsque la case correspondante est cochée. Si l’export natif n’est pas reconnu ou si le fichier reçu est illisible comme CSV, il essaie la solution **Afficher tout** décrite ci-dessous lorsque la liste le permet.
4. Aux sessions suivantes, cliquer simplement **Extraire tout**. Le profil du navigateur, l’adresse complète, le tableau appris, le mode, le dossier et le thème sont conservés.

Le bouton **Page du navigateur** reprend l’adresse de la recherche affichée dans le navigateur piloté. Une sélection manuelle du tableau dans Outils manuels peut également être mémorisée pour les pages qui contiennent plusieurs tableaux.

## Rester dans le logiciel

**Extraire tout** laisse le navigateur piloté ouvert, en taille normale ou maximisée, derrière l’application. GLPI Pilot reste au-dessus pendant le traitement. Le SSO continue dans ce navigateur réel avec son profil réservé à l’application. Une fenêtre déjà réduite est restaurée ; aucune commande de réduction n’est envoyée.

Le bouton **Voir le navigateur**, en haut de la fenêtre, suspend la priorité de GLPI Pilot et affiche la page sans la recharger. Il reste accessible pendant le traitement et devient **Revenir à GLPI Pilot**. Si une saisie de mot de passe ou de code est détectée, le statut l’indique ; après la connexion, l’extraction reprend automatiquement tant que le délai SSO n’est pas écoulé. Le logiciel ne renseigne aucun identifiant. L’accès volontaire au navigateur reste actif jusqu’au retour demandé dans le logiciel ou au démarrage d’une nouvelle extraction. Une fois le traitement terminé, la priorité de fenêtre est relâchée pour laisser les autres applications fonctionner normalement.

Dans **Outils manuels**, **Ouvrir GLPI seulement** affiche le navigateur pour la préparation initiale. Les actions qui demandent un clic dans la page, comme désigner un tableau, affichent également le navigateur. Ces commandes concernent la fenêtre pilotée par GLPI Pilot.

## Mettre à jour un pack existant

Fermer GLPI Pilot avant de remplacer les fichiers. Les préférences et le profil Edge/Chrome sont stockés dans `%LOCALAPPDATA%\GLPI-Pilot`, indépendamment du dossier de l’application. Ils sont conservés lors du remplacement du pack.

**Recoller une fois l’URL complète de la recherche si ses filtres avaient disparu.** La version 1.0 supprimait les paramètres lors de l’enregistrement ; la 1.1 conserve les paramètres de recherche et retire seulement les paramètres transitoires d’authentification. Les paramètres déjà perdus ne peuvent pas être reconstitués automatiquement.

Le ZIP livré est le pack complet : extrais-le dans un nouveau dossier, sans récupérer de fichier de l’ancien pack. Il contient le nouvel EXE, le pilote complet et les documents. Il n’est pas nécessaire d’installer Python, Node.js ou .NET sur le poste utilisateur. Edge ou Chrome doit être installé et autorisé à s’ouvrir sur ce poste.

## Nouveautés 1.5.0

### Zoom et interface plus lisible

Les boutons **− / 100 % / +**, à côté du bouton de thème, règlent toute l’interface de **75 à 150 %**, par pas de 5 %. Cliquer sur le pourcentage revient à 100 %. **Ctrl+Plus**, **Ctrl+Moins**, **Ctrl+0** et **Ctrl+molette** donnent le même accès. Le zoom est mémorisé automatiquement pour le poste ; importer la configuration d’un collègue conserve votre zoom.

L’en-tête, les actions du relevé et la navigation se réorganisent avec la place réellement disponible après zoom. Les palettes claire et sombre s’appliquent aux nouvelles vues. Les tableaux Techniciens, Tournées et Requêtes utilisent leur propre zone délimitée ; les formulaires et les résultats sont répartis en sous-onglets pour limiter les défilements imbriqués.

### Des widgets qui prennent la place nécessaire

Les cartes du dashboard utilisent une **hauteur automatique** et suivent le contenu. La météo n’ajoute plus son propre ascenseur à celui de la page. Les méthodes de calcul et les explications détaillées se déplient à la demande. Les longues répartitions, alertes et listes de qualité ouvrent une vue complète **paginée et recherchable**, intégrée au dashboard, avec retour à la carte. Les indices des lignes source sont conservés pour retrouver les tickets.

L’éditeur propose une hauteur automatique ou une hauteur minimale manuelle. Agrandir une explication repousse les cartes suivantes ; le contenu reste lisible. Le catalogue présente six résultats par page, avec recherche sur toutes les sources. Déplacement, redimensionnement, verrouillage, styles, couleurs et annuler/rétablir restent disponibles.

### Suivi opérationnel et bibliothèque de 64 modèles

**16 nouvelles sondes** complètent les indicateurs : tickets en attente anciens ou sans modification récente, nouveaux sans modification, priorités hautes et retards sans technicien, affectation à un groupe seul, planifiés à échéance dépassée, anciens sans technicien, actifs sans catégorie, résolus dont la clôture est à suivre, résolus sans date, incidents en priorité haute, et moyenne/médiane/P90/maximum des jours écoulés depuis la résolution des tickets actuellement résolus.

Les seuils d’ancienneté et de modification sont ceux des Réglages. Une date illisible ou une colonne absente reste signalée comme indisponible. L’âge d’un ticket en attente ne représente pas sa durée dans ce statut. Le suivi de clôture ne déduit ni approbation de solution ni délai contractuel.

L’atelier fournit **64 modèles** : les 40 existants et 24 nouvelles répartitions par ville, technicien ou groupe. Dans **Requêtes**, le bouton **Modèles +** ajoute les modèles manquants à votre brouillon, puis **Enregistrer** les mémorise. Vos requêtes modifiées, favoris et identifiants existants sont conservés. La limite reste de 128 définitions au total.

### Annulation et import

Une annulation est désormais contrôlée avant et après les enregistrements automatiques, puis avant l’annonce de fin. Une écriture déjà commencée peut terminer proprement ; elle ne déclenche plus de message de réussite après l’annulation. Le lecteur CSV contrôle aussi l’annulation pendant les longues suites de lignes vides et la normalisation finale. La détection du préambule `sep=` ne charge plus une première ligne entière avant d’appliquer les limites de lecture.

## Fonctions de l’espace 1.4.0 conservées

### Un espace de travail plus direct

La navigation s’adapte à la largeur de la fenêtre : barre latérale sur grand écran, onglets compacts sur une fenêtre plus étroite. **Ctrl+K** ouvre une palette filtrable donnant accès aux principales actions. **Ctrl+L** sélectionne la source, **Ctrl+F** la recherche du relevé, **Ctrl+R** relance l’extraction et **Ctrl+E** propose son export Excel.

L’adresse source possède son bouton **Enregistrer** et un état lisible : vide, invalide, modifiée ou mémorisée. L’extraction indique les étapes réellement traversées, du SSO au fichier prêt, avec le temps écoulé. Le relevé précédent reste disponible pendant l’actualisation et après une annulation ou un échec. Les commandes **Ouvrir le fichier** et **Afficher dans le dossier** retrouvent le dernier export de la session.

La recherche locale porte désormais sur **toutes les lignes chargées**, même lorsqu’elles dépassent les 10 000 lignes affichables. Elle compte toutes les correspondances puis affiche les 10 000 premières ; le tri porte sur cet aperçu. Les colonnes masquées participent à la recherche. **Colonnes** permet de chercher les en-têtes, masquer ou réafficher une colonne, changer l’ordre et régler sa largeur. Cette présentation est mémorisée sur le poste pour la structure exacte du relevé ; les exports complets conservent toutes les colonnes.

### Dashboard global et dashboard personnel indépendants

L’organisation du dashboard global et celle des vues personnelles sont séparées. Modifier les widgets d’un espace ne remplace plus ceux de l’autre. Une ancienne configuration reçoit une copie indépendante de sa disposition pour l’espace personnel. Les techniciens consultés utilisent le modèle personnel commun, avec leurs propres données.

Neuf modèles sont proposés : **Pilotage**, **Exploitation**, **Analyse**, **Mon quotidien**, **Charge de l’équipe**, **Échéances**, **Qualité des données**, **Villes et territoires** et **Page libre**. Chaque espace peut conserver 12 dispositions de 60 widgets au maximum. Les cartes restent déplaçables, redimensionnables, duplicables, masquables et verrouillables ; quatre styles, sept couleurs, titres libres et commandes Annuler/Rétablir permettent d’adapter la présentation.

Les répartitions gagnent une présentation **Anneau** avec une légende accessible et un regroupement **Autres** ouvrant les lignes correspondantes. Les indicateurs peuvent devenir des **jauges** : objectif, seuil de vigilance, seuil critique et sens souhaité sont réglables. La valeur exacte reste affichée même au-delà de l’objectif ; un objectif absent ou une donnée indisponible ne produit pas de fausse jauge. Les objectifs sont des repères d’affichage choisis par l’utilisateur, pas des engagements SLA déduits du portail.

Le catalogue propose une recherche, des catégories, un filtre **Disponibles seulement** et des favoris de session. Le widget **Qualité** répertorie les sondes, leur disponibilité, leur échantillon et leurs règles de calcul. Les principes d’interface repris de Domenia sont les actions rapides, les panneaux contextuels, les états explicites et les thèmes cohérents ; les graphiques utilisent le moteur de GLPI Pilot.

### Plus de sondes du relevé

Aux volumes, répartitions, météo et délais existants s’ajoutent notamment les parts d’actifs, d’attente, de haute priorité et d’échéances dépassées ; les actifs sans technicien ou sans groupe ; les affectations multiples ; les échéances du jour, du lendemain et de fin de semaine ; les urgences en retard ou sans modification récente ; les actifs sans échéance ; les catégories et demandeurs absents ; les dates invalides, futures ou incohérentes ; les durées d’inactivité et le délai entre résolution et clôture. Des répartitions supplémentaires couvrent l’origine, l’entité, les lieux, les types, l’horizon des échéances et la qualité des dates.

Chaque sonde indique son périmètre. Une colonne absente ou des statuts entièrement inconnus donnent une valeur indisponible. Un zéro peut représenter une population calculée réellement vide. Les dates et taux utilisent leurs échantillons exploitables ; les échéances sont calendaires. La météo est une synthèse de la santé du relevé, sans appel à un service météorologique externe.

### Gestion des techniciens

L’espace Techniciens comprend une **gestion de l’équipe** avec tableau triable et virtualisé : volumes, actifs, attentes, priorités hautes, retards, ancienneté, inactivité, statuts inconnus et affectations multiples. Recherche, filtre des actifs et favoris permettent de retrouver une personne. La sélection ouvre son dashboard ou les lignes à examiner dans le relevé.

Un objectif local de tickets actifs peut être défini pour chaque technicien, avec un favori et une note facultative. Le rapport **actifs / objectif** affiche ses limites de couverture ; il décrit un stock de tickets dans la recherche, sans inférer la présence ou la productivité de la personne. Ces repères restent dans `technician-preferences.json` sur le poste. Ils ne réaffectent aucun ticket dans GLPI et ne sont pas transmis par l’export de configuration.

**C’est moi** mémorise l’identité personnelle. L’option **Ouvrir mon espace après une extraction réussie** attend la nouvelle détection des techniciens avant d’ouvrir le bon dashboard. Elle reste locale et ne déclenche aucune extraction au lancement. Les affectations multiples appartiennent à chaque personne concernée : leurs totaux ne sont jamais présentés comme un total de tickets distincts.

### Villes, variantes et répartition géographique

Dans **Réglages → Correspondances des villes**, définissez un nom de référence et plusieurs variantes. Exemple : **Marseille** avec **MA**, **Mars** et **Marseille**. Le nom de référence est lui-même reconnu automatiquement. Le collage groupé accepte une ville par ligne, `Marseille | MA ; Mars` ou `MA ; Mars ; Marseille = Marseille`. Les exemples affichés ne sont jamais ajoutés à votre configuration sans votre action.

La recherche porte sur les mots entiers du titre, sans distinction de casse ni d’accents. Elle reconnaît aussi les noms composés ; un terme court inclus dans une ville plus longue ne remplace pas ce nom complet. Deux villes présentes à des endroits distincts, ou un alias partagé entre plusieurs villes, donnent un résultat **ambigu**. Le logiciel ne choisit pas une ville arbitrairement. Un alias court comme `MA` peut désigner autre chose dans un titre : contrôlez les résultats et ajustez votre liste.

L’enrichissement ajoute **Ville détectée** et **Repérage ville** à l’aperçu et aux exports produits localement. Si ces en-têtes existent déjà, un suffixe évite de remplacer les données d’origine. Les états distinguent une ville reconnue, aucune correspondance, une ambiguïté et un titre indisponible. Les quatre catégories sont disjointes. Le CSV natif téléchargé reste intact ; le parcours automatique crée aussi un CSV enrichi et, si demandé, un Excel enrichi. Une modification des correspondances peut être appliquée au relevé déjà chargé : le calcul repart des cellules d’origine, sans empiler les colonnes dérivées.

La liste est mémorisée et incluse dans la configuration partageable. Elle accepte jusqu’à 1 000 villes et 12 variantes par ville, sous réserve de la limite globale du fichier partagé. Un code postal et des coordonnées peuvent être renseignés volontairement. Aucune adresse exacte n’est déduite d’un simple titre.

Le dashboard utilise la ville détectée si aucune autre colonne Ville n’a été choisie explicitement. Le modèle **Villes et territoires**, les répartitions par ville et les sondes de couverture, d’ambiguïté ou de titres manquants sont disponibles dans le catalogue. Les mêmes calculs suivent le périmètre personnel du technicien. L’atelier ajoute les vues de charge, d’urgence, de retard et de suivi par ville.

### Villes et tournées

L’onglet **Villes et tournées** prépare une sélection pour toute l’équipe ou pour un technicien détecté. Les tickets actifs connus constituent le périmètre initial ; l’option de tous les statuts est disponible. Chaque ville regroupe les lignes correspondantes, avec les volumes et les signaux calculables. Les tickets sans ville, ambigus ou sans titre restent accessibles séparément.

L’ordre des étapes peut être ajusté manuellement ou classé par nom, priorité ou échéance. Si toutes les étapes possèdent des coordonnées, un ordre de proximité géographique est proposé. Il utilise les distances à vol d’oiseau : il ne calcule ni les routes, ni le trafic, ni une heure d’arrivée. Les arrêts représentent des villes, pas des adresses d’intervention. L’ordre choisi est mémorisé localement.

L’export CSV/Excel reprend les lignes du périmètre sélectionné avec l’ordre de visite, la ville et l’état de localisation. Les lignes non localisées sont conservées sans faux numéro de visite. Un clic volontaire peut ouvrir une carte publique avec le nom de ville et son code postal uniquement. Il ne transmet pas les tickets, les techniciens ou le texte des incidents.

### Atelier de requêtes et widgets personnalisés

La bibliothèque fournit **64 requêtes prêtes à l’emploi** pour les villes, les techniciens, les échéances, l’activité et la qualité du relevé. Vous pouvez rechercher une définition, la marquer comme favorite, la dupliquer, la modifier ou créer votre propre requête. Les définitions et les critères sont mémorisés et suivent l’export de configuration.

Le constructeur choisit un périmètre de départ — toutes les lignes, tickets actifs, retards, priorités hautes, etc. — puis combine jusqu’à 20 conditions avec **toutes** ou **au moins une**. Les champs peuvent suivre un rôle reconnu du dashboard ou un en-tête exact. Les opérateurs couvrent les textes, les valeurs vides, les nombres et les dates, y compris les fenêtres relatives en jours. Les valeurs sont des critères littéraux, sans exécution de SQL ou de script.

Les calculs disponibles sont le nombre de lignes, la part dans le périmètre de départ, le nombre de valeurs distinctes et, sur une colonne numérique, la somme, la moyenne, le minimum ou le maximum. Un regroupement facultatif répartit les lignes retenues par ville, technicien, catégorie ou autre colonne. **Le regroupement compte toujours les lignes**, même si l’indicateur principal calcule une moyenne ou une somme.

Testez la requête sur le relevé chargé, contrôlez son échantillon et ouvrez les lignes retenues. Après enregistrement, ajoutez son indicateur ou sa répartition au dashboard global ou personnel ; les résultats se recalculent avec les prochains relevés. Une requête supprimée laisse ses anciens widgets signalés comme indisponibles, jusqu’à leur remplacement ou suppression. La bibliothèque accepte 128 définitions au total, modèles compris.

Une colonne requise manquante ou ambiguë rend la requête indisponible, même en mode OU. Une cellule numérique ou date illisible est signalée : en ET, la ligne ne remplit pas toutes les conditions ; en OU, une autre condition vraie suffit à la retenir. Le diagnostic distingue les lignes contenant une valeur illisible de celles qui n’ont pas été retenues. Les sommes ou moyennes sans valeur numérique exploitable restent indisponibles. Une requête dans un dashboard personnel reste limitée aux lignes de ce technicien.

### Configuration et compilation Windows

L’export JSON transporte les deux ensembles de dispositions, les objectifs et seuils des widgets, les styles, les réglages de lecture, les correspondances de villes et la bibliothèque de requêtes avec ses critères. L’identité personnelle, l’ouverture automatique de l’espace, les colonnes de l’aperçu, les repères locaux des techniciens et la session SSO restent propres au poste destinataire.

Les sources incluent un workflow GitHub Actions Windows : contrôles du moteur, harnais WPF et démarrage du pack extrait dans un état temporaire isolé. Ce workflow doit être exécuté avec succès sur GitHub avant de constituer une preuve de validation Windows ; sa présence dans les sources ne signifie pas qu’il a déjà tourné. Voir `docs/GitHub-et-compilation.txt` et le rapport de vérification livré.

## Démarrage renforcé depuis 1.3.1

La 1.3.1 journalise le démarrage avant le chargement des ressources WPF, puis distingue le chargement de la fenêtre, le thème, le détail ticket, le dashboard, les techniciens et la restauration des réglages. Une erreur de construction ou de premier rendu ne doit plus rester dans une instance invisible : un message indique l’étape et le fichier de diagnostic, puis l’application quitte avec un code d’échec.

Le modèle de lecture du ticket est désormais compilé avec l’application ; il n’est plus analysé dynamiquement à chaque lancement. Les événements des espaces Techniciens, Villes et Requêtes sont branchés après la construction de leurs contrôles. Ces changements durcissent le démarrage ; la cause exacte du blocage signalé sur le poste utilisateur n’a pas encore été confirmée par un journal Windows.

Le dernier journal se trouve dans `%LOCALAPPDATA%\GLPI-Pilot\Logs\demarrage-dernier.log`. Un journal daté est aussi conservé. Si ce dossier n’est pas accessible, le logiciel essaie `%TEMP%\GLPI-Pilot\Logs`. Une impossibilité d’écrire le diagnostic n’empêche pas à elle seule l’application de démarrer.

Si l’EXE ne montre toujours aucune fenêtre, lancer **`wad\support\DIAGNOSTIC_DEMARRAGE.cmd`** depuis le pack entièrement extrait. Il lance l’EXE de ce pack, observe ses vingt premières secondes et ouvre un rapport dans le Bloc-notes. Le rapport indique les fichiers présents, l’éventuelle erreur Windows, le code de sortie et le journal de démarrage de cet essai. Il ne ferme pas de processus et laisse l’application ouverte si elle démarre. Le script suit la politique PowerShell du poste ; un éventuel refus est également enregistré.

Avant de réessayer, fermer une ancienne instance de GLPI Pilot, y compris si elle reste visible uniquement dans le Gestionnaire des tâches. Ne pas supprimer la configuration ou le profil SSO pour tester cette version.

## Nouveautés 1.3.0

### Un dashboard personnalisable

Le bouton **Personnaliser** ouvre le mode édition. **+ Widget** donne accès aux indicateurs, répartitions, météo, activité datée, tickets à examiner, qualité du relevé et notes libres. Choisis les informations utiles à ton activité ; une colonne absente reste explicitement indisponible.

- Déplace un widget par sa poignée et redimensionne-le par son coin inférieur droit. La grille de 12 colonnes s’adapte à la largeur disponible ; les cartes occupent l’espace sans les grands trous de l’ancienne présentation.
- Modifie le titre, la largeur, la hauteur, la couleur d’accent et le style de carte. Les répartitions proposent plusieurs présentations et un nombre de valeurs réglable.
- Duplique, masque, réaffiche ou supprime un widget. Le verrouillage protège son déplacement et sa taille. Les commandes d’ordre offrent une alternative au glisser-déposer.
- Conserve plusieurs tableaux de bord nommés, avec des modèles de départ pour le pilotage, les opérations et l’analyse, ou une présentation vide.
- Annuler et rétablir permettent de revenir sur les modifications de la session d’édition. La disposition est sauvegardée automatiquement et fait partie de la configuration partageable.

Les widgets restent calculés sur tout le relevé disponible, indépendamment des 10 000 lignes maximales de l’aperçu. Cliquer un indicateur ou une répartition retrouve les lignes concernées. **Bilan Excel** exporte les chiffres et leurs limites ; un bilan trop grand pour une feuille est proposé en CSV complet.

### Techniciens et tableau de bord personnel

Dans **Techniciens**, les personnes sont détectées dans la colonne d’affectation du relevé. Choisis ton nom puis **C’est moi** : le choix est mémorisé sur ce poste. **Mon tableau de bord** retrouve ensuite ta vue. Les onglets de techniciens permettent de consulter les autres périmètres avec les mêmes widgets personnalisables. Une recherche porte sur tous les noms ; les onglets sont présentés par groupes de 24 avec les commandes de navigation.

Les volumes, statuts, priorités, échéances disponibles, anciennetés et météo sont recalculés sur les tickets affectés au technicien sélectionné. Un ticket partagé peut apparaître chez plusieurs techniciens : leurs totaux ne doivent pas être additionnés. Les noms séparés explicitement par des retours à la ligne, des points-virgules ou des barres verticales espacées sont reconnus ; un nom ambigu n’est pas découpé au hasard. L’absence d’un technicien dans la recherche reste signalée. Cette vue ne cherche pas les tickets qui ne font pas partie du relevé.

Le choix personnel **C’est moi** reste local lors du partage d’une configuration. Le collègue conserve son identité et choisit son propre nom si nécessaire. Les dispositions des widgets sont communes aux vues équipe et technicien et sont partageables ; les chiffres suivent toujours le périmètre sélectionné. Le bilan personnel exporte ce périmètre, tandis que les exports du relevé depuis Extraction conservent toutes ses lignes.

### Un espace de consultation complet pour le ticket

Une sélection prépare la lecture automatiquement ; un double-clic ou **Détail du ticket** ouvre la fiche. Le menu présente les sections lues et les onglets de consultation réellement disponibles dans GLPI. La partie **Traitement du ticket** est lue automatiquement lorsqu’elle est détectée : suivis, tâches, solutions, validations ou documents présents dans la chronologie. Les autres onglets se chargent à la demande.

Les descriptions longues et les textes de traitement utilisent toute la largeur disponible. Les éditeurs de texte enrichi associés à un champ visible sont pris en compte, tandis que les zones de dépôt de fichiers et les commandes d’ajout ne deviennent plus de fausses données. Recherche dans le détail, choix de section, texte intégral, actualisation et export restent accessibles dans l’application. La fiche utilise son onglet navigateur dédié ; la recherche est conservée.

Les sections peuvent toujours être définies dans Réglages lorsque le portail utilise une présentation particulière. Un contenu absent, une limite ou un onglet non reconnu sont signalés. Les boutons GLPI d’ajout, de sauvegarde et de suppression ne sont pas activés par cette consultation.

### Données mieux reconnues et diagnostic plus lisible

L’impact reconnaît aussi les libellés masculins, comme **Moyen** et **Haut**. Priorité, urgence et impact restent distincts. Les noms de colonnes qualifiés par un contexte GLPI connu sont mieux reconnus ; les doublons et correspondances ambiguës restent à choisir explicitement. Les caractères de contrôle trouvés dans certains CSV ne bloquent pas l’import et ne sont pas supprimés. Le détail technique demeure dans le diagnostic, avec un message plus court dans l’interface.

La configuration JSON partage la source, le mode d’extraction, les repères, les sections, les correspondances, les seuils, les tableaux de bord et les widgets. L’import valide la structure et les limites avant de remplacer les réglages, et sauvegarde l’ancienne configuration. La session SSO et le dossier d’export du poste restent locaux.

### Fonctions conservées

- Interface plus sobre : une action **Extraire tout** dans la barre supérieure, source compacte, aperçu sur toute la largeur, réglages regroupés et journal repliable. Les deux thèmes utilisent des surfaces neutres cohérentes.
- Navigateur laissé ouvert derrière GLPI Pilot pendant le traitement automatique, avec commandes **Voir le navigateur / Revenir à GLPI Pilot** disponibles à tout moment.
- Import CSV corrigé : les caractères de contrôle textuels C0/C1 sont conservés, avec diagnostic de leur code Unicode et de leur nombre. L’erreur observée en 1.1.1 venait d’un refus global trop strict ; le caractère exact du fichier utilisateur n’a pas été identifié, faute de disposer de ce fichier.
- Export Excel adapté : les caractères non représentables directement en XML sont encodés avec SpreadsheetML, sans les supprimer ni altérer les séquences littérales comme `_x000B_`.
- Reprise automatique par **Afficher tout** si le fichier natif ne peut pas être lu comme CSV, avec vérification du total et conservation du fichier reçu.
- Commande **Importer un CSV…** pour charger un fichier déjà téléchargé et créer un Excel sans refaire le SSO. La portée GLPI d’un fichier local seul reste non confirmée.
- Parcours automatique unique, avec sauvegarde de la source et des réglages avant le démarrage.
- Retour à l’adresse demandée après le SSO, suivi des onglets ouverts et réapplication de la recherche si ses paramètres ont disparu de l’adresse affichée.
- Actualisation limitée d’un écran de connexion du portail resté inchangé, sans actualiser la page du fournisseur d’identité ou un formulaire de mot de passe/MFA.
- Export natif **CSV toutes les pages**, reconnu sur les structures officielles GLPI 10 et 11 et le formulaire historique. La réception du téléchargement est armée avant le clic.
- Recours automatique à **Afficher tout** si l’export natif n’est pas reconnu ou si le CSV est illisible : sélection d’une option existante suffisante pour le total connu, rechargement de la liste puis contrôle de sa portée et de son nombre de lignes.
- Lorsqu’un CSV natif est reçu, l’aperçu et l’Excel sont construits à partir de son contenu.
- Import CSV avec gestion des accents, champs multilignes, guillemets et identifiants comportant des zéros initiaux. Le fichier natif reste intact.
- Deux thèmes complets, clair et sombre, avec bouton soleil/lune en haut et choix mémorisé.
- **Copier le diagnostic** pour transmettre les étapes, durées, changements d’onglet et compteurs, sans cellules, cookies, mots de passe ni jetons SSO.

## Contrôles affichés

Le nombre de lignes importées est comparé au total annoncé par GLPI. Un total absent ou différent est signalé. Les fichiers Excel à vérifier portent le suffixe `a-verifier`. Le nombre de lignes de l’aperçu ne devient jamais artificiellement le total attendu.

L’aperçu affiche au maximum 10 000 lignes. La limite d’import paramétrée dans Réglages s’applique aux lignes conservées en mémoire et au fichier Excel ; **elle ne tronque pas le CSV natif téléchargé**. Le logiciel indique alors le nombre de lignes du fichier et le nombre chargé. Le filtre local de l’aperçu ne réduit pas les fichiers exportés.

Une recherche vide est reconnue grâce au total zéro de GLPI, sans chercher un bouton d’export que le portail masque. Les en-têtes disponibles sont sauvegardés sans transformer le message « Aucun résultat » en ligne de données. Si aucun en-tête n’est disponible, un rapport texte confirme le résultat vide.

La solution **Afficher tout** utilise une option déjà proposée dans la liste GLPI. Elle nécessite un total connu et une capacité suffisante. Si le total dépasse la limite de lignes réglée dans l’application, le traitement s’arrête avant de demander l’affichage de toute la liste. Sinon, le logiciel attend le rechargement, lit le tableau agrandi et contrôle le nombre de lignes, le début et la fin de la plage ainsi que l’absence de page suivante avant de confirmer la totalité. Le journal et le bilan précisent cette méthode. Le CSV et l’Excel sont alors créés localement à partir du texte visible du tableau. Si cette méthode suit un échec de lecture du CSV natif, ce fichier reçu reste intact et distinct des exports locaux. Si les contrôles ne concordent pas, cette tentative s’arrête avec un diagnostic et ne crée aucun export complet depuis la liste affichée.

Le mode alternatif **Parcours du tableau** recharge la recherche avec l’offset GLPI `start=0`, puis parcourt les pages suivantes. Il conserve les lignes déjà lues en cas d’arrêt, respecte les limites de pages/lignes et vérifie le total. Les actions manuelles « Cette page » et « Pages suivantes » gardent leur périmètre explicite dans Outils manuels.

Une page adaptée par l’entreprise peut nécessiter un réglage initial du tableau ou des repères d’export. En cas d’ambiguïté, le logiciel affiche un diagnostic au lieu de choisir une autre instance GLPI ou un export de la page courante. L’export doit être proposé à l’utilisateur par le portail.

## Diagnostic

Cliquer sur **Copier le diagnostic**, puis coller le texte dans la conversation. Le rapport inclut la version, le navigateur, le mode, les délais, les limites, l’adresse sans ses paramètres, les intitulés des colonnes et leurs correspondances, ainsi que le journal de la session. Il n’inclut pas les valeurs des tableaux ni les fichiers exportés.

Les journaux quotidiens se trouvent dans `%LOCALAPPDATA%\GLPI-Pilot\Logs`. Les erreurs navigateur sont réduites à leur première ligne, sans extraits HTML ni trace détaillée contenant des valeurs de la page. Le diagnostic d’import indique l’encodage, le BOM, le séparateur, les dimensions et les codes des contrôles inhabituels avec leur nombre et premier offset UTF-16 (base zéro, BOM exclu), jamais un extrait du fichier. Le nom du fichier et les cellules ne sont pas inclus dans ce diagnostic d’import.

## Contenu et compilation

Chaque pack possède exactement deux entrées à sa racine : `GLPI-Pilot.exe` et `wad`. Le dossier `wad` contient le pilote `.playwright`, la documentation dans `docs`, les licences, les diagnostics de démarrage et les empreintes SHA-256. Le pack DEV ajoute les sources complètes dépliées dans `wad/sources` ; le pack PROD contient les fichiers nécessaires à l’utilisation.

Projet C# WPF, .NET 10, publication autonome Windows x64. Avec le SDK .NET 10 installé :

```powershell
.\build.ps1
```

La publication conserve le pilote Playwright Windows à l’extérieur de l’EXE dans `wad/.playwright`, avec ses licences. Les sources incluent les vérifications d’export, d’import CSV, de navigation et de DOM. Aucun identifiant, cookie ou contenu du portail utilisateur n’est livré avec les sources.

Les journaux du workflow Windows sont associés au commit contrôlé : tests des moteurs, rendu WPF dans les deux thèmes, captures, démarrage du pack et mémorisation du zoom. La compilation croisée Linux seule ne prouve pas un démarrage Windows. Le SSO, le comportement du premier plan sur le poste utilisateur et les personnalisations de son portail restent à confirmer dans cet environnement réel. Outil indépendant, sans affiliation au projet GLPI.
