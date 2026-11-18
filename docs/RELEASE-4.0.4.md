# GLPI Pilot 4.0.4

## Collecte après préparation des colonnes

La version 4.0.3 pouvait interrompre la collecte avec « L’option de colonne n’est plus proposée par GLPI » en laissant ouverte la fenêtre de configuration des colonnes. Ce message regroupait une option réellement absente et des repères HTML renouvelés pour le menu ou le bouton Ajouter.

La préparation réobserve désormais les commandes natives de la même vue personnelle et vérifie la valeur choisie avant le clic. Les identifiants temporaires du menu et du bouton peuvent changer sans être interprétés comme une disparition de colonne.

Si une option devient indisponible avant son ajout, la préparation s’arrête, ferme la fenêtre native et attend le renouvellement du tableau. Après vérification de la recherche et des colonnes existantes, la collecte reprend sur les colonnes effectivement affichées. Un avertissement conserve la limite rencontrée et le nombre d’ajouts confirmés ; cette reprise ne déclare pas que toutes les colonnes demandées ont été ajoutées.

Un changement de recherche, d’origine ou d’identité, une annulation ou un ajout dont l’enregistrement n’est pas confirmé restent des motifs d’arrêt. Le choix enregistré entre Vue actuelle, Colonnes de supervision et Toutes les colonnes proposées par GLPI est conservé.

## Vérification

Les régressions couvrent les commandes renouvelées, la valeur sélectionnée, la disparition d’une option, le rechargement différé du tableau et les limites de la reprise. Les scénarios ciblés de colonnes sont aussi exécutés au début de la qualification Windows. Le pack publié est celui de la qualification complète, avec contrôle de l’archive originale et du téléchargement public.

## Mise à jour et dépannage

Fermer GLPI Pilot puis remplacer les exécutables et le dossier `wad/` avec ceux du pack 4.0.4. Les données et réglages existants sont conservés.

Pour une installation 4.0.3 encore bloquée : fermer la fenêtre de configuration des colonnes dans GLPI, sélectionner **Données → Colonnes avant la collecte → Vue actuelle**, puis relancer la collecte. Ce mode ignore la préparation facultative des colonnes.
