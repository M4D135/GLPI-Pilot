# Historique, qualité, alertes et organisation

## Des relevés datés et comparables

L’archivage local est activable dans **Sources et organisation**. Chaque relevé enregistré conserve ses cellules, son origine, la méthode d’extraction, les dates, les limites observées et les règles utilisées pour interpréter ses colonnes. Une empreinte SHA-256 contrôle l’intégrité du contenu compressé lors de sa réouverture. Il s’agit d’un contrôle de corruption, pas d’une signature ni d’une preuve inviolable.

La rétention est réglable par âge, nombre d’archives et budget disque. Le réglage initial est de 180 jours, 500 archives et 1 024 Mio. La dernière archive de chaque source, son dernier relevé complet comparable et les archives protégées sont conservés : elles peuvent donc faire dépasser un objectif de rétention. Le catalogue permet de protéger un relevé, de le rouvrir et de l’exporter. L’ouverture du dernier relevé au démarrage est facultative.

Un historique se consulte à sa date et avec ses règles d’interprétation enregistrées. Les seuils modifiés depuis ne réécrivent pas ses cellules. La présentation actuelle peut être utilisée pour le consulter. Une fiche de ticket rouverte dans le navigateur reste, elle, une lecture du portail à l’instant de l’ouverture ; elle ne devient pas rétroactivement la fiche d’origine de l’archive.

La comparaison exige deux relevés complets de la même recherche et du même contexte observable, des colonnes et règles compatibles, des identifiants valides et uniques et un ordre temporel cohérent. Elle distingue les tickets apparus, absents et modifiés. Résolutions, réouvertures et changements d’affectation ne sont comptés que lorsqu’ils sont observés entre deux lignes possédant le même identifiant. Une ligne absente peut être sortie du filtre, déplacée ou devenue inaccessible : elle n’est jamais comptée comme résolue pour cette seule raison.

Les courbes globales utilisent quatre indicateurs stockés dans des archives comparables : volume, actifs, échéances dépassées, priorités hautes actives. Un point incompatible crée une rupture. Les courbes personnelles et filtrées ne sont pas reconstruites dans cette version.

## Examiner la qualité

L’espace **Qualité** sépare l’exhaustivité de l’extraction, la présence des colonnes nécessaires et l’interprétation de leurs valeurs. Il donne, par colonne, le nombre de valeurs renseignées, vides ou illisibles et des exemples utiles. Le comptage des valeurs distinctes est borné ; le rapport l’indique lorsqu’une limite est atteinte.

Les contrôles recherchent notamment les identifiants non interprétables ou répétés, les dates futures, les dates de résolution ou clôture antérieures à l’ouverture, les statuts non reconnus et les champs nécessaires absents. Les lignes en cause sont accessibles sans recopier leur contenu dans les journaux techniques. Les limites d’affichage du détail ne réduisent pas les compteurs globaux.

Un import local CSV/XLSX reste de portée GLPI non confirmée. Le lecteur XLSX utilise la première feuille visible, les valeurs de cellules et les résultats de formules déjà enregistrés : il n’exécute pas les formules, ne calcule pas Excel et ne suit pas les liens externes. Les formats usuels de dates, les chaînes partagées et les identifiants à zéros initiaux sont pris en compte. Une feuille atypique peut nécessiter un CSV préparé depuis Excel.

## Alertes sur les chiffres disponibles

Dans **Alertes**, créer une règle à partir d’une mesure du catalogue ou d’une requête. Définir l’opérateur, le seuil, l’échantillon minimal et l’exigence d’un relevé complet et vérifié. Jusqu’à 50 règles locales sont conservées.

Les évaluations se font sur le dashboard global du relevé courant, sans filtre de consultation. Les démonstrations, archives rouvertes et dashboards personnels ne déclenchent pas ces alertes. Un même relevé n’ouvre pas plusieurs fois la même occurrence. Les incidents sont séparés par source et par définition de règle.

