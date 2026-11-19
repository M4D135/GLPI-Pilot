# GLPI Pilot 4.0.14 — qualification et publication vérifiées

La [release v4.0.14](https://github.com/M4D135/GLPI-Pilot/releases/tag/v4.0.14) est publiée le **19 novembre 2026 à 17 h 15 (Europe/Paris)**. Elle corrige le blocage de la collecte ciblée à « Préparation du critère Identifiant ».

## Identité de la livraison

| Élément | Valeur vérifiée |
| --- | --- |
| Commit du code, du binaire et du tag `v4.0.14` | `4457ac5773ad0cec700883e11b4799edd1d44e01` |
| Qualification Windows et PostgreSQL | [Run 44556615470](https://github.com/M4D135/GLPI-Pilot/actions/runs/44556615470), tentative `1`, **16/16 jobs réussis** |
| Publication | [Run 44563776360](https://github.com/M4D135/GLPI-Pilot/actions/runs/44563776360), tentative `1`, [job 135699869696](https://github.com/M4D135/GLPI-Pilot/actions/runs/44563776360/job/135699869696) réussi |
| Artefact de qualification original | `14547026095` |
| ZIP public | `GLPI-Pilot_4.0.14_PROD-LIGHT_Windows-x64.zip`, fichier `768362021` |
| Fichier public d’empreintes | `SHA256SUMS.txt`, fichier `768362012` |
| Taille du ZIP | **138 691 673 octets** |
| SHA-256 du ZIP | `8698d3dcf74df4f1bb97439dd263a0376570491b07dac471429a96c3547a75cc` |

Téléchargement : [ZIP PROD-LIGHT Windows x64](https://github.com/M4D135/GLPI-Pilot/releases/download/v4.0.14/GLPI-Pilot_4.0.14_PROD-LIGHT_Windows-x64.zip).

Le run de publication a été créé à `2026-11-19T16:14:12Z` ; la release a été publiée à `2026-11-19T16:15:07Z`. Il était déjà actif lors de son contrôle dans cette reprise. Aucun second déclenchement n’a été effectué.

## Correctif qualifié

Pilot exigeait `equals` pour le critère Identifiant. Le champ numérique natif de GLPI peut proposer `contains` avec le libellé « est » / « is ». La collecte reconnaît maintenant cette variante sur le champ ID observé, utilise l’opérateur réellement proposé et vérifie chaque ligne du groupe OU dans les réponses. Les opérateurs textuels, doublons, ambiguïtés et changements de formulaire restent refusés. Les lots de 25 identifiants, les contrôles de périmètre et la restauration du filtre précédent sont conservés. Les [notes de version](RELEASE-4.0.14.md) détaillent le comportement et les sources GLPI.

## Résultats observés

Les journaux officiels du run de qualification établissent les résultats suivants ; aucun de ses 16 jobs n’est ignoré, annulé ou en échec.

| Contrôle | Résultat |
| --- | --- |
| Acquisition de bibliothèque | **46/46** |
| Configuration / règles pures de restauration des colonnes | 94/94 et 24/24 |
| Automatisation générale / colonnes / attente des exports natifs | 53/53, 72/72 et 11/11 |
| Lecture directe des fiches | 36/36 |
| Isolation et arrêt du moteur | 208/208 |
| Données et sources, Standard puis Admin | 55/55 dans chaque édition |
| Actualisation de l’espace, Standard puis Admin | 19/19 dans chaque édition |
| Structure de distribution hors ligne / hôtes Windows WebView réels | 14/14 et 72/72 |
| Mise à jour Windows et retour arrière | 4/4 |
| PostgreSQL 17 : bibliothèque / partage / corpus SQL / administration | 55/55, 44/44, 12/12 et 19/19 |
| Règles de publication et sélection des packs | 42 tests, résultat `OK` |

Les cinq nouveaux scénarios d’acquisition sont tous réussis : égalité numérique française avec Select2 et restauration, égalité anglaise avec 30 cibles en deux lots, refus des opérateurs non exacts, refus des doublons et contrôles ambigus, refus d’une dérive de l’opérateur sur toute la réponse ou une seule ligne OU. Le résultat final `46/46` est consigné à `2026-11-19T16:12:50Z` dans le journal C#/navigateur.

Les scénarios GLPI utilisent des pages et données synthétiques. Cette qualification ne constitue pas un essai dans le portail privé de l’organisation.

## Vérification du pack et de sa publication

L’inspection locale du ZIP original confirme la version, le commit source, la distribution PROD-LIGHT et les **372 fichiers** du manifeste interne. Aucun export de sources n’est recensé ; les deux exécutables Standard et Admin ont des empreintes distinctes. Cette inspection locale n’a pas exécuté les binaires ; leurs contrôles réels sont ceux de la CI Windows ci-dessus.

Les deux modes du sélecteur de release vérifiée ont réussi. Le workflow de publication confirme la taille, le SHA-256 et les 372 fichiers avant publication, puis après téléchargement public. Il vérifie la correspondance octet pour octet des fichiers publiés avec les originaux inspectés. Le ZIP de qualification a été promu sans reconstruction ni recompression.

La page de release a également été vérifiée visuellement ; une [capture de la publication publique](evidence/GLPI-Pilot-4.0.14-publication.jpg) accompagne ce reçu.

## Reproduction et reprise

Le tag `v4.0.14` reste attaché au commit qualifié `4457ac5773ad0cec700883e11b4799edd1d44e01`. Ce reçu et les mises à jour de suivi sont postérieurs à la construction : le pack conserve les documents de son commit source. Pour une nouvelle correction, repartir de l’état courant du dépôt sans reconstruire ni remplacer les fichiers de cette release.
