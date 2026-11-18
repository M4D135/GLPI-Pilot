# GLPI Pilot 4.0.1

## Supervision

Cette version corrige des cas où les widgets indiquaient « Donnée indisponible » malgré la présence des colonnes nécessaires dans le relevé.

- Reconnaissance des libellés courts d’export : Groupe, Ouverture, Dernière modif, rédacteurs de suivis et de tâches, TTR/Progression et compteurs déclarés.
- Lorsqu’une correspondance configurée désigne un libellé connu devenu absent, récupération de son équivalent pour le même champ uniquement si une seule colonne convient. Le diagnostic indique la correspondance récupérée. Une correspondance explicite présente reste prioritaire ; un nom personnalisé absent et des colonnes ambiguës ne sont pas remplacés arbitrairement.
- Conservation des dictionnaires de correction de valeurs liés à l’ancien libellé reconnu, sans réécriture des données ni des réglages.
- Le classement « Les dix plus anciens du périmètre » fonctionne avec les statuts et les dates d’ouverture, même sans colonne Groupe. Le classement par groupe conserve son exigence d’un groupe reconnu. Les détails et les exports respectent cette distinction.
- Suppression de la note automatique « Équipes du relevé » sans contenu dans la supervision par défaut. Les notes et tableaux personnalisés restent conservés.
- Historique : distinction entre absence d’observations archivées, observations incompatibles et première observation disponible. Les explications sont visibles directement. Une courbe d’évolution nécessite plusieurs observations comparables ; aucun historique n’est déduit artificiellement des dates des tickets.

## Création des tournées

Deux actions distinctes sont proposées depuis les tournées liées à une recherche :

- **Créer une tournée depuis la recherche** utilise les résultats de la recherche courante, ou les tickets explicitement sélectionnés. Après saisie du nom et de la date, le brouillon s’ouvre directement dans le planning. Le traitement porte sur la recherche complète, indépendamment de la pagination, et conserve le panier existant.
- **Créer une tournée depuis la carte** ouvre la carte avec le même contexte de recherche pour choisir les tickets géographiquement et préparer les étapes.

La création refuse une recherche périmée ou vide et demande confirmation avant de remplacer un brouillon non enregistré.

## Lecture des tickets

Une reprise automatique liée au focus ne relance plus une lecture déjà en cours sur le même ticket. Cela évite une deuxième actualisation juste après l’affichage d’une première réponse réussie. L’affichage immédiat du cache, l’actualisation manuelle, les annulations et la navigation entre tickets restent disponibles.

Une petite icône de base de données indique l’état du cache dans la recherche et le détail. Rouge : aucune copie disponible vérifiée. Vert : copie récente dont le repère correspond au relevé. Orange : copie ancienne ou version à vérifier. Gris : état indéterminé. Une infobulle donne le motif ; le détail affiche la dernière lecture du cache et la dernière modification GLPI connues.

La lecture automatique compare le repère de modification du ticket dans le relevé à celui de la copie locale. Une version fiable inchangée est réutilisée, même si sa copie a vieilli ; une modification détectée déclenche la relecture. Sans repère fiable, la version reste à vérifier et seule une lecture récente permet une réutilisation temporaire. La comparaison tient compte de l’installation GLPI et de l’identité du ticket.

L’âge visuel du cache est de sept jours ; il ne provoque pas de relecture lorsque la version est inchangée. Sans repère comparable, la réutilisation temporaire est limitée à cinq minutes. Les copies disponibles en mémoire, dans la bibliothèque ou conservées hors connexion sont réutilisées ; cette correction ne crée pas un second stockage complet des fiches. La lecture d’un onglet ne valide pas artificiellement les autres rubriques de la fiche.

## Vérifications

Les contrôles de non-régression vérifient les valeurs et les lignes sources des widgets groupes/statuts, stock par groupe, priorités, ancienneté et participation avec des libellés courts et des correspondances conservées d’un autre export. Ils couvrent également les ambiguïtés, les correspondances personnalisées et les cas d’historique incomplet.

Les contrôles des tournées vérifient les identifiants exacts issus de la recherche complète, la sélection explicite, les filtres combinés, la conservation des paniers et du brouillon en cas de refus, ainsi que la limite de 2 000 tickets. Les contrôles de lecture reproduisent le double déclenchement pendant l’application d’une réponse réussie.

Contrôles locaux du 18 novembre 2026 :

- `DashboardChecks` : 164/164, après reproduction des échecs de reconnaissance avec le code antérieur.
- `AnalyticsBridgeChecks --supervision-only` : 27/27, avec import CSV, filtres, détails, restauration du cache et exports.
- Suite complète `AnalyticsBridgeChecks` hors navigateur : 373/373.
- `AnalyticsChecks` : 39/39.
- `FoundationChecks` : 38/38 ; la compatibilité est vérifiée avec la version courante et les versions futures sont calculées, sans conserver 4.0.1 comme ancien cas de rejet.
- `TerrainBridgeChecks` : 287/287, dont 41 contrôles de création depuis la recherche.
- Ordonnanceur de lecture automatique : 15/15 contrôles Node, après reproduction des deux relances indues ; indicateurs et dates du cache : 7/7.
- Cache et pont applicatif : 10/10 scénarios ciblés finaux ; la suite complète `RuntimeSessionChecks` a passé ses 160 contrôles avant les deux derniers cas ciblés. Le scénario avec navigateur réel est intégré à la qualification Windows.
- Contrôles des outils de publication `ReleasePackChecks` : 42/42.
- Compilation croisée Windows Standard et Admin : aucun avertissement, aucune erreur.
- Syntaxe JavaScript et contrôle du diff vérifiés. Les nouveaux contrôles visuels sont intégrés à la qualification Windows ; Chromium local ne peut pas démarrer dans l’environnement d’exécution utilisé.

La qualification Windows et PostgreSQL, le démarrage du pack et la vérification de ses empreintes restent nécessaires pour publier le pack PROD-LIGHT. La page Releases indique les versions effectivement disponibles. Les contrôles automatiques utilisent des relevés synthétiques ; le fichier professionnel signalé n’a pas été fourni avec le rapport de régression.

## Mise à jour

Pack Windows x64 : `GLPI-Pilot.exe`, `GLPI-Pilot-Admin.exe` et dossier `wad/`. Les préférences, correspondances, dispositions de widgets et données existantes sont conservées. Le runtime WebView2 de Windows ou du Socle reste utilisé.