Une valeur inconnue, un échantillon trop petit ou un relevé partiel ne clôture pas une alerte existante. La clôture par retour à la normale nécessite une mesure valide. Modifier ou désactiver une règle termine ses anciennes occurrences en indiquant le changement de règle ; cela n’est pas présenté comme une amélioration des données.

La prise en compte et le report d’une heure organisent les alertes à examiner. Une nouvelle occurrence après retour à la normale repart sans prise en compte. L’accès aux tickets concernés nécessite que le relevé associé soit encore chargé. Ce système reste dans l’application ; il n’envoie ni courriel ni message à un collègue. Les notifications Windows des rappels personnels appartiennent à **Ma journée**.

## Vues du relevé

**Vues** mémorise la recherche, les filtres par colonne, jusqu’à quatre niveaux de tri et les colonnes affichées. Les filtres couvrent texte, égalité, présence, nombres et dates, selon le type choisi. Un clic d’en-tête trie tout le résultat local ; Maj + clic ajoute un niveau. Les filtres et tris sont appliqués avant la limite de 10 000 lignes de l’aperçu.

La structure exacte des en-têtes lie une vue à son relevé pour éviter d’appliquer des numéros de colonnes à des champs différents. Jusqu’à 64 vues sont conservées localement. **Exporter la vue** exporte toutes les lignes qui correspondent aux critères, avec les colonnes visibles, même si l’aperçu n’en affiche qu’une partie. L’export complet garde son périmètre distinct.

## Sources, campagnes et horaires

Chaque source mémorise un nom, l’URL de recherche, le navigateur et la méthode d’extraction. La campagne parcourt les sources cochées successivement ; elle ne lance pas plusieurs sessions SSO en parallèle et ne fusionne pas arbitrairement les recherches. Les fichiers et archives gardent leur source.

La programmation est désactivée par défaut. Lorsqu’elle est activée, elle s’exécute seulement tant que GLPI Pilot reste ouvert. L’intervalle est borné à 5 minutes minimum. Un travail déjà en cours, un éditeur intégré ouvert, une plage silencieuse ou le verrouillage de la session Windows retardent le lancement. Un contrôle de fraîcheur signale un relevé ancien. Aucune tâche Windows permanente n’est installée et l’application ne se réveille pas lorsqu’elle est fermée.

Le journal d’opérations conserve le type d’opération, ses dates, son résultat et les erreurs utiles, dans une limite de 500 entrées. Une opération restée ouverte lors de l’arrêt précédent est marquée comme interrompue ; elle n’est pas automatiquement annoncée réussie ni reprise au milieu d’une pagination.

## Configuration et récupération

L’enregistrement des réglages est atomique. Les 30 versions précédentes valides sont conservées lorsque le contenu change. Un fichier courant illisible est conservé à part ; le logiciel essaie une sauvegarde valide et affiche ce qu’il a récupéré. La restauration manuelle présente un aperçu avant application.

L’import d’une configuration permet de sélectionner les sections et de comparer leurs volumes. Le mode fusion conserve les éléments déjà présents avec le même identifiant, puis ajoute les nouveaux dans les limites prises en charge. Le remplacement est explicite par section. Une ancienne configuration qui ne possède pas de liste de sources conserve les sources du poste destinataire.

Le partage inclut les réglages de recherche, les repères, les définitions de dashboard, les villes/sites et les requêtes. Le profil SSO, les cookies, le dossier d’export, l’identité personnelle, le zoom, la programmation, l’historique, les alertes et les notes locales ne sont pas transmis par ce fichier. Les adresses partagées restent des informations d’équipe à vérifier avant de remettre la configuration à un collègue.

## Présentation et démonstration

Les réglages locaux proposent densité, couleur d’accent, suivi du thème Windows, contraste, réduction des animations, colonnes figées et mode concentré qui masque le journal et le contexte secondaire. Les dimensions et la position de la fenêtre sont enregistrées lorsqu’elles ont changé. Le zoom est mémorisé séparément.

La démonstration charge 180 tickets entièrement fictifs. Ils ne sont pas archivés et ne déclenchent pas les alertes. Le mode permet d’explorer l’interface avant de se connecter à son portail.
