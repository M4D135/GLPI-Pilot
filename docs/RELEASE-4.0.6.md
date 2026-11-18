# GLPI Pilot 4.0.6

## Vue personnelle des colonnes

GLPI peut remplacer les contrôles de la fenêtre des colonnes pendant son chargement. La collecte comparait encore leurs anciens identifiants HTML, ce qui pouvait provoquer le message « L’onglet personnel des colonnes a changé avant sa sélection » alors que la vue personnelle était accessible.

La préparation relit le rôle du contrôle dans la même fenêtre et accepte la vue personnelle si elle est déjà chargée. Si cette commande de navigation devient indisponible avant tout ajout, elle ferme la fenêtre native et relit la liste avant de continuer avec les colonnes disponibles. Les critères de recherche sont vérifiés avant la fermeture et après la reprise.

## Tableau remplacé avant l’export

Un rechargement natif peut remplacer l’identifiant du tableau alors que les résultats restent ceux de la même recherche. Un repère ancien ne doit pas interrompre à lui seul la collecte. Le tableau est retrouvé dans son contexte d’origine et sa structure est vérifiée avant l’identification de la commande CSV Toutes les pages.

Plusieurs tableaux correspondants, des critères modifiés ou un ajout dont l’enregistrement n’a pas été confirmé restent des motifs d’arrêt. La collecte ne choisit pas arbitrairement un tableau ni une commande d’export.

## Vérification et mise à jour

Les régressions Windows couvrent le remplacement des contrôles de la vue personnelle et des repères du tableau, ainsi que les refus en cas d’ambiguïté ou de changement de périmètre. La qualification complète et la vérification de l’archive originale précèdent la publication du même ZIP. Les fixtures utilisent des données fictives ; elles ne constituent pas une validation sur le portail privé de l’organisation.

Fermer GLPI Pilot puis remplacer les deux exécutables et le dossier `wad/` par ceux du pack 4.0.6. Conserver les données et réglages existants.
