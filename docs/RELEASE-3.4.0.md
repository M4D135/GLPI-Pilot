# GLPI Pilot 3.4.0 — Six espaces repensés, météo animée et terrain opérationnel

## Une refonte des écrans autour de six usages

GLPI Pilot présente désormais six espaces principaux : **Accueil, Pilotage, Tickets, Terrain, Données et Réglages**. Chaque espace possède sa propre composition et ses actions. Les outils détaillés s’ouvrent dans des panneaux contextuels ; le menu et la palette ne proposent plus le catalogue des anciennes pages.

| Espace | Ce que l’on y fait |
| --- | --- |
| **Accueil** | Retrouver les priorités, alertes et rendez-vous ; reprendre un dossier ou démarrer un travail. |
| **Pilotage** | Lire ensemble la météo, les volumes, la charge, les catégories, les tickets anciens et l’activité des agents ; composer et exporter le même périmètre. |
| **Tickets** | Rechercher dans le relevé, ouvrir la fiche à côté de la liste, consulter les observations, travailler ses notes et préparer une action. |
| **Terrain** | Localiser les tickets, explorer les sites, vérifier les affectations observées et préparer les visites ; gérer le référentiel, les corrections et les tournées depuis la carte. |
| **Données** | Importer un fichier, connecter et collecter une recherche GLPI, vérifier la qualité et les correspondances, consulter la mémoire locale et les échanges d’équipe. |
| **Réglages** | Adapter l’apparence, les seuils et équipes, les connexions, la conservation, les automatismes et les fonctions facultatives ; entretenir l’installation. |

La personnalisation du menu porte sur ces six espaces. Les anciens identifiants de liens restent compatibles : ils conduisent à la section ou au panneau approprié du nouvel espace. Ils ne rétablissent pas une ancienne vue dans la navigation. Les paramètres utiles, dont le ticket et sa révision, la tournée ou le périmètre transmis, sont conservés.

**Assistant de langage**, **Lectures ciblées** et **Effectifs des sites** sont facultatifs. Leur activation se fait dans **Réglages → Fonctions facultatives** ; leur commande apparaît ensuite dans son contexte de travail.

## Un pilotage composé, avec des chiffres que l’on peut examiner

La page Pilotage rapproche la météo, les indicateurs d’activité, les points de vigilance, les groupes, les catégories et les participants dans une seule composition. Ses repères font défiler les sections de cette page. Les analyses avancées, comparaisons d’observations, capacités et réglages s’ouvrent depuis ses actions.

Les équipes peuvent se cumuler. Le technicien et les filtres d’analyse définissent le périmètre commun des chiffres, des tableaux, des détails et des exports. Les compositions personnelles restent disponibles dans le sélecteur ; l’éditeur conserve les titres, styles, dimensions, positions et sections personnalisés. Ajouter, déplacer, régler ou masquer un widget ne change pas son calcul.

**Composer** propose un catalogue de widgets, l’ajout de sections, les commandes d’ordre, les dimensions, les éléments masqués et Annuler/Rétablir. Les filtres enregistrés restent visibles et réinitialisables ; les contextes nommés et la comparaison A/B permettent de reprendre deux périmètres distincts. Les mini-historiques utilisent des observations comparables et signalent leurs ruptures. L’import d’une composition présente un aperçu avant le choix explicite entre ajout et remplacement. Si l’ajout dépasse une limite, le remplacement reste proposé sans tronquer le fichier valide.

Les tickets les plus anciens, les auteurs de suivis et de tâches, leur intersection et leur union, ainsi que l’attribution des tickets gardent leurs mesures distinctes. Les dates inexploitables et les valeurs absentes sont annoncées ; elles ne deviennent pas des résultats nuls ou une série inventée.

### Une météo réellement animée

