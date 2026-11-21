# GLPI Pilot 4.0.16 — qualification et publication

La [release v4.0.16](https://github.com/M4D135/GLPI-Pilot/releases/tag/v4.0.16) est publiée depuis le **21 novembre 2026 à 01 h 32 (Europe/Paris)**. Le [ZIP PROD-LIGHT Windows x64](https://github.com/M4D135/GLPI-Pilot/releases/download/v4.0.16/GLPI-Pilot_4.0.16_PROD-LIGHT_Windows-x64.zip) est l’original inspecté du **run 44838578203, tentative 1**, intégralement qualifié. Les deux fichiers publics ont été retéléchargés et comparés octet pour octet aux originaux ; le manifeste du ZIP public a été vérifié de nouveau.

## Identité exacte

| Élément | Valeur vérifiée |
| --- | --- |
| Commit qualifié | `a0676e4163f81b97e7ce737679737f468b93b3b1` |
| Arbre source | `87af945d25b652debaa0e8ad17df7d7868cb0bbf` |
| Qualification Windows et PostgreSQL | [Run 44838578203](https://github.com/M4D135/GLPI-Pilot/actions/runs/44838578203), tentative **1**, **16/16 jobs réussis**, aucun job ignoré, annulé ou en échec |
| Dates du run | Créé le **20 novembre 2026 à 23:41:02 UTC** ; conclusion `success` observée, dernière mise à jour le **21 novembre à 00:22:59 UTC** |
| Intégration | Commit qualifié présent sur `main` avant publication ; le push a déclenché le run `44843511145` le **21 novembre à 00:23:37 UTC**, sans refaire la qualification déjà acquise |
| Tag `v4.0.16` | `a0676e4163f81b97e7ce737679737f468b93b3b1`, identique au commit du binaire et au commit qualifié |
| Publication | [Run 44844431605](https://github.com/M4D135/GLPI-Pilot/actions/runs/44844431605), tentative **1**, [job 136660552108](https://github.com/M4D135/GLPI-Pilot/actions/runs/44844431605/job/136660552108) **réussi** |
| Release publique | **453326026**, `v4.0.16`, publiée le **21 novembre 2026 à 00:32:17 UTC**, soit **01 h 32 Europe/Paris** ; ni brouillon ni préversion |

Les métadonnées du run, l’inventaire de ses seize jobs et les reçus des journaux concordent sur ce commit et cette tentative. Le ZIP publié provient du **run 44838578203, tentative 1**. Les ajouts documentaires postérieurs ne changent ni le commit du binaire, ni son tag, ni l’archive publiée.

## Correctif et conservation des données

Le cache restauré permet de consulter les descriptions, suivis, tâches et autres rubriques déjà enregistrés, y compris les copies partielles ou non confirmées, avec leurs dates et leurs limites. Le ticket garde l’installation et le mapping du relevé d’origine. La consultation et **Vérifier les contenus conservés** restent locales et utilisent SQLite en lecture seule ; cet inventaire ne certifie pas l’état actuel de GLPI. **Consulter les contenus** ouvre les copies. **Compléter seulement les contenus absents** conserve ses cibles après pause ou redémarrage et préserve les corps déjà enregistrés. **Reprendre les erreurs** reste une action distincte pouvant relire des contenus.

En live automatique comme pendant les collectes globale et ciblée, une copie complète et confirmée dont le repère correspond au relevé GLPI courant n’est plus relue à cause de son âge. Les contrôles d’index, d’identité et de couverture restent actifs. Une fiche absente, modifiée, incomplète ou sans version comparable peut nécessiter une lecture ; la relecture forcée reste volontaire. Réutiliser un corps ne recrée ni sa date ni sa preuve ; les observations d’index et le journal peuvent être enregistrés. La compatibilité avec un ancien lecteur peut préserver une copie sans certifier sa fraîcheur.

La date du corps est distincte de celle d’un onglet lu ensuite. Un onglet récent ne rajeunit pas le corps, même si la version GLPI est inconnue. Les anciennes copies JSON ne reçoivent pas de date principale inventée. Le choix entre copie personnelle et bibliothèque utilise la véritable date du corps lorsqu’elle est connue et respecte les versions ainsi que leurs contextes de dates. Il ne réécrit ni la bibliothèque ni les observations personnelles ; les onglets d’une autre version restent dans leur copie d’origine. La réouverture après redémarrage est couverte avec une copie hors connexion **explicitement enregistrée**, sans promettre la persistance implicite de toute lecture live.

## Vérifications du run retenu

| Contrôle | Résultat observé dans le run 44838578203 |
| --- | --- |
| PostgreSQL : bibliothèque / partage / corpus SQL / administration | **56/56**, **44/44**, **12/12**, **19/19** ; [job 136642718668](https://github.com/M4D135/GLPI-Pilot/actions/runs/44838578203/job/136642718668) réussi |
| Verrou PostgreSQL du collecteur | **6/6** ; [job 136642718996](https://github.com/M4D135/GLPI-Pilot/actions/runs/44838578203/job/136642718996) réussi |
| Isolation du moteur, cache de ticket et restauration du cache de source | **225/225**, dont **15 cas de cache de source** ; [job 136642718858](https://github.com/M4D135/GLPI-Pilot/actions/runs/44838578203/job/136642718858) réussi |
| Ancien parcours de reprise Standard | **14/14** ; [job 136642718822](https://github.com/M4D135/GLPI-Pilot/actions/runs/44838578203/job/136642718822) réussi |
| Ancien parcours de reprise Admin | **14/14** ; [job 136642719102](https://github.com/M4D135/GLPI-Pilot/actions/runs/44838578203/job/136642719102) réussi |
| Parcours navigateur de collecte et pause/reprise | **17/17** ; [job 136642719429](https://github.com/M4D135/GLPI-Pilot/actions/runs/44838578203/job/136642719429) réussi |
| Acquisition de bibliothèque | **60/60**, même job de collecte |
| Moteur de collecte / contrôleur | **64/64** / **79/79**, même job ; suites partiellement communes |
| Interface de collecte / pilotage des tickets | **35/35** / **20/20**, même job |
| Bibliothèque locale | **45/45**, même job |
| Tests Node / tests du vérificateur de packs | **34 réussis, 0 échec** / **26 réussis**, même job |
| Préflight : colonnes / exports natifs | **75/75** / **11/11** ; [job 136642718627](https://github.com/M4D135/GLPI-Pilot/actions/runs/44838578203/job/136642718627) réussi, dont le scénario de requêtes de fond avec sa limite originale |
| Préflight : sources / actualisation du relevé | **60/60** / **19/19** dans chacune des éditions Standard et Admin, même job |
| C# : configuration / restauration pure / intégration | **96/96** / **27/27** / **65/65** ; [job 136642718566](https://github.com/M4D135/GLPI-Pilot/actions/runs/44838578203/job/136642718566) réussi |
| C# : automatisation / colonnes / exports / lecture de fiche | **63/63** / **75/75** / **11/11** / **36/36**, même job ; acquisition également **60/60**, sans addition aux suites précédentes |
| Interfaces WebView complètes et lecteur hors connexion | [Job 136642718583](https://github.com/M4D135/GLPI-Pilot/actions/runs/44838578203/job/136642718583) **réussi**, dont **17/17** sur le cache de source ; revue visuelle indépendante favorable, sans défaut bloquant observé |
| Transitions Standard / Admin, reconnexion SSO, Terrain et ergonomie | Jobs [136642718794](https://github.com/M4D135/GLPI-Pilot/actions/runs/44838578203/job/136642718794), [136642718992](https://github.com/M4D135/GLPI-Pilot/actions/runs/44838578203/job/136642718992), [136642719024](https://github.com/M4D135/GLPI-Pilot/actions/runs/44838578203/job/136642719024), [136642719082](https://github.com/M4D135/GLPI-Pilot/actions/runs/44838578203/job/136642719082) et [136642719122](https://github.com/M4D135/GLPI-Pilot/actions/runs/44838578203/job/136642719122) **réussis** |
| Structure / vrais EXE Windows / mise à jour et retour arrière | **14/14** / **72/72** / **4/4** ; [job 136642718657](https://github.com/M4D135/GLPI-Pilot/actions/runs/44838578203/job/136642718657) réussi ; WebView2 Evergreen **157.0.4341.46**, profil isolé |
| Qualification complète et sélection | **16/16 jobs réussis** ; les deux sélecteurs, original enregistré (`stored`) puis qualification complète, ont validé ce run, cette tentative et cet original |

Les reçus `GLPI_CI_EVIDENCE` récupérés portent le commit, le run et la tentative ci-dessus. Les scénarios de collecte confirment des copies complètes et confirmées âgées de **trente jours**, inchangées dans GLPI : en global comme en ciblé, y compris après pause et redémarrage, les métadonnées restent contrôlées avec **zéro requête de détail**. Le corps, sa date de lecture, son empreinte et sa preuve restent conservés. Les corps modifiés, partiels ou absents continuent d’être lus selon le parcours prévu. Les suites se recouvrent ; leurs comptes ne sont pas additionnés en un total de tests distincts.

Le moteur vérifie aussi la réutilisation d’une version identique, la relecture après changement du repère GLPI, le forçage manuel et les quatre choix de copie selon la véritable date du corps. Le scénario de redémarrage utilise une copie hors connexion explicitement enregistrée ; le scénario navigateur vérifie les décisions live dans une même session.

Les journaux `preflight-run-44838578203-job-136642718627.log` et `checks-run-44838578203-job-136642718566.log` ont été recueillis directement pour ce run. Le cas de requêtes de fond réussit dans les deux exécutions de la suite de colonnes. La revue visuelle s’appuie sur l’artefact **14681547297**, dont le SHA-256 et le contexte de construction ont été vérifiés : la capture `run-44838578203-source-cache-ticket-details.png` montre la fiche conservée hors connexion, « Version à confirmer », la date préservée et les commandes GLPI désactivées. Elle couvre le haut du lecteur ; la conservation du texte et l’absence de réseau sont établies par les **17/17** assertions du parcours, sans étendre la portée de la capture.

## Historique et suivi distinct du harnais

Le premier échec des anciens parcours venait de la ponctuation et de la casse d’une phrase de production ; le texte a été corrigé sans modifier l’assertion. Un nouveau cas de bibliothèque a ensuite nécessité de passer le compteur attendu de 55 à 56, avec les onze cas PostgreSQL toujours exigés. Les compléments de date principale et de choix du corps font partie du commit qualifié.

Un autre run de ce même commit, **44838204942**, avait obtenu **74/75** dans la suite de colonnes : un dépassement de délai du scénario de requêtes de fond avait été masqué par l’énumération concurrente des diagnostics. Ce run ne sert pas de preuve finale. Le présent run indépendant **44838578203** est entièrement réussi avec les exigences originales.

Le suivi de test **`f86a0b126c31dbfebf0354d6bc0610e351ae1674`**, [run 44841566063](https://github.com/M4D135/GLPI-Pilot/actions/runs/44841566063), remplace seulement les listes de phases et de diagnostics de cette fixture par des files concurrentes, sans changer les assertions ni la limite de dix secondes. Sa qualification relève d’un suivi distinct. La cause du dépassement initial reste non établie. **Ce correctif de harnais n’est pas le commit du présent binaire**, et ses résultats ne sont pas additionnés à ceux du run retenu.

## Archive originale et publication

L’original du **run 44838578203, tentative 1** a été téléchargé et inspecté avec `verify-release-packs.py`, terminé avec succès. `pack-receipt.json`, `originals.json`, `pack-artifact.json`, `original-inspection.json` et `original/SHA256SUMS.txt` concordent sur son identité et son empreinte. Le contrôle local est statique (`executed_binaries: false`) ; l’exécution réelle des binaires est attestée séparément par le job Windows natif. Les sélecteurs de l’original enregistré et de la qualification complète ont réussi.

Le workflow de publication a promu cet original **sans reconstruction ni recompression**. Le ZIP et `SHA256SUMS.txt` ont ensuite été téléchargés par leurs liens publics dans le navigateur et comparés **octet pour octet** aux originaux inspectés : les deux comparaisons ont réussi. Une nouvelle inspection du ZIP public a vérifié ses **378 fichiers du manifeste**, son commit et ses exécutables. La release comporte uniquement ces deux fichiers joints ; elle n’est ni un brouillon ni une préversion.

L’artefact de preuves de publication **14682116938** (`published-verification-44844431605-1`) a également été téléchargé. Son SHA-256 `cf36be2091dc9c9b23a0331638d1238e3bd8689f261e4b47ba5fbacdd8467d0a` correspond aux métadonnées GitHub ; ses reçus confirment le run source, l’original publié et la nouvelle inspection publique.

| Preuve | Valeur vérifiée |
| --- | --- |
| Artefact original | **14680817498**, run **44838578203**, tentative **1**, commit `a0676e4163f81b97e7ce737679737f468b93b3b1` |
| ZIP original inspecté | `GLPI-Pilot_4.0.16_PROD-LIGHT_Windows-x64.zip` |
| Taille exacte | **139 101 971 octets** |
| SHA-256 de l’original | `5f7e88d7f83c7a1bc3cce0a16093acfc5797a9cd7040c29b2b7b5ba04f810436` |
| Version / commit source / manifeste | **4.0.16**, **PROD-LIGHT**, commit exact ci-dessus ; **378 fichiers du manifeste vérifiés**, **0 export de sources** |
| SHA-256 Standard | `6f7d640499c10f12d1c6445aa60cd76c675c59c1836b98618fc0395538455052`, concordant avec `wad/version.json` |
| SHA-256 Admin | `7c3189f075bd7dc375b87ca491984ef1decb6de6e52a2da0e973ccb0a2875b7c`, distinct et concordant avec `wad/version.json` |
| Racine et exclusions | **Conformes** : deux EXE et `wad/`, absence de runtime WebView2 embarqué, d’exports de sources et de données opérationnelles interdites par le vérificateur |
| ZIP public | Fichier **773879112**, **139 101 971 octets** ; [téléchargement officiel](https://github.com/M4D135/GLPI-Pilot/releases/download/v4.0.16/GLPI-Pilot_4.0.16_PROD-LIGHT_Windows-x64.zip), même SHA-256 que l’original |
| Fichier public d’empreintes | [SHA256SUMS.txt](https://github.com/M4D135/GLPI-Pilot/releases/download/v4.0.16/SHA256SUMS.txt), fichier **773879114**, **111 octets** ; SHA-256 `15262bc512efb667721727b6492182f8a86d945a6e1df3deb08bee3b7c6076b0` |
| Contrôle après publication | **Réussi** : les deux fichiers retéléchargés sont identiques aux originaux ; les **378 fichiers** du manifeste public sont de nouveau vérifiés |

## Mise à jour et limites

Pour commencer par la consultation seule, désactiver au préalable **Collecter au démarrage** et toute programmation activée dans les réglages de collecte. Les programmations existantes restent celles du profil.

Fermer les deux lanceurs puis copier le dossier `%LOCALAPPDATA%\GLPI-Pilot` entier, notamment `LastSource`, les réglages et la bibliothèque par défaut. Si la bibliothèque est ailleurs, copier aussi ce dossier entier, avec ses éventuels fichiers SQLite auxiliaires, pendant que Pilot est fermé. La sauvegarde des préférences ne remplace pas celle des contenus.

Extraire tout le ZIP publié dans un nouveau dossier local, garder ensemble les deux EXE et `wad/`, puis ouvrir avec le même compte Windows. Le profil et le chemin de bibliothèque existants sont réutilisés. Ne pas effacer la base ni recommencer la collecte pour retrouver les fiches conservées. Charger le cache habituel et ouvrir un ticket dans Rechercher, ou utiliser **Consulter les contenus**. Voir les [notes 4.0.16](RELEASE-4.0.16.md).

Les essais utilisent des pages synthétiques et de vraies bases SQLite/PostgreSQL de test. Ils ne qualifient ni les données privées, ni le portail et le SSO de l’organisation. Le cas terrain des villes **0/17** reste distinct et suivi dans [l’issue #2](https://github.com/M4D135/GLPI-Pilot/issues/2), toujours ouverte lors du contrôle du 21 novembre 2026. Sa résolution n’est pas revendiquée.
