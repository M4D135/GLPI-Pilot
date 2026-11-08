# GLPI Pilot 3.11.1 — Windows x64

## Correctif Live / cache / CSV

- Sélecteur permanent des sources GLPI Live, cache local, CSV/Excel et démonstration.
- Depuis le dernier relevé conservé : « Revenir au GLPI Live » relance une collecte explicite ; le relevé existant reste consultable si la collecte échoue.
- Le bouton « Actualiser » des espaces de collecte peut démarrer une nouvelle collecte GLPI. Les cartes, le planning et les interventions gardent leur actualisation locale et leurs filtres sans relance réseau implicite.
- L'état du cache est distingué de l'état d'authentification SSO. Les erreurs de collecte sont affichées.
- Les contrôles WebView vérifient la présence du sélecteur et la stabilité du bouton « Actualiser cet espace » après restauration du cache.
- Tous les apports de 3.11.0 (widgets indépendants, présentation, configuration locale dans `wad/GLPI-Pilot.deployment.json`) sont conservés.

## Qualification et distribution

La publication exige la réussite de la prévalidation ciblée Standard/Admin puis de la validation complète Windows et PostgreSQL pour **le commit exact** des ZIP PROD/DEV. Le script de publication vérifie les manifestes internes et les SHA-256 avant de publier `v3.11.1`. Aucun résultat de tests sur le vrai SSO de l'organisation n'est revendiqué. Les catalogues de sites et JSON privés de déploiement restent exclus des packs publics.