La référence visuelle examinée est l’[Accueil de Domenia v2 au commit 54b071d](https://github.com/M4D135/domenia-d120/tree/54b071d8cdecaa387a67d6c1a1e620af4674f2ba/v2/web) : bandeau à orbes colorées, surfaces translucides, reflets au survol et graphiques dessinés progressivement. Les scènes météo de GLPI Pilot sont des SVG propres à l’application.

Le soleil, ses rayons et son halo, les différents plans de nuages et les gouttes de pluie possèdent des mouvements séparés. Les scènes ont une amplitude visible et continuent à vivre après l’apparition de la carte. Les états inconnus ou partiels restent identifiables et ne reçoivent pas une gravité inventée.

Les KPI disposent d’une illustration, d’un accent cohérent et d’une hiérarchie commune : titre, valeur, unité, part ou échantillon pertinent, puis définition accessible. Une barre de part exige un dénominateur compatible. Une courbe exige de vraies observations historiques. Les tableaux et les zones d’édition restent stables.

Dans **Réglages → Confort et présentation**, **Animations** propose :

- **Expressives** : choix explicite des animations de l’application.
- **Suivre Windows** : application de la préférence du système.
- **Réduites** : arrêt des effets de mouvement.

Le mode réduit, le contraste renforcé, l’édition d’une disposition, les scènes hors écran et le passage de l’application en arrière-plan sont pris en compte. Les boutons d’une même barre ont une hauteur commune ; les résumés de périmètre et les précisions des champs disposent de leur propre ligne.

## Liste et fiche dans le même espace Tickets

Sur une fenêtre large, la fiche se lit à côté de la liste. Sur une fenêtre plus étroite, le retour à la liste conserve sa recherche, son tri, sa sélection et sa position tant que le relevé reste le même. Les boutons précédent et suivant suivent la séquence de cette liste. Un changement de relevé invalide les anciennes sélections de lignes.

Le lecteur rassemble les faits source, les observations datées, les notes et rappels. Les brouillons, actions assistées, recherches, passations et rapprochements sont accessibles dans des outils ciblés. Les dossiers conservés restent consultables après la sortie d’un ticket du relevé courant.

Les notes personnelles sont enregistrées par **Enregistrer mon travail** ou **Enregistrer et partager la note**. Les écritures en attente du brouillon de suivi et les positions de lecture sont terminées dans leur contexte d’origine avant une bascule de contexte. Ce mécanisme ne constitue pas une sauvegarde automatique générale de tous les formulaires. Les actions GLPI conservent leur aperçu et leur validation explicite.

## Terrain : partir des tickets pour préparer les interventions

Terrain est un espace de travail centré sur la carte. Les marqueurs regroupent les tickets du périmètre ; la liste détaillée reste synchronisée avec le lieu choisi. Les affectations indiquent les techniciens attribués aux tickets observés. Les **visites PRÉVUES** proviennent du planning local enregistré et de la date choisie ; elles ne représentent pas une position GPS actuelle.

Le stock actif est le périmètre de départ. Les filtres permettent de cumuler des équipes et groupes, plusieurs techniciens, les incidents ou demandes et les statuts actifs. **Mes tickets** utilise l’identité déjà choisie. Un ticket appartenant à plusieurs groupes retenus est compté une seule fois comme ligne source. Les périmètres d’équipes et de technicien transmis depuis Pilotage sont repris à l’ouverture.

Un site utilise ses propres coordonnées. Une commune seule fournit un repère explicitement approximatif. Un site sans coordonnées ne reçoit pas silencieusement le centre de sa commune. Les lieux inconnus, les ambiguïtés et les titres absents restent dans les compteurs et la liste paginée.

### Sites, corrections et tournées dans leur contexte

**Explorer les sites** conserve les sites à zéro ticket, leurs stocks, les préconisations et la copie complète des IDs du périmètre. Le seuil d’intervention est celui du profil. Le référentiel et les corrections restent modifiables dans leurs panneaux. Les effectifs datés servent aux ratios par personne ou poste lorsqu’ils sont activés ; ils ne désignent pas les effectifs de techniciens.

Sélectionner des tickets ou un lieu entier permet de **Préparer une tournée**. L’aperçu expose les étapes réelles et les tickets encore non localisés. Le clic **Créer le brouillon** ouvre le planning dans Terrain ; l’enregistrement de la tournée reste une action distincte. Un autre brouillon non enregistré est protégé. La sélection maximale de **2 000 tickets** est annoncée et un dépassement est refusé avant d’ajouter une sélection partielle.

Les calendriers, étapes, horaires, contacts, compétences, capacités, services de route et de météo, exports et partages restent disponibles dans les panneaux de Terrain. Les cartes contextuelles d’un site ou d’une tournée utilisent le même composant embarqué.

### Rues par défaut, fond local en cas d’indisponibilité

À l’ouverture explicite d’une carte, les rues OpenStreetMap sont demandées par défaut, **y compris en simulation**. Si elles sont indisponibles, le fond vectoriel local Natural Earth conserve les repères et affiche son état. **Fond hors ligne** permet aussi de le choisir volontairement.

Les noms, tickets, notes, affectations et coordonnées de marqueurs sont traités localement. Le service de fond reçoit les demandes d’images des tuiles et leurs métadonnées HTTP habituelles. Aucune adresse n’est envoyée à un service de géocodage. Le fond local ne contient pas de rues ; les pointillés d’une tournée indiquent uniquement l’ordre de visites à vol d’oiseau et s’interrompent à une étape sans coordonnées.

## Import et simulation : préparer avant de changer de contexte

Le choix d’un CSV ou d’un classeur est préparé avant de fermer les outils en cours. Annuler le sélecteur ou le choix de feuille conserve le relevé et la page. Un jeton lie le fichier choisi à sa révision et à son empreinte ; un fichier modifié entre le choix et la lecture est refusé.

Après le choix définitif, les écritures en attente du lecteur sont terminées dans l’ancien espace, puis le fichier ouvre une simulation isolée. Une validation refusée restaure la page précédente. Les opérations locales, réglages, notes enregistrées et tournées de simulation restent dans son stockage séparé. Quitter la simulation restaure le profil et le relevé ordinaires.

CSV/TSV et Excel XLSX/XLSM sont pris en charge. Pour Excel, on choisit une feuille visible et sa ligne d’en-têtes ; seules les valeurs enregistrées sont lues. Macros, formules et liaisons externes ne sont pas exécutées. La reprise facultative des paramètres du classeur peut fournir les règles météo, équipes et sites. La provenance du fichier reste distincte de la couverture d’une recherche GLPI.

## Seuils, compatibilité et installation

Les seuils de départ restent **100/200** pour le stock global, **50/100** pour les incidents ou demandes et **10/20** par équipe ou catégorie, avec dépassement strict. Les seuils importés ou personnalisés restent prioritaires. Le rappel d’ancienneté du relevé n’altère pas la couleur d’un état météo calculé.

Fermez l’ancienne instance, puis extrayez le pack complet avec **GLPI-Pilot.exe** et **wad** côte à côte. Le profil existant, les compositions et le référentiel facultatif de sites restent compatibles. Les résultats des contrôles locaux, du workflow Windows et de PostgreSQL sont distingués dans le [rapport de vérification](Rapport-verification.txt) ; la version et le commit de l’artefact publié déterminent la livraison disponible.
