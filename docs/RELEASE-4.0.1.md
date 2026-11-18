# GLPI Pilot 4.0.1

## Supervision

Cette version corrige des cas où les widgets indiquaient « Donnée indisponible » malgré la présence des colonnes nécessaires dans le relevé.

- Reconnaissance des libellés courts d’export : Groupe, Ouverture, Dernière modif, rédacteurs de suivis et de tâches, TTR/Progression et compteurs déclarés.
- Lorsqu’une correspondance configurée désigne un libellé connu devenu absent, récupération de son équivalent pour le même champ uniquement si une seule colonne convient. Le diagnostic indique la correspondance récupérée. Une correspondance explicite présente reste prioritaire ; un nom personnalisé absent et des colonnes ambiguës ne sont pas remplacés arbitrairement.
- Conservation des dictionnaires de correction de valeurs liés à l’ancien libellé reconnu, sans réécriture des données ni des réglages.
- Le classement « Les dix plus anciens du périmètre » fonctionne avec les statuts et les dates d’ouverture, même sans colonne Groupe. Le classement par groupe conserve son exigence d’un groupe reconnu. Les détails et les exports respectent cette distinction.
- Suppression de la note automatique « Équipes du relevé » sans contenu dans la supervision par défaut. Les notes et tableaux personnalisés restent conservés.
- Historique : distinction entre absence d’observations archivées, observations incompatibles et première observation disponible. Les explications sont visibles directement. Une courbe d’évolution nécessite plusieurs observations comparables ; aucun historique n’est déduit artificiellement des dates des tickets.

## Vérifications

Les contrôles de non-régression vérifient les valeurs et les lignes sources des widgets groupes/statuts, stock par groupe, priorités, ancienneté et participation avec des libellés courts et des correspondances conservées d’un autre export. Ils couvrent également les ambiguïtés, les correspondances personnalisées et les cas d’historique incomplet.

Contrôles locaux du 18 novembre 2026 :

- `DashboardChecks` : 164/164, après reproduction des échecs de reconnaissance avec le code antérieur.
- `AnalyticsBridgeChecks --supervision-only` : 27/27, avec import CSV, filtres, détails, restauration du cache et exports.
- Suite complète `AnalyticsBridgeChecks` hors navigateur : 373/373.
- `AnalyticsChecks` : 39/39.
- Contrôles des outils de publication `ReleasePackChecks` : 42/42.
- Compilation croisée Windows Standard et Admin : aucun avertissement, aucune erreur.
- Syntaxe JavaScript et contrôle du diff vérifiés. Les nouveaux contrôles visuels sont intégrés à la qualification Windows ; Chromium local ne peut pas démarrer dans l’environnement d’exécution utilisé.

La qualification Windows et PostgreSQL, le démarrage du pack et la vérification de ses empreintes restent nécessaires pour publier le pack PROD-LIGHT. La page Releases indique les versions effectivement disponibles. Les contrôles automatiques utilisent des relevés synthétiques ; le fichier professionnel signalé n’a pas été fourni avec le rapport de régression.

## Mise à jour

Pack Windows x64 : `GLPI-Pilot.exe`, `GLPI-Pilot-Admin.exe` et dossier `wad/`. Les préférences, correspondances, dispositions de widgets et données existantes sont conservées. Le runtime WebView2 de Windows ou du Socle reste utilisé.
