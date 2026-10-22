# GLPI Pilot 2.0.0 — Windows PROD + DEV

Cette version étend GLPI Pilot en espace de travail opérationnel tout en conservant l’accès par navigateur et SSO. Les packs sont publiés uniquement après les contrôles Windows du commit correspondant.

## Packs complets

- **PROD** : `GLPI-Pilot.exe` et `wad/`, prêts à utiliser sous Windows x64 avec Edge ou Chrome installé.
- **DEV** : le même EXE et `wad/`, avec les sources, les tests, les scripts, le workflow et la documentation dans `wad/sources/`.
- `SHA256SUMS.txt` permet de vérifier les deux archives. Chaque pack contient également les empreintes de ses fichiers et le commit source dans `wad/version.json`.

Extraire le ZIP entier, garder `wad` à côté de l’EXE et lancer **GLPI-Pilot.exe**. Les réglages existants restent dans les données locales du poste. Une sauvegarde des préférences précède la première ouverture de cette version.

## Principales nouveautés

### Pilotage et analyses

Dashboards global et personnel indépendants, sections repliables, édition groupée, favoris persistants, filtres communs et par widget, filtres croisés et comparaison A/B. Matrices colorées, barres empilées, histogrammes, Pareto, boîtes statistiques, quantiles et nuage âge/échéance complètent les cartes existantes. La météo de santé du relevé devient paramétrable. Export PNG et bilan détaillé CSV/Excel.

### Travail des techniciens

Compétences, territoires, relais, absences et capacités déclarées. Scénarios locaux de répartition expliqués et comparaison des charges. Espace **Ma journée** avec file ordonnée, notes, checklists, modèles, rappels et passation. Jusqu’à 12 onglets de tickets, chronologie du traitement, épingles, filtres et sections préférées. Cache daté facultatif et rapprochements avec les tickets ou solutions réellement lus.

### Requêtes, lieux et tournées

**80 modèles de requêtes**, expressions ET/OU/NON imbriquées, champs calculés bornés, paramètres, comparaisons entre champs, regroupements multiples et explication par ticket. Référentiel de villes/sites avec variantes, conditions, exclusions, preuves de détection et corrections locales. Tournées datées avec panier explicite, durées, fenêtres horaires, départ/retour, étapes verrouillées, scénarios, rapprochement après actualisation, briefing imprimable et export ICS.

### Conservation et organisation

Archives datées, protégées et comparables, quatre courbes historiques globales, contrôle d’intégrité, qualité des colonnes et des valeurs. Vues du relevé mémorisées, filtres et tris sur toutes les lignes avant l’aperçu limité. Import XLSX, alertes locales sur indicateurs/requêtes, sources multiples, campagnes séquentielles et programmation facultative pendant l’ouverture de l’application. Import de configuration avec aperçu, choix des sections et fusion ; réglages versionnés et récupération.

### Interface et moteur

Thèmes cohérents, calendriers intégrés aux palettes, accents, densité, contraste, animations réduites et suivi du thème Windows ; zoom 75–150 % et éditeurs intégrés. Instance unique rappelant la fenêtre existante et diagnostic de démarrage.

Prévol avant extraction, verrou du profil navigateur, contrôles de périmètre et de totaux, rapprochement d’identifiants entre liste et CSV. Pagination automatique uniquement si l’export natif est réellement indisponible. Un transfert corrompu ou ambigu n’est pas masqué par une autre méthode. Les résultats incomplets restent signalés, les fichiers reçus intacts et l’annulation respectée.

## Validation et limites

La chaîne exige les tests des moteurs, des scénarios navigateur synthétiques, le rendu WPF dans les deux thèmes, le démarrage réel de l’EXE extrait, le rappel par un second lancement et la persistance du zoom. Les journaux et captures sont joints au workflow du commit ; les ZIP sont vérifiés avant publication.

Le portail de l’entreprise et son SSO ne sont pas accessibles à ces tests. Une présentation GLPI personnalisée peut nécessiter un premier réglage des repères. Les notes, tournées et affectations simulées restent locales : elles ne modifient pas les tickets GLPI. Les données manquantes restent indisponibles. Les distances sont géographiques et les temps de trajet saisis, sans routage externe. Les courbes historiques personnelles, cartes intégrées, prévisions statistiques, synchronisation d’équipe, signature de l’EXE et mise à jour automatique ne sont pas incluses.

Consulter les guides détaillés dans `wad/docs` et le README pour les méthodes, les limites et les chemins des données.
