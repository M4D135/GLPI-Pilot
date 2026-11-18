# GLPI Pilot 4.0.9

## Collecte ciblée à partir du relevé GLPI

Le panneau de collecte permet de choisir une date de début d’historique et une date de fin, les numéros de ticket, le sujet, les groupes, les utilisateurs et les autres colonnes présentes dans le relevé chargé. Les équipes configurées sont traduites en groupes. Les critères se combinent et les valeurs rares peuvent être recherchées dans l’inventaire local.

Un aperçu explicite affiche la sélection avant toute acquisition de contenu. Un changement de filtre, de relevé ou de source invalide cet aperçu. Une sélection vide ou une colonne absente ne déclenche jamais une collecte globale. Le parcours de toute la liste reste accessible par un choix avancé explicite.

Le filtre de sujet proche utilise des mots significatifs du titre, avec un seuil de recouvrement. Il ne prétend pas reconnaître des synonymes ou réaliser une recherche sémantique.

## Vérifications limitées aux tickets sélectionnés

Le navigateur observe le champ Identifiant et son opérateur dans le formulaire natif de GLPI, puis vérifie les références sélectionnées par petits lots. Le numéro technique du champ n’est pas supposé. Les critères effectivement renvoyés, les identités des tickets et le périmètre de session sont contrôlés. Le filtre précédent est restauré selon les règles existantes de session et d’annulation.

Les vérifications initiale et finale évitent le parcours paginé de la liste globale. Seules les fiches nécessitant une acquisition sont lues. Un contenu complet et confirmé correspondant au même jeton fiable de modification est réutilisé, même au-delà de l’ancien délai de cache. Les protections sur la précision des dates, les conflits, les lecteurs antérieurs et les contenus non confirmés restent appliquées.

Le passage traite au maximum 25 références à la fois : vérification, lecture, confirmation et enregistrement du point de reprise. Une interruption conserve les lots terminés et les contenus du lot en cours. La reprise évite de recommencer l’indexation de toute la sélection avant d’enregistrer une fiche supplémentaire. Un changement de profil ou d’entités interrompt le passage avant la lecture des fiches du nouveau lot.

## Couverture et reprise durables

La couverture est calculée sur les identités distinctes du périmètre choisi. Les catégories distinguent les contenus complets, absents, modifiés, partiels et à confirmer. Elle reste séparée de l’avancement du passage et du compteur de requêtes. La date de l’inventaire et celle de la vérification GLPI sont distinctes ; un inventaire partiel ne produit aucune promesse de couverture globale.

La liste sélectionnée, les filtres et leur provenance sont enregistrés avec le passage. La reprise conserve cette liste après un arrêt ou un redémarrage. Les preuves de modification peuvent être actualisées sans modifier sa composition. Une reprise des erreurs réduit ses cibles et préserve le périmètre original de la programmation.

Les facettes sont bornées et recherchables. Les plans conservent les métadonnées nécessaires à la sélection, sans dupliquer toutes les colonnes d’un inventaire volumineux. Le calcul de couverture interroge des métadonnées SQL et ne charge pas les corps complets des tickets dans l’application.

## Utilisation et vérifications

Le [guide de collecte ciblée](https://github.com/M4D135/GLPI-Pilot/blob/v4.0.9/docs/Collecte-ciblee.md), inclus dans le pack, décrit le choix des dates, les filtres, le taux, la reprise et la programmation. Pour inclure les tickets apparus depuis la sélection initiale, actualiser le relevé puis prévisualiser un nouveau périmètre.

Les contrôles couvrent les filtres combinés, les dates, les identités ambiguës, les groupes aux noms proches, les plans figés, les preuves de version, l’isolation des sources et les inventaires de 180 000 tickets. Les parcours de navigateur et d’interface utilisent un portail synthétique dans la qualification Windows ; le portail privé de l’organisation n’est pas testé directement. La publication exige le succès de la qualification complète sur le commit exact et l’inspection de son archive originale.

Fermer GLPI Pilot, puis remplacer les deux exécutables et le dossier `wad/` par ceux du pack 4.0.9. Conserver les données et réglages existants. Les contenus déjà recueillis, le partage et les vues SQL du corpus restent conservés.
