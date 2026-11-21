# GLPI Pilot 4.0.17 — qualification et publication vérifiées

La [release v4.0.17](https://github.com/M4D135/GLPI-Pilot/releases/tag/v4.0.17) est publiée le **21 novembre 2026 à 11:39:30 (Europe/Paris)**, soit **10:39:30 UTC**. La qualification complète et les contrôles après publication sont terminés.

Cette version corrige les bilans de collecte qui pouvaient annoncer une réussite malgré des contenus non confirmés. Ce reçu porte sur un seul commit, un seul run de qualification et l’original PROD-LIGHT identifié ci-dessous. Les [notes de version](RELEASE-4.0.17.md) décrivent les comportements et la procédure de reprise.

## Identité de la livraison

**ZIP distribué : [GLPI-Pilot_4.0.17_PROD-LIGHT_Windows-x64.zip](https://github.com/M4D135/GLPI-Pilot/releases/download/v4.0.17/GLPI-Pilot_4.0.17_PROD-LIGHT_Windows-x64.zip) — 139 130 419 octets.** Le [fichier SHA256SUMS.txt](https://github.com/M4D135/GLPI-Pilot/releases/download/v4.0.17/SHA256SUMS.txt) accompagne cet original.

| Élément | Identité vérifiée |
| --- | --- |
| Version | `4.0.17` |
| Commit du code, des exécutables et du tag `v4.0.17` | `1e8782c539e0b2263de47ed76c274e524e005a5a` |
| Arbre Git | `01a88141dad8dc3047139f57d28a209bcc247558` |
| Qualification | [Run 44892356079](https://github.com/M4D135/GLPI-Pilot/actions/runs/44892356079), exécution nº 102, tentative **1**, **16/16 jobs réussis** |
| Construction Windows | [Job 136810794099](https://github.com/M4D135/GLPI-Pilot/actions/runs/44892356079/job/136810794099) |
| Original GitHub Actions | [Artefact 14703265832](https://github.com/M4D135/GLPI-Pilot/actions/runs/44892356079/artifacts/14703265832) |
| SHA-256 du ZIP distribué | `df7752fee607402030ed76151203f0a871caafdbcdd5cfef86d3c2897d165e97` |
| Publication | [Run 44895345361](https://github.com/M4D135/GLPI-Pilot/actions/runs/44895345361), exécution nº 28, tentative **1**, réussi |
| Release | [v4.0.17](https://github.com/M4D135/GLPI-Pilot/releases/tag/v4.0.17), identifiant **453615713**, ni brouillon ni préversion |
| Preuves après publication | [Artefact 14702764872](https://github.com/M4D135/GLPI-Pilot/actions/runs/44895345361/artifacts/14702764872), `published-verification-44895345361-1` |

Le reçu officiel `GLPI_CI_ORIGINAL`, les métadonnées de l’artefact et les octets téléchargés concordent. Le ZIP a été publié sans recompilation ni recompression, puis retéléchargé depuis son URL publique et comparé octet par octet à l’original inspecté.

## Qualification complète et sélection de l’original

Le run **44892356079**, tentative **1**, s’est terminé avec la conclusion **success** le **21 novembre 2026 à 10:37:32 UTC**. Les **16/16 jobs** ont réussi ; aucun n’est ignoré, annulé ou en échec. La [dernière suite C# et WebView](https://github.com/M4D135/GLPI-Pilot/actions/runs/44892356079/job/136810794135) est également réussie.

Les deux modes de `select-verified-release.py` ont validé le même commit, run, numéro de tentative et SHA-256 : le reçu officiel du job Windows puis l’artefact correspondant. Le fichier `originals.json` a été produit à partir du reçu officiel. L’inspection et la publication sont ainsi rattachées aux mêmes octets.

La promotion a respecté **la réussite de tous les jobs requis du run**. Le workflow de publication a utilisé `source_run=44892356079`, `source_attempt=1` et `light_sha256=df7752fee607402030ed76151203f0a871caafdbcdd5cfef86d3c2897d165e97` ; le tag `v4.0.17` reste fixé au commit qualifié.

## Comportements couverts

- **Réussite vérifiée.** Un passage validé exige toutes les cibles traitées, un périmètre final vérifié et aucune lecture échouée, partielle, conflictuelle, non confirmée ou restant à reprendre. La qualité actuelle des documents est aussi contrôlée ; un bilan incomplet n’avance pas la dernière réussite.
- **Consultation historique.** Le cache retrouve le journal de son installation et peut reconstituer son état d’affichage en mémoire. Cette consultation ne lance pas GLPI et ne réécrit pas les corps, les dates ou les preuves.
- **Précontrôle des dates.** Un ancien relevé peut seulement avertir. L’index GLPI obtenu en ligne est contrôlé avant les fiches ; si aucune date du périmètre contrôlé n’est fiable, leur lecture s’arrête. Aucun fuseau n’est inventé ; une date ISO avec décalage explicite reste admise sans fuseau supplémentaire.
- **Reprise ciblée.** Un plan déjà terminé ne peut pas être rouvert pour produire un nouveau succès sans preuve fraîche. L’interface oriente vers les fiches réellement à reprendre et conserve les motifs historiques même lorsque leur ancien compteur de reprises vaut zéro.

Le bilan de Pilot applique les cinq critères du contrat SQL : `detail_complete=1`, `has_content=1`, `content_confirmed=1`, `confirmation_pending=0`, `conflict_count=0`. Relais vérifie ensuite chaque fragment, son contenu, l’intégrité, les limites de taille et les droits. La présence d’une fiche consultable ne garantit pas son admission dans les connaissances.

## Résultats observés sur ce run

Les suites Core et runtime se recouvrent ; certains contrôles sont réexécutés dans plusieurs jobs. Les exécutions PostgreSQL comprennent aussi des contrôles locaux. Ces compteurs ne doivent pas être additionnés en un total général.

| Contrôle | Résultat établi | Preuve GitHub |
| --- | --- | --- |
| Collecte Core / avec intégration runtime | **73/73 / 91/91**, Core inclus dans runtime | [Collecte](https://github.com/M4D135/GLPI-Pilot/actions/runs/44892356079/artifacts/14702667780) |
| Bibliothèque SQLite / journal / progression de l’index | **47/47 / 10/10 / 9/9** | [Collecte](https://github.com/M4D135/GLPI-Pilot/actions/runs/44892356079/artifacts/14702667780) |
| JavaScript / interface sous CSP de production | **46/46 / 41/41** | [Collecte](https://github.com/M4D135/GLPI-Pilot/actions/runs/44892356079/artifacts/14702667780) |
| Parcours C# de reprise / acquisition navigateur | **19/19 / 60/60** | [Job 136810794435](https://github.com/M4D135/GLPI-Pilot/actions/runs/44892356079/job/136810794435) |
| PostgreSQL : bibliothèque / partage | **58/58 / 44/44** | [PostgreSQL](https://github.com/M4D135/GLPI-Pilot/actions/runs/44892356079/artifacts/14702871258) |
| PostgreSQL : connaissances / administration | **12/12 / 19/19** | [Job 136810794356](https://github.com/M4D135/GLPI-Pilot/actions/runs/44892356079/job/136810794356) |
| Prévalidation : automatisation | **75/75** | [Job 136810794175](https://github.com/M4D135/GLPI-Pilot/actions/runs/44892356079/job/136810794175) |
| Prévalidation : sources / actualisation | **60/60 / 19/19 dans chaque édition Standard et Admin** | [Prévalidation](https://github.com/M4D135/GLPI-Pilot/actions/runs/44892356079/job/136810794175) |
| Vérificateur du contenu ZIP, `test_verify.py` | **26 tests, résultat OK** | [Collecte](https://github.com/M4D135/GLPI-Pilot/actions/runs/44892356079/artifacts/14702667780) |
| Hôtes Windows réels Standard et Admin | **72/72** | [Job Windows](https://github.com/M4D135/GLPI-Pilot/actions/runs/44892356079/job/136810794099) |
| Mise à jour et retour arrière | **4/4** | [Job Windows](https://github.com/M4D135/GLPI-Pilot/actions/runs/44892356079/job/136810794099) |
| Dernière suite : automatisation cœur / colonnes / exports natifs | **63/63 / 75/75 / 11/11** | [C# et WebView](https://github.com/M4D135/GLPI-Pilot/actions/runs/44892356079/job/136810794135) |
| Dernière suite : lecture des fiches / acquisition de bibliothèque | **36/36 / 60/60** | [C# et WebView](https://github.com/M4D135/GLPI-Pilot/actions/runs/44892356079/job/136810794135) |

Les contextes des artefacts annoncent bien le commit `1e8782c` et la tentative indiquée. Le serveur des suites SQL est **PostgreSQL 17.11**. Les quatre totaux PostgreSQL concordent avec leurs lignes `PASS` ; les autres lignes sont des traces de préparation ou de chronométrage.

| Archive de preuves contrôlée | Taille | SHA-256 recalculé, conforme aux métadonnées |
| --- | --- | --- |
| `postgres-checks-44892356079-1` | **6 864 octets** | `9bce09f42be22625382423f0a2d5d5c190710cdd2bdc9c9ebe28c75265696857` |
| `library-collection-44892356079-1` | **1 246 539 octets** | `91244d1bdfe6c289536525d234764fda4d2369eab9fcbd5a952f4793b227634b` |

Ces empreintes identifient les preuves de test ; l’empreinte du pack Windows figure dans l’identité de la livraison.

### Prévalidation des requêtes de fond étrangères

Le correctif de harnais conserve **dix secondes depuis `RunAsync`** jusqu’au point de contrôle réel de sélection CSV et de fermeture unique des colonnes. Le transfert conserve ensuite son budget d’export existant de dix secondes. Les assertions réseau, le CSV exact, les colonnes inchangées, l’export unique et la phase finale restent intactes ; aucun réglage produit n’a changé.

Le journal officiel de prévalidation mesure **8,485 secondes** jusqu’au point de contrôle, puis enregistre le PASS le **21 novembre 2026 à 10:08:45 UTC**. La dernière suite C# et WebView rejoue ce scénario avec un point de contrôle à **7,515 secondes**, également suivi d’un PASS. Ces mesures concernent deux exécutions distinctes de la fixture synthétique. Le journal de prévalidation contrôlé mesure **69 862 octets**, SHA-256 `fddde9e0a87d826e116573ae2175e1466428917782c3399ead02174c2766c784`.

### Bilan visible dans l’interface

La capture synthétique `collection-unconfirmed-quality.png`, dans l’artefact de collecte, montre **800 contenus lus**, **800 versions non confirmées** et **0 / 800 fiches complètes et confirmées**. Les compteurs sont lisibles dans la largeur mobile, sans débordement visible des deux colonnes de motifs.

Le cadrage coupe le haut et le bas de la carte ; il ne prouve pas la visibilité de tout le panneau. Le titre exact « Passage à compléter », son rôle `status` et `aria-live=polite` sont vérifiés séparément par le scénario navigateur réussi.

## Inspection de l’original Windows

Le vérificateur canonique a validé **381 fichiers manifestés**, plus le manifeste lui-même : **382 fichiers** au total. La racine contient `GLPI-Pilot.exe`, `GLPI-Pilot-Admin.exe` et `wad/`. La version, le commit, la distribution et les empreintes déclarées concordent.

La comparaison exhaustive avec le commit de la livraison porte sur les chemins **et** le contenu de chaque fichier. Aucun chemin attendu n’est absent, aucun chemin supplémentaire n’est présent dans ces ensembles, et aucun fichier n’a été ignoré.

| Ensemble | Couverture exacte | Identiques octet par octet | Différence limitée à CRLF/LF |
| --- | --- | --- | --- |
| `wad/web/` | **134/134 fichiers** | **10** | **124** |
| `wad/docs/` | **109/109 documents** | **2** | **107** |

Les documents correspondent aux 108 fichiers suivis sous `docs/`, plus `README.md` copié vers `wad/docs/LISEZ-MOI.md` selon le build. Aucune autre normalisation n’a été utilisée. Le nouveau `wad/web/js/collection-quality.js` est présent et son import exact depuis `wad/web/js/pages/library-collection.js` est vérifié.

Les ressources PE des deux exécutables indiquent **AMD64 (`0x8664`)**, `FileVersion=4.0.17.0` et `ProductVersion=4.0.17+1e8782c539e0b2263de47ed76c274e524e005a5a`. Leurs empreintes distinctes correspondent à `wad/version.json`.

| Exécutable | SHA-256 |
| --- | --- |
| `GLPI-Pilot.exe` | `59ee2333e4b24c3f4c8f73f097d6bc83f48daef2de28ee6fa9649f2f0e8cc2ab` |
| `GLPI-Pilot-Admin.exe` | `d7c7fb2ab70d2bb4d81d6157df66be06bbcf201b43f2767184714dbd36463833` |

Le pack PROD-LIGHT utilise le runtime WebView2 installé sur Windows (`evergreen-installed`) et n’embarque ni runtime fixe ni export de sources. Aucun nom de base SQLite, export opérationnel, journal, profil, session, `LastSource`, `TicketCache` ou fichier de secrets identifié n’a été trouvé. Cette revue d’inventaire ne constitue pas une analyse de secrets dans tous les octets du pack.

### Provenance des contrôles source et PE

Les liens GitHub ci-dessus désignent les jobs et artefacts de qualification. La comparaison web/docs et la lecture des ressources PE sont des **contrôles indépendants effectués après téléchargement**, sur les octets liés au reçu Windows et au commit exact. Ils ont utilisé les blobs Git, une lecture ZIP et un lecteur de ressources PE, **sans extraction ni exécution des EXE**.

Un rapport JSON détaillé a été conservé pendant la préparation, hors des artefacts GitHub cités. Il documente les chemins, empreintes, normalisations de fins de ligne et versions vérifiés ; il n’est pas présenté comme un fichier public. Les résultats de cette inspection sont consignés dans ce reçu.

## Conservation des données et limites

Avant mise à jour, fermer les deux lanceurs et sauvegarder le dossier de profil ainsi que le dossier complet de bibliothèque, avec ses fichiers SQLite auxiliaires éventuels. Extraire le pack dans un nouveau dossier d’application, puis conserver le compte Windows et les réglages habituels. La mise à jour ne réinitialise pas la base et n’accorde aucune confirmation rétroactive.

Vérifier le format et le fuseau effectivement utilisés par GLPI, puis collecter quelques tickets représentatifs et analyser une copie SQLite cohérente dans Relais avant d’étendre le périmètre. Un changement de format ou de fuseau ne rend pas comparables des versions anciennes qui ne l’étaient pas.

Si une recollecte neuve est choisie explicitement, suivre la procédure des notes de version : copie vers un dossier local distinct, sauvegarde native contrôlée puis réinitialisation des seuls tickets de cette copie dans Admin. **Changer de dossier seul copie la bibliothèque existante.** Conserver l’original et désactiver le partage pendant la vérification pour éviter de réintroduire les anciennes versions.

Les essais utilisent des tickets synthétiques et de vraies bases SQLite/PostgreSQL. Ils ne constituent pas une analyse de la base privée de l’organisation, de son portail GLPI ou de son SSO. La cause de chaque exclusion réelle reste à établir à partir de ses diagnostics. Aucune durée de collecte terrain ni disparition garantie des huit heures observées n’est revendiquée.

## Historique des trois runs écartés

| Run | Cause et correction |
| --- | --- |
| [44888790666 — dc2f0b3](https://github.com/M4D135/GLPI-Pilot/actions/runs/44888790666) | Attente CI restée à 56 contrôles PostgreSQL après l’ajout de deux cas ; nombre attendu corrigé à 58. |
| [44889349374 — adf2f6b7](https://github.com/M4D135/GLPI-Pilot/actions/runs/44889349374) | Sélecteur de test `heading` incompatible avec le rôle explicite `status` ; vérification du nœud visible, du texte exact et de ses attributs ARIA. |
| [44890447108 — 01e536a](https://github.com/M4D135/GLPI-Pilot/actions/runs/44890447108) | Watchdog global englobant préparation et export ; frontières du harnais corrigées avec les plafonds existants et les assertions métier conservées. |

Aucun de ces runs n’est utilisé pour qualifier le ZIP identifié en tête. Leurs preuves restent historiques. Le diagnostic du dernier échec ne démontre pas une régression de production entre les deux candidats alors comparés ni une cause d’infrastructure certaine.

## Contrôle des fichiers publiés

Le [run de publication 44895345361](https://github.com/M4D135/GLPI-Pilot/actions/runs/44895345361), tentative 1, a réussi. Le ZIP et `SHA256SUMS.txt` ont été retéléchargés depuis leurs URL publiques, puis comparés **octet par octet** aux fichiers originaux inspectés. Les deux comparaisons ont réussi. Le vérificateur canonique a de nouveau validé les **381 fichiers manifestés** du ZIP distribué.

| Fichier public | Identifiant | Taille | SHA-256 vérifié |
| --- | --- | --- | --- |
| [ZIP PROD-LIGHT Windows x64](https://github.com/M4D135/GLPI-Pilot/releases/download/v4.0.17/GLPI-Pilot_4.0.17_PROD-LIGHT_Windows-x64.zip) | **775230926** | **139 130 419 octets** | `df7752fee607402030ed76151203f0a871caafdbcdd5cfef86d3c2897d165e97` |
| [SHA256SUMS.txt](https://github.com/M4D135/GLPI-Pilot/releases/download/v4.0.17/SHA256SUMS.txt) | **775230930** | **111 octets** | `483c9bb634f1cfc9f9321d7ac3b0496ea88147a44ca2c8f1be5f816785d7ae89` |

Les quatre rapports de manifeste — inspection originale, inspection de la copie publique, `candidate-archives.json` et `published-archives.json` — sont identiques. La release **453615713** est publiée, ni brouillon ni préversion, avec le tag au commit exact `1e8782c539e0b2263de47ed76c274e524e005a5a`.

L’[artefact de preuves 14702764872](https://github.com/M4D135/GLPI-Pilot/actions/runs/44895345361/artifacts/14702764872), `published-verification-44895345361-1`, mesure **29 521 octets**. Son SHA-256 est `6ee94c3eff21b6c015fd9e7c0563c1f420cb320a019ad090225f80d88db3f776`. Il conserve les reçus et rapports du workflow de publication.

Ce reçu est ajouté après construction. Le ZIP conserve les documents de son commit source ; le tag et l’identité du binaire restent inchangés.
