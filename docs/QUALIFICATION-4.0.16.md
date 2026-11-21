# GLPI Pilot 4.0.16 — qualification et publication vérifiées

La [release v4.0.16](https://github.com/M4D135/GLPI-Pilot/releases/tag/v4.0.16) est publiée le **21 novembre 2026 à 01:32 (Europe/Paris)**. Elle permet de consulter les contenus déjà recueillis depuis le cache, de vérifier leur présence localement et de réutiliser les tickets inchangés sans expiration fondée sur leur âge.

## Identité de la livraison

| Élément | Valeur vérifiée |
| --- | --- |
| Commit du code, des exécutables et du tag `v4.0.16` | `a0676e4163f81b97e7ce737679737f468b93b3b1` |
| Qualification Windows et PostgreSQL | [Run 44838578203](https://github.com/M4D135/GLPI-Pilot/actions/runs/44838578203), tentative `1`, **16/16 jobs réussis** |
| Publication | [Run 44844431605](https://github.com/M4D135/GLPI-Pilot/actions/runs/44844431605), tentative `1`, [job 136660552108](https://github.com/M4D135/GLPI-Pilot/actions/runs/44844431605/job/136660552108) réussi |
| Artefact de qualification original | `14680817498`, produit par le [job Windows 136642718657](https://github.com/M4D135/GLPI-Pilot/actions/runs/44838578203/job/136642718657) |
| ZIP public | `GLPI-Pilot_4.0.16_PROD-LIGHT_Windows-x64.zip`, fichier `773879112` |
| Fichier public d’empreintes | `SHA256SUMS.txt`, fichier `773879114` |
| Taille du ZIP | **139 101 971 octets** |
| SHA-256 du ZIP | `5f7e88d7f83c7a1bc3cce0a16093acfc5797a9cd7040c29b2b7b5ba04f810436` |
| Preuves après publication | [Artefact 14682116938](https://github.com/M4D135/GLPI-Pilot/actions/runs/44844431605/artifacts/14682116938), `published-verification-44844431605-1` |

Téléchargements : [ZIP PROD-LIGHT Windows x64](https://github.com/M4D135/GLPI-Pilot/releases/download/v4.0.16/GLPI-Pilot_4.0.16_PROD-LIGHT_Windows-x64.zip) et [SHA256SUMS.txt](https://github.com/M4D135/GLPI-Pilot/releases/download/v4.0.16/SHA256SUMS.txt).

La qualification complète a été exécutée sur `qualify/4.0.16-a0676e4`, puis ce commit exact a été avancé sur `main`. Aucun des 16 jobs n’est ignoré, annulé ou en échec. Le run automatique de `main` qui constate une qualification déjà acquise ne remplace pas cette exécution complète. Le tag et le ZIP restent attachés au code qualifié ; ce reçu est ajouté après la construction.

## Comportements qualifiés

**Consulter les contenus conservés.** Rechercher ouvre les descriptions, suivis, tâches et autres rubriques déjà enregistrés, y compris depuis un relevé restauré en mode cache. Les contenus partiels ou non confirmés restent lisibles avec leurs dates et limites. Un contenu absent est signalé sans réafficher celui du ticket précédent.

Le numéro du ticket reste associé à l’installation GLPI et au mapping du relevé d’origine. Changer le profil de la prochaine collecte ne fait pas emprunter le contenu d’un ticket de même numéro sur un autre portail. Le bilan et le journal conservent également l’installation du cache.

**Vérifier la présence localement.** Le bouton « Vérifier les contenus conservés » recense les copies présentes, complètes ou partielles, absentes, archivées et à confirmer. Cette lecture SQLite n’ouvre pas GLPI et ne réécrit pas les observations. Le bilan vérifie la disponibilité et la cohérence élémentaire des copies ; il ne compare pas leur contenu à l’état actuel du portail.

**Reprendre un complément borné.** « Compléter seulement les contenus absents » enregistre sa liste avant navigation. Une pause ou un redémarrage conserve ce sous-ensemble, et les contenus déjà sauvegardés pendant ce complément ne sont pas relus pour leur seule fraîcheur incertaine. « Reprendre les erreurs » demeure une action distincte pouvant relire des contenus partiels ou non confirmés. Les commandes en ligne indisponibles en cache sont expliquées et les erreurs de reprise restent visibles.

**Réutiliser les tickets inchangés.** Une copie complète et confirmée dont la version correspond au relevé GLPI courant reste réutilisable en live et en collecte globale ou ciblée, même après sept jours. Les contrôles d’identité, de version, de précision des dates et de couverture restent appliqués. Les contenus absents, modifiés, incomplets ou sans version comparable peuvent encore nécessiter une lecture. La relecture forcée reste volontaire.

La lecture d’un onglet conserve le repère de version et la date du corps principal. La réouverture après redémarrage est couverte avec une copie hors connexion **explicitement enregistrée**, sans promettre la persistance implicite de toute lecture live. Un onglet récent ne rajeunit plus un corps ancien. Les anciens JSON dépourvus de date principale séparée restent consultables sans recevoir une date inventée.

La liste et le lecteur appliquent le même choix entre copie personnelle et bibliothèque. Les dates de lecture du corps déterminent la préférence lorsqu’elles sont connues ; un onglet personnel récent ne masque plus un corps bibliothèque plus récent, et un corps personnel réellement plus récent est conservé. Le recours aux versions pour les anciennes copies exige un même contexte d’interprétation. Des parseurs UTC et Europe/Paris différents ne servent pas à inventer un ordre entre versions. La sélection ne réécrit ni SQLite ni les observations personnelles ; les onglets d’une autre version restent dans leur copie d’origine.

Les scénarios de collecte conservent des copies complètes et confirmées âgées de **trente jours**, inchangées dans le relevé GLPI : en global comme en ciblé, y compris après pause et redémarrage, les métadonnées restent contrôlées avec **zéro requête de détail**. Le corps, sa date, son empreinte et sa preuve restent conservés. Les observations d’index et le journal peuvent toujours être enregistrés.

Les [notes de version](RELEASE-4.0.16.md) détaillent ces comportements.

## Résultats observés

Les journaux officiels du **run 44838578203, tentative 1**, établissent les résultats suivants. Les lignes décrivent des suites qui peuvent se recouvrir ; elles ne doivent pas être additionnées en un total unique.

| Contrôle | Résultat |
| --- | --- |
| Bibliothèque SQLite, dont audit local et conservation | **45/45** |
| Lecture directe des fiches / acquisition de bibliothèque | **36/36** et **60/60** |
| Collecte : moteur seul / moteur avec intégration runtime | **64/64** et **79/79**, suites qui se recouvrent |
| Parcours navigateur de collecte et reprise | **17/17** |
| Moteur applicatif complet | **225/225** |
| Régressions du cache local incluses dans le moteur | **19 contrôles sans navigateur + 1 scénario de lecture automatique**, tous réussis |
| Régressions du cache de source incluses dans le moteur | **15/15** |
| Cache restauré dans WebView | **17/17** |
| JavaScript : lecture live, cache, actualisation des interventions, journal et périmètre | **31/31 + 34/34, soit 65/65** |
| Parcours historiques de bibliothèque | **14/14 dans chaque édition** |
| Transitions de contexte, Standard et Admin | **19/19, trois exécutions successives dans chaque édition** |
| Colonnes et restauration navigateur / attente des exports natifs | **75/75** et **11/11** |
| Données et sources / actualisation de l’espace | **60/60** et **19/19 dans chaque édition** |
| PostgreSQL : bibliothèque / partage / corpus SQL / administration | **56/56**, **44/44**, **12/12** et **19/19** |
| Structure de distribution / hôtes Windows réels | **14/14** et **72/72** |
| Mise à jour Windows et retour arrière | **4/4** |
| Verrou PostgreSQL du collecteur | **6/6** |
| Configuration / restauration pure / intégration / automatisation | **96/96**, **27/27**, **65/65**, **63/63** |
| Vérificateur canonique du ZIP | **378 fichiers manifestés vérifiés** |

Les suites bibliothèque et partage exécutent respectivement **11** et **19** scénarios avec une vraie base PostgreSQL. Les deux éditions Standard et Admin sont couvertes par les parcours Windows. Les tests ciblés du cache vérifient notamment les dates principales, l’onglet personnel récent face au corps bibliothèque plus récent, le cas inverse, les anciens JSON, les fuseaux différents et l’absence de modification des copies pendant la consultation.

Le premier échec des anciens parcours de reprise venait de la ponctuation et de la casse d’une phrase de production ; le texte a été corrigé sans modifier l’assertion. Un nouveau cas de bibliothèque a ensuite nécessité de passer le compteur attendu de 55 à 56, avec les onze cas PostgreSQL toujours exigés.

Un autre run du commit a067, `44838204942`, a été arrêté après un dépassement de délai dans la prévalidation des colonnes ; il n’est pas utilisé pour cette livraison. Le complément du harnais **`f86a0b126c31dbfebf0354d6bc0610e351ae1674`** protège les collecteurs de phases et de diagnostics contre les événements simultanés afin de conserver le diagnostic du délai. Il a réussi **16/16 jobs** dans le [run distinct 44841566063](https://github.com/M4D135/GLPI-Pilot/actions/runs/44841566063), tentative **1**, terminé le **21 novembre à 01:45 (Europe/Paris)**. Le scénario concerné passe dans ses deux suites de colonnes, préflight et C#/WebView, avec les assertions et le délai de dix secondes inchangés. **La cause du dépassement initial reste non établie.** Ce complément ne modifie pas l’application et ne fait pas partie du ZIP 4.0.16. La preuve de qualification du binaire reste exclusivement le run **44838578203** indiqué ci-dessus.

## Inspection du ZIP et publication

Le ZIP original de l’artefact `14680817498` a été téléchargé et inspecté sans exécuter ses EXE. Sa taille et son SHA-256 ont été recalculés. Les deux modes du sélecteur de release ont confirmé le run, sa tentative, le commit, le reçu officiel du job Windows et l’artefact correspondant.

L’archive contient **378 fichiers manifestés**, plus le manifeste lui-même. La racine contient les deux exécutables et `wad/`, sans doublon ni chemin dangereux. Les ressources PE confirment deux exécutables x64 en **4.0.16.0**, avec `ProductVersion=4.0.16+a0676e4163f81b97e7ce737679737f468b93b3b1`.

| Exécutable | SHA-256 |
| --- | --- |
| `GLPI-Pilot.exe` | `6f7d640499c10f12d1c6445aa60cd76c675c59c1836b98618fc0395538455052` |
| `GLPI-Pilot-Admin.exe` | `7c3189f075bd7dc375b87ca491984ef1decb6de6e52a2da0e973ccb0a2875b7c` |

L’inspection indépendante confirme les six modules web modifiés et les 107 documents embarqués par rapport au commit source, avec normalisation des seules fins de ligne lorsque nécessaire. Aucun fichier opérationnel apparent n’a été trouvé dans l’inventaire : base SQLite, export de données, journal, profil personnel, LastSource, TicketCache ou fichier de secrets identifié.

Le workflow de publication a promu **cet original**, sans recompilation ni recompression. Il a retéléchargé le ZIP et `SHA256SUMS.txt` depuis leurs adresses publiques, comparé les octets aux originaux et réexécuté le vérificateur canonique. Les rapports `candidate-archives.json` et `published-archives.json` sont identiques au rapport de l’inspection locale. Le reçu `verification.log` confirme le commit, les deux runs, l’empreinte et `PROD-LIGHT: OK`. La release publique n’est ni un brouillon ni une préversion.

Une vérification indépendante a aussi retéléchargé les deux fichiers publics par le navigateur et comparé leurs octets aux originaux inspectés, puis revérifié les **378 fichiers** du manifeste. Les deux comparaisons ont réussi. Le fichier public `SHA256SUMS.txt` mesure **111 octets** et son SHA-256 est `15262bc512efb667721727b6492182f8a86d945a6e1df3deb08bee3b7c6076b0`. L’artefact de preuves de publication **14682116938** a été téléchargé ; son SHA-256 `cf36be2091dc9c9b23a0331638d1238e3bd8689f261e4b47ba5fbacdd8467d0a` concorde avec les métadonnées GitHub.

## Conservation des données et mise à jour

Fermer les deux éditions puis copier le dossier **`%LOCALAPPDATA%\GLPI-Pilot` entier** pour conserver une copie de retour. Si la bibliothèque est configurée ailleurs, copier aussi ce dossier entier, avec ses éventuels fichiers SQLite auxiliaires, pendant que Pilot est fermé. Une sauvegarde des seules préférences ne remplace pas celle des contenus.

Extraire le ZIP publié dans un nouveau dossier local, en gardant ensemble `GLPI-Pilot.exe`, `GLPI-Pilot-Admin.exe` et `wad/`, puis ouvrir avec le même compte Windows, le même profil et la bibliothèque existante. Le pack PROD-LIGHT utilise le runtime WebView2 disponible sur Windows.

Pour une première ouverture limitée à la consultation, désactiver au préalable **Collecter au démarrage** et les programmations déjà activées. Le correctif n’ajoute aucune nouvelle recollecte automatique, mais les programmations existantes restent celles du profil.

Charger le cache habituel, puis ouvrir un ticket dans **Rechercher**, ou utiliser **Données → Collecte et reprise → Vérifier les contenus conservés / Consulter les contenus**. Aucune réinitialisation ou recollecte n’est nécessaire pour lire les fiches déjà présentes. Les données réellement absentes ne sont pas reconstituées artificiellement.

Les scénarios utilisent des données synthétiques et de vraies bases SQLite/PostgreSQL. Aucun accès au portail privé de l’organisation ou à ses tickets n’est revendiqué. Les réglages, le SSO et les accès réseau propres au poste restent à vérifier dans son contexte autorisé. Le cas terrain des villes **0/17** reste distinct et suivi dans [l’issue #2](https://github.com/M4D135/GLPI-Pilot/issues/2), toujours ouverte lors du contrôle du **21 novembre 2026** ; sa résolution n’est pas revendiquée.

Ce reçu consigne la publication après construction : le ZIP conserve les documents de son commit source, et le tag `v4.0.16` reste fixé à ce commit.
