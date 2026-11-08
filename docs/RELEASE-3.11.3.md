# GLPI Pilot 3.11.3 : choisir les groupes directement dans les cartes

## Configuration des widgets du pilotage

Le panneau « Groupes et filtres de cette carte » se trouve au début de « Configurer le widget » et s'ouvre directement lorsqu'un filtre de groupes existe. Les filtres distinguent « Groupes inclus dans cette carte » et « Groupes exclus de cette carte », avec une action lisible plutôt qu'une case comprimée par la grille.

« Choisir les groupes » propose la recherche, les cases à cocher, la sélection multiple et le retrait individuel. Les noms exacts, espaces et accents sont conservés. Les correspondances enregistrées mais absentes du relevé restent visibles et sélectionnées. La recherche tolérante ne modifie pas l'identité d'un groupe. La saisie manuelle, le type et la source techniques restent disponibles dans les réglages avancés.

Les groupes d'une condition sont combinés par OU ; les conditions sont combinées par ET. Les exclusions existantes des cartes météo d'équipe restent en place et leur rôle dans la priorité des équipes est expliqué. Rien n'est enregistré avant « Enregistrer » dans le widget. Le correctif ne modifie pas le moteur de calcul, les seuils, l'ordre général des équipes ni la géométrie des cartes.

La mise en page sépare les actions, les champs et les groupes. Les noms longs peuvent revenir à la ligne ; les cases du sélecteur conservent une largeur fixe. Les erreurs de chargement restent visibles et permettent de réessayer sans perdre les filtres. Fermer l'éditeur ou changer de page ferme aussi ses sélecteurs.

## Validation avant push

Syntaxe des deux modules JavaScript contrôlée. 21 contrôles ciblés ont réussi dans Chromium local : inclusions et exclusions conservées, annulation, espaces et accents, groupes absents, limite de sélection, sauvegarde des filtres sans perte des seuils ni de position, dates et sous-catégories, absence de débordement à 1280 et 480 pixels, erreur/reprise et fermeture.

Ces contrôles utilisent une interface isolée avec des doublures des helpers d'interface et du moteur C#, et des groupes fictifs. Ils ne constituent pas une validation complète Windows/WebView2, du SSO d'entreprise ni de la publication. Les workflows existants ne sont pas modifiés ; les nouveaux packs restent soumis à leur résultat. Aucune donnée privée de déploiement n'est publiée.
