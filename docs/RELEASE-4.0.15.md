# GLPI Pilot 4.0.15 — pagination native et fin de collecte

## Pagination avant l’extraction de secours

La collecte de bibliothèque lit d’abord l’index GLPI, puis les fiches une par une. Elle prépare maintenant le nombre de tickets par page avant de lire cet index. Le repli paginé utilisé lorsque la commande d’export CSV native est absente emploie le même contrôle.

Pilot utilise uniquement les options du sélecteur natif associé au tableau Ticket : 1 000, sinon la plus petite option supérieure ; une limite déjà au moins égale à 1 000 est conservée. Si aucune option n’atteint ce seuil, la plus grande limite disponible est utilisée. Un contrôle absent ou ambigu laisse la pagination actuelle en place. Aucune option ni adresse de commande n’est inventée.

Après une seule commande native, la liste est relue : limite, plage affichée, total, identifiants, colonnes, critères et contexte doivent rester cohérents. Si le changement est refusé et que l’ancienne liste est encore vérifiable, la lecture continue avec la limite réellement observée. Une dérive de session, de filtre ou d’identités arrête la collecte. Une commande incertaine n’est jamais renvoyée automatiquement.

Le réglage de pagination reste dans la session GLPI et sa valeur est indiquée dans le journal. Le filtre précédent est restauré après la collecte de bibliothèque. Les limites configurées de collecte et la lecture séquentielle des fiches restent applicables ; 1 000 lignes par page ne signifie pas 1 000 ouvertures simultanées.

Les contrôles ont été rapprochés des sources officielles : [pager GLPI 9.5.13](https://github.com/glpi-project/glpi/blob/9.5.13/inc/html.class.php), [options natives 9.5.13](https://github.com/glpi-project/glpi/blob/9.5.13/inc/dropdown.class.php), [sélecteur GLPI 10.0.28](https://github.com/glpi-project/glpi/blob/10.0.28/templates/components/dropdown/limit.html.twig) et [actualisation fluide GLPI 10.0.28](https://github.com/glpi-project/glpi/blob/10.0.28/js/modules/Search/Table.js). Le rechargement automatique qui suit un refus AJAX est attendu avant de valider une poursuite sur l’ancienne limite.

## Restauration des colonnes

Le délai indépendant de nettoyage est renouvelé après une étape native confirmée et journalisée. Une suite de dix retraits valides peut donc se terminer même lorsque sa durée totale dépasse le délai initial. Les sondages, les messages d’attente et une commande non confirmée ne renouvellent pas ce délai. L’observateur d’inactivité garde sa valeur configurée.

Une interruption conserve le nombre exact de retraits confirmés et le journal durable. La reprise vérifie l’état persisté avant de retirer les seules colonnes temporaires restantes. Les changements concurrents de l’utilisateur sont préservés ; aucune commande incertaine n’est répétée.

Le mode « Vue actuelle » permet toujours de collecter sans ajouter de colonnes. Le mode de supervision ne demande que les champs manquants reconnus et utilisés par la configuration.

## Résultat utilisable et automatisation

Une évolution de liste pendant la préparation reste enregistrée. Elle ne déclassifie plus un CSV dont les contrôles réalisés après préparation confirment indépendamment la réception complète, les deux échantillons, le total et la stabilité du périmètre. Une preuve manquante ou une divergence conserve le statut à vérifier. Un avertissement de restauration ne réécrit pas les cellules ni les observations de réception.

Le journal et les notices distinguent réception des lignes, stabilité du périmètre, couverture vérifiée et restauration de la vue GLPI. Les opérations locales restent accessibles quand la restauration est en attente.

Une collecte déjà active, y compris son nettoyage, refuse un second départ immédiat. L’échéance récurrente repart de la fin du cycle, quelle que soit son issue. Les collectes planifiées et au démarrage sont suspendues tant que la restauration est en attente ; une reprise manuelle peut effectuer la récupération.

Un formulaire de connexion sans modification locale suit les nouveaux relevés. Un formulaire déjà modifié conserve la protection de révision et ne rejoue aucune sauvegarde.

## Repérage des villes

Le journal indique les nombres de références et de variantes, ainsi que la disponibilité des rôles identifiant, titre et lieu : colonne reconnue, absente, ambiguë ou non configurée, valeurs présentes et contexte des règles disponible. Il ne contient ni noms de colonnes privés, ni titres de tickets, ni valeurs du référentiel.

Les tests couvrent treize références synthétiques avec accents, apostrophes, tirets, casse et alias, ainsi que les sources vides et les correspondances multiples. Les références privées de l’organisation n’ont pas été consultées. Aucune ville par défaut n’est attribuée ; les options permettent de choisir explicitement la colonne de lieu ou de titre correcte.

## Validation et livraison

La publication exige une qualification Windows et PostgreSQL complète sur le commit exact et l’inspection du ZIP original. Les essais navigateur utilisent des pages synthétiques GLPI 9.5 et modernes, dans les éditions Standard et Admin. La page Releases et le reçu de qualification font foi pour le statut de publication. Le portail privé et son SSO n’ont pas été ouverts pendant ces essais.

Pour mettre à jour, fermer Pilot puis extraire le pack publié dans un nouveau dossier local en conservant ensemble les deux exécutables et `wad/`. Les données et réglages du profil utilisateur sont conservés. La release 4.0.14 reste inchangée.
