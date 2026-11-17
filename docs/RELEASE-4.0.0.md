# GLPI Pilot 4.0.0

## Distribution Windows

Pack PROD-LIGHT : `GLPI-Pilot.exe`, `GLPI-Pilot-Admin.exe` et dossier `wad/`. Le runtime WebView2 installé sur Windows est utilisé ; le pack n'embarque pas de runtime WebView2. Aucun environnement de développement ni compilation locale n'est nécessaire.

La publication est conditionnée à une qualification Windows et PostgreSQL complète réussie sur le commit exact et à la vérification de l'archive distribuée. Les contrôles automatisés utilisent des données fictives et ne certifient pas le SSO du portail professionnel. Une vérification sur un poste autorisé reste nécessaire avant un déploiement généralisé.

## Interface

- Présentation inspirée de Domenia v2, thèmes clair/sombre, contraste renforcé et réduction des animations.
- Libellés fonctionnels et neutres, sans slogans, salutations ni vouvoiement dans les textes de l'application. Les contenus des tickets et les notes saisis ne sont pas réécrits.
- Aperçus météo des Réglages et de la fenêtre Animations corrigés : couleurs de repli définies, nuages lisibles en mode sombre, adaptations conservées pour le mode clair et les contrastes renforcés.
- Disposition et dimensionnement des widgets, filtres métier et préférences conservés. Aucun chiffre fictif n'est ajouté à une source de production.

## Carte et recherche

- Rues détaillées OpenStreetMap activées par défaut au lancement. Une ancienne préférence hors ligne ne désactive plus les rues au lancement suivant ; cadrage, zoom et filtres sont conservés.
- Fond local de secours en cas d'indisponibilité réseau. Les rues nécessitent l'accès réseau au fournisseur cartographique.
- Sélection de plusieurs villes ou sites, inclusion des sites rattachés à une ville et déduplication des tickets.
- Un seul tableau de référence : « Voir les tickets » ouvre la recherche existante sur les lignes exactes sélectionnées, avec des filtres géographiques visibles. Les tickets sans coordonnées restent consultables.
- Description de la demande dans la liste et la fiche ; texte complet disponible dans l'inspecteur. Une description absente reste indiquée comme absente.
- Recherche, carte, planning, tournées et préparation du matériel partagent leur contexte.

## Fiches tickets

- Observation locale affichée immédiatement, puis lecture GLPI après 500 ms si le ticket reste ouvert. Aucune lecture au simple survol.
- Annulation lors d'un changement de ticket ; cache récent de 30 secondes et actualisation manuelle immédiate.
- Indicateur K2000 discret uniquement pendant la lecture effective. Lecture suspendue pendant les outils de rédaction.
- Navigation précédent/suivant au sein des résultats filtrés, y compris entre les pages, avec boutons latéraux de souris et Alt+flèches. Les saisies et boîtes de dialogue sont protégées.
- Retour à la liste conservant la recherche, les filtres, le tri, la sélection, le défilement et le focus. Le ticket courant reste repérable.
- Carte secondaire repliée par défaut et chargée à la demande, sans recalcul caché à chaque fiche.

## Compatibilité et vérifications

- Import des configurations 3.15.1 et 4.0 corrigé ; formats futurs non pris en charge toujours refusés.
- Qualification des éditions Standard et Admin, des échanges C#/WebView, de SQLite, de PostgreSQL 17, du démarrage natif, de la mise à jour et du retour arrière.
- Tests de parcours exécutés avec la politique CSP de production et des données fictives. Aucun contrôle synthétique ne vaut validation du SSO privé.
- Les captures sans accès réseau montrent le fond cartographique local de secours, pas les rues détaillées.

## Installation

Extraire l'archive complète dans un dossier local et conserver les deux exécutables avec `wad/`. Si nécessaire, utiliser `wad/support/DEBLOQUER_TOUT.cmd`. Lancer `GLPI-Pilot.exe` pour l'utilisation courante ; réserver `GLPI-Pilot-Admin.exe` aux fonctions d'administration.

Pour une mise à jour, conserver les réglages existants lors de l'éventuelle proposition d'import de configuration. Une sauvegarde des données et de la configuration précède toute diffusion sur plusieurs postes.
