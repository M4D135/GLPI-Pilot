# GLPI Pilot 4.0.12 — collecte utile, vue restaurée et fiches accessibles

## Colonnes utiles par défaut

Le choix par défaut devient **« Colonnes utiles à ma configuration »**. Il conserve les 31 rôles opérationnels nécessaires aux vues et filtres : identifiant, titre, état, dates, priorité, techniciens, demandeurs, groupes, catégorie, site et indicateurs. Les textes Description et Solution sont ajoutés lorsqu’une correspondance ou un usage configuré les demande. Les requêtes mémorisées sont prises en compte, y compris les calculs effectivement référencés. Les options GLPI et plugins sans usage identifié ne sont pas ajoutées automatiquement.

Le choix « Toutes les colonnes proposées par GLPI » reste disponible. L’ancien défaut local « Toutes » passe une seule fois au nouveau défaut utile. Un choix explicite ultérieur, les recherches mémorisées et les réglages explicitement importés ou restaurés sont conservés.

## Préparation temporaire et récupération

La collecte sauvegarde durablement l’état initial de la vue personnelle avant d’envoyer une commande native : activation, identifiants de préférences et ordre des colonnes. Chaque commande est journalisée avant son envoi, puis confirmée par l’état réellement observé. Le journal ne contient ni valeurs de tickets ni jetons de session.

Après le transfert, les ajouts attribuables à Pilot sont retirés. Le même nettoyage est tenté après un échec ou une annulation avec un délai indépendant de l’opération annulée. Si Pilot a créé une vue personnelle temporaire, il revient à l’état initial lorsque cela peut être prouvé sans effacer une modification concurrente. Les colonnes personnelles déjà présentes et leur ordre sont conservés.

Une interruption laisse un journal de récupération. À la connexion suivante, Pilot ouvre les préférences natives sauvegardées et contrôle l’origine, le compte et le contexte observable **avant de charger la liste des tickets**. Il n’envoie pas de nouveau une commande dont l’issue reste ambiguë. Un changement de compte, de profil ou de vue peut laisser la récupération en attente ; le reçu et le journal l’expliquent.

Cette récupération concerne les changements suivis par cette version. Elle ne supprime pas une ancienne vue inconnue créée par une version précédente et ne réinitialise pas les préférences globales. Supprimer manuellement une vue lourde dans GLPI reste nécessaire si aucun journal initial n’existe.

Le reçu de collecte distingue une **vue GLPI restaurée** d’une **récupération en attente**, avec le nombre de colonnes temporaires retirées. Une restauration non confirmée n’est pas présentée comme réussie.

## Repli paginé sans affichage massif

Lorsque l’export natif toutes-pages est indisponible, le repli autorisé parcourt les pages. Il ne sélectionne plus automatiquement une valeur gigantesque de `glpilist_limit` pour afficher toute la recherche. La limite d’affichage GLPI reste conservée, même si l’option « Afficher tout » est proposée. Une limite de collecte dépassée ou un repli désactivé produit un message explicite.

Les filtres de collecte ciblée, la date de début d’historique, la réutilisation des contenus déjà confirmés, la reprise des lots et le taux de couverture du périmètre introduits en 4.0.9 restent disponibles. Ils évitent de collecter les fiches de toute la base pour une sélection limitée.

## Ouverture des fiches après collecte

La connexion peut partir de l’accueil GLPI puis être résolue vers la liste native des tickets. Le lecteur reconnaît désormais cette correspondance lorsque la recherche demandée et le périmètre initial ou final ont été contrôlés. Il vérifie toujours l’origine, l’installation, la liste observée et la recherche actuellement configurée. Un import ou une adresse étrangère ne bénéficie pas de cette correspondance.

L’adresse source est mémorisée avant le téléchargement natif. Un contrôleur d’export tel que `report.dynamic.php` ne remplace donc plus l’adresse du tableau dans le résultat. La preuve du périmètre initial sert à identifier la source ; elle ne certifie ni l’exhaustivité du résultat ni la stabilité finale de la recherche. Les autres contrôles de qualité restent inchangés.

## Qualification et mise à jour

Les contrôles purs couvrent le choix des colonnes, la migration, la source des tickets, la durabilité du journal, les identités natives, l’ordre et les refus de restauration ambiguë. Les scénarios navigateur couvrent les vues legacy et modernes, les annulations, les interruptions, la reprise avant navigation, les changements concurrents et les reçus affichés en mode Standard et Admin.

| Parcours obligatoire | Seuil requis |
| --- | --- |
| Configuration et source des fiches | 81/81 |
| Journal et règles de restauration, sans navigateur | 20/20 |
| Automatisation générale et provenance avant export | 53/53 |
| Colonnes GLPI et récupération | 68/68 |
| Attente et identité des exports natifs | 11/11 |
| Données et sources · Standard | 46/46 |
| Données et sources · Admin | 46/46 |

Les 55 anciens scénarios de colonnes sont conservés et treize scénarios de restauration sont ajoutés. La préparation comprend maintenant son nettoyage ; le groupe dispose de 900 secondes et le préflight exécute les groupes complets, y compris les nouveaux cas. Chaque cas garde sa limite propre de 35 secondes. Les autres groupes gardent leurs limites et les délais de l’application restent bornés séparément. Les seuils ci-dessus sont des critères de qualification, dont la réussite se vérifie dans le run du commit publié.

La publication nécessite la qualification Windows et PostgreSQL complète sur le commit exact, puis la vérification de l’archive originale PROD-LIGHT. Les scénarios utilisent des données synthétiques ; ils ne se connectent pas au portail privé de l’organisation. La page Releases fait foi pour la disponibilité du pack.

Fermer Pilot, extraire tout le pack 4.0.12 publié dans un dossier local puis remplacer les deux exécutables et le dossier `wad/` ensemble. Les données, contenus collectés et réglages utilisateur sont conservés. Une ancienne source déjà enregistrée avec l’adresse du contrôleur d’export doit être recollectée pour disposer de la provenance corrigée.
