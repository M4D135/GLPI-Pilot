# GLPI Pilot 4.0.3

## Supervision adaptée au relevé

La supervision masquait insuffisamment les widgets indisponibles : les tableaux natifs forçaient leur affichage, même lorsque les colonnes requises ne figuraient pas dans la vue GLPI récupérée.

- Les tableaux natifs masquent automatiquement les widgets sans données calculables.
- Les dispositions natives enregistrées par les anciennes versions adoptent ce comportement tout en conservant leurs widgets, filtres, positions et réglages.
- Les cartes restent enregistrées et réapparaissent dès qu'un relevé permet leur calcul.
- Une valeur réellement calculée à zéro reste visible.
- Le mode de personnalisation donne accès aux cartes indisponibles. Un choix explicite d'affichage reste mémorisé.
- Les tableaux personnalisés conservent leur préférence existante.

## Colonnes GLPI avant collecte

Dans **Données → Collecte → Colonnes avant la collecte**, trois modes sont proposés. Le mode **Colonnes de supervision** est sélectionné par défaut :

| Mode | Effet |
| --- | --- |
| Vue actuelle | Utilise les colonnes déjà affichées dans GLPI. |
| Colonnes de supervision | Complète les champs métier utilisés par les indicateurs. |
| Toutes les colonnes proposées par GLPI | Ajoute chaque colonne disponible dans la configuration personnelle. |

La préparation conserve les colonnes existantes et leur ordre, ajoute seulement les manquantes et utilise les commandes natives GLPI dans la session connectée. GLPI mémorise ces colonnes dans la vue personnelle. Les critères de recherche sont contrôlés après la préparation, puis le tableau et l'échantillon de collecte sont lus à nouveau.

Si le droit de configuration personnelle ou la structure native reconnue n'est pas disponible, la collecte utilise la vue actuelle et indique la limite rencontrée. Les colonnes absentes ne sont pas inventées. Le mode toutes colonnes peut être plus long, car GLPI enregistre chaque ajout séparément ; la progression et l'annulation restent disponibles.

## Vérification et disponibilité

Les contrôles utilisent des données fictives pour le masquage, la réapparition des widgets, la migration des dispositions et les commandes natives de colonnes. La qualification Windows et PostgreSQL complète et l'inspection du ZIP original sont requises avant publication. La page Releases fait foi pour la disponibilité du pack.

## Mise à jour

Pack Windows x64 : `GLPI-Pilot.exe`, `GLPI-Pilot-Admin.exe` et dossier `wad/`. Les réglages et les données existantes sont conservés. Le runtime WebView2 de Windows ou du Socle reste utilisé.
