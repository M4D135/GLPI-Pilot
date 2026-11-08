# GLPI Pilot 3.10.2 — Actualisation et aperçus du référentiel

## Actualiser la vue affichée

Dans **Interventions**, le bouton **Actualiser** du bandeau recharge le planning, la carte ou les tournées en cours. Il conserve l’onglet, la date et les filtres affichés. Une ouverture précédente de **Sites et détection** ne rouvre plus le **Référentiel des lieux** après sa fermeture. Un aller-retour entre Carte et Planning ne rejoue pas non plus cette ancienne demande.

Sur la carte des tickets, l’actualisation reprend le type, les statuts, les équipes et les techniciens choisis, la recherche, le lieu sélectionné et le cadrage. Les tickets cochés restent sélectionnés lorsque le relevé et le périmètre sont toujours compatibles. Une révision différente invalide les anciennes sélections pour ne pas appliquer une action aux mauvaises lignes.

Une saisie d’intervention reste ouverte pendant le rechargement. Les champs de tournée non appliqués conservent leur confirmation avant rechargement ; annuler garde la saisie. Les demandes simultanées sont regroupées pour la page courante, et une lecture devenue étrangère à l’espace affiché ne doit pas rétablir l’ancien écran.

**Actualiser** utilise les données déjà ouvertes, y compris le cache hors connexion. Une nouvelle collecte GLPI ou un nouvel import du fichier des tickets reste une action explicite. Les modifications d’une simulation restent dans son espace isolé.

## Simuler et enregistrer les sites avec les anciens profils

L’erreur **« Cet aperçu est périmé. Relancez la simulation sur le relevé actuel. »** pouvait apparaître dès la simulation d’un référentiel importé, alors que rien n’avait changé. Deux formats anciens la provoquaient : une correction de localisation sans date d’enregistrement, et une observation sans disposition de dashboard.

Une ancienne date absente reste désormais **non renseignée** ; la lecture ne fabrique plus une nouvelle date. Les corrections nouvelles conservent leur véritable date d’enregistrement. Le contrôle de l’aperçu utilise les correspondances de colonnes et les corrections qui servent réellement au calcul, indépendamment des identifiants de présentation.

Les protections contre les changements réels restent actives : nouveau relevé, brouillon modifié, changement du référentiel enregistré, des correspondances ou des corrections. Le contexte est fixé avant le calcul ; une modification intervenue pendant celui-ci rend l’aperçu caduc. Un refus conserve le brouillon, les réglages et le relevé.

L’importateur, la fusion conservatrice, les corrections manuelles, le cache et les archives gardent leurs règles. Enregistrer un référentiel de travail pendant la consultation d’un relevé conservé ne réinterprète pas automatiquement cette observation.

## Référentiels propres à une organisation

Le catalogue de sites et ses sources ont été retirés de la branche principale. La publication automatique du complément Sites est arrêtée. Les pièces jointes `sites.csv`, `GLPI-Pilot-Sites.zip` et `SITES-SHA256SUMS.txt` ont été retirées des anciennes releases ; les autres pièces jointes sont conservées.

Les notices et exemples de la nouvelle version sont génériques. L’import d’un CSV privé, le fichier local facultatif `wad/sites.csv`, les patchs de sites et l’export des réglages restent disponibles. Les données de sites déjà présentes sur le poste ne sont pas supprimées par cette mise à jour.

Ce retrait ne réécrit pas l’historique Git : les anciens commits et tags, ainsi que les notices déjà incluses dans d’anciens packs, peuvent conserver des traces des versions précédentes. Les anciens exécutables ne sont pas remplacés sous leur numéro.

## Livraison et validation

Les packs **PROD** et **DEV** possèdent leur propre numéro **3.10.2**, avec `GLPI-Pilot.exe`, `GLPI-Pilot-Admin.exe` et `wad/`. Cette release fournit les deux packs Windows et `SHA256SUMS.txt`, sans complément de données Sites.

Les contrôles couvrent le parcours réel Données → référentiel → fermeture → Actualiser, les filtres et sélections, le cache, les brouillons, la simulation et les aperçus issus d’anciens profils. Les parcours d’interface utilisent le véritable moteur C# sur des données fictives. Les suites Windows et PostgreSQL, les démarrages natifs, le PDF 16:9, la reconstruction DEV, les mises à jour et les empreintes des fichiers réellement publiés restent les conditions de livraison. Le commit dans `wad/version.json` et les journaux CI identifient la version qualifiée.
