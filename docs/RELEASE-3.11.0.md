# GLPI Pilot 3.11.0 : composition libre et configuration locale

## Des cartes indépendantes dans Pilotage

Dans **Personnaliser la vue**, chaque carte possède une poignée **Déplacer**, un choix **Petit / Moyen / Grand** et une poignée de redimensionnement. Le déplacement choisit une position dans la grille à douze colonnes, y compris dans une autre section. Les espaces libres intentionnels sont conservés. Les flèches du clavier permettent également de régler la position et les dimensions ; Échap annule le geste en cours.

Un graphique haut ne donne plus sa hauteur aux indicateurs voisins. Les trois formats proposent respectivement 4 colonnes et 220 pixels, 6 colonnes et 360 pixels, ou 12 colonnes et 480 pixels. Le redimensionnement permet de conserver une taille personnalisée. La hauteur adaptée au contenu reste bornée par la hauteur choisie ; les listes longues se parcourent dans leur carte.

Une carte déposée déplace vers le bas les cartes non verrouillées qui entrent en collision. Les cartes verrouillées refusent un dépôt qui les recouvre. Les positions, dimensions, sections, filtres et paramètres continuent à utiliser la sauvegarde C#, les opérations Annuler/Rétablir et les compositions existantes.

Les dispositions anciennes restent importables. Sans coordonnées enregistrées, le logiciel leur donne une disposition automatique ; l'édition enregistre les positions explicites. Une fenêtre plus étroite adapte la lecture sans réécrire la disposition enregistrée. En édition, la grille conserve sa largeur logique avec défilement horizontal lorsque nécessaire.

## Configuration par défaut, sites compris

**Réglages > Configuration complète et déploiement > Définir comme configuration par défaut** écrit le fichier `wad/GLPI-Pilot.deployment.json` dans le dossier du pack extrait. Il contient les réglages enregistrés de l'application, les compositions et leurs coordonnées, les tailles, les sélections et seuils, les présentations préparées ainsi que le référentiel des communes, sites et bâtiments avec leurs alias et champs personnalisés.

Les formulaires encore ouverts doivent être enregistrés avant la génération. Les tickets, bases, sessions SSO, cookies et secrets ne sont pas des paramètres transportables et ne sont pas inclus. Les protections Standard/Admin, les sauvegardes avant remplacement et l'isolation de la démonstration restent actives.

**Ce JSON est local et peut contenir des informations propres à votre organisation. Il ne doit pas être ajouté au dépôt ni aux packs publics.** Aucun catalogue Sites privé, `sites.csv` ou complément Sites n'est réintroduit dans cette livraison. L'import de fichiers privés reste disponible.

## Présentation et compatibilité

L'éditeur de diapos conserve son placement libre et ses exports HTML/PDF. Les présentations préparées sont indépendantes du placement des cartes de l'écran de pilotage. La collecte navigateur, les imports CSV/Excel, le cache hors connexion, la démonstration fictive, les mises à jour GitHub ou réseau et les patchs de sites gardent leurs parcours existants.

## Qualification et distribution

Cette version ajoute des contrôles de géométrie, collisions, tailles indépendantes, navigation clavier, gestes réels de déplacement et redimensionnement, annulation, restauration hors connexion et aller-retour de configuration avec le véritable moteur C#. Les données de qualification sont fictives.

La publication des packs PROD et DEV reste conditionnée à la réussite du workflow Windows/PostgreSQL, du démarrage natif, des exports, de la reconstruction DEV et des mises à jour/retours arrière. Le statut des exécutions et les journaux constituent la preuve de validation ; cette notice ne remplace pas leurs résultats.

Les archives portent leur propre numéro **3.11.0** et contiennent `GLPI-Pilot.exe`, `GLPI-Pilot-Admin.exe` et `wad/`. Les anciennes archives ne sont pas remplacées sous leur numéro.
