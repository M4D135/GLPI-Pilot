# GLPI Pilot 4.0.15 — qualification et publication vérifiées

La [release v4.0.15](https://github.com/M4D135/GLPI-Pilot/releases/tag/v4.0.15) est publiée le **20 novembre 2026 à 14:05 (Europe/Paris)**. Elle ajoute le secours automatique par parse du tableau, prépare la pagination native et corrige la fin des collectes et la restauration des colonnes.

## Identité de la livraison

| Élément | Valeur vérifiée |
| --- | --- |
| Commit du code, du binaire et du tag `v4.0.15` | `d6515fb91abb31a06ca76b5c2c20611b1b3e7b5a` |
| Qualification Windows et PostgreSQL | [Run 44735890895](https://github.com/M4D135/GLPI-Pilot/actions/runs/44735890895), tentative `1`, **16/16 jobs réussis** |
| Publication | [Run 44741211137](https://github.com/M4D135/GLPI-Pilot/actions/runs/44741211137), tentative `1`, [job 136305050590](https://github.com/M4D135/GLPI-Pilot/actions/runs/44741211137/job/136305050590) réussi |
| Artefact de qualification original | `14628982387` |
| ZIP public | `GLPI-Pilot_4.0.15_PROD-LIGHT_Windows-x64.zip`, fichier `771861710` |
| Fichier public d’empreintes | `SHA256SUMS.txt`, fichier `771861717` |
| Taille du ZIP | **138 891 510 octets** |
| SHA-256 du ZIP | `201922c5f2705f7a45f51973c68d67985be55c90c52f547c8ea42ad15233468b` |

Téléchargement : [ZIP PROD-LIGHT Windows x64](https://github.com/M4D135/GLPI-Pilot/releases/download/v4.0.15/GLPI-Pilot_4.0.15_PROD-LIGHT_Windows-x64.zip).

La qualification a été lancée sur la branche `fix/4.0.15-column-restoration-budget`, puis ce commit exact a été avancé sur `main`. Le run automatique de `main` qui constate une qualification déjà acquise ne remplace pas le run complet ci-dessus. Le tag reste attaché au code qualifié.

## Comportement qualifié

L’export natif reste la première méthode. Une commande indisponible ou ambiguë avant envoi, une panne de transfert, un dépassement du délai, un reçu illisible, un nombre de lignes non conforme ou des identifiants initiaux absents déclenche le parse de la même recherche lorsque le secours automatique est activé, ce qui est la valeur par défaut. Après un échec ou un délai de transfert, sa page est retirée et la reprise utilise une page fraîche. Un transfert terminé dont le reçu est invalide ou partiel peut réutiliser sa page après rechargement. La commande d’export n’est jamais renvoyée. Pilot revient à la première page, vérifie de nouveau le périmètre et conserve les colonnes et identifiants observés. Le fichier reçu initialement reste intact ; le résultat du parse possède son propre fichier et sa provenance.

Les seules divergences de valeurs dans un CSV utilisable gardent leur signalement et ne déclenchent pas, à elles seules, le secours.

Le changement de méthode est annoncé par une information : « Collecte poursuivie en mode parse du tableau. » Un résultat ensuite vérifié conserve un statut normal. Les contrôles de périmètre et de couverture gardent leurs effets ; une annulation, une perte de session ou de droits, une origine refusée, une tentative d’export ambiguë après envoi ou une erreur d’écriture locale ne deviennent pas un succès de secours.

La pagination native est préparée avant le parse du tableau et avant la lecture de l’index de bibliothèque. Lorsqu’un contrôle natif est identifié sans ambiguïté, Pilot choisit 1 000, sinon la plus petite option supérieure réellement offerte ; il conserve une limite déjà au moins égale à 1 000. En l’absence de ces valeurs, il utilise la plus grande option disponible en excluant 9 999 999 des nouveaux choix automatiques. Les options 20/50/9 999 999 conduisent donc à 50, tandis qu’une limite existante de 2 000 ou 9 999 999 reste inchangée, sans commande. Un contrôle absent ou ambigu laisse la pagination actuelle en place, sans mutation. La réponse, la plage, le total, les identifiants, les colonnes et le contexte sont vérifiés après l’unique action native. La limite réellement appliquée est journalisée et reste dans la session GLPI.

Le nettoyage des colonnes renouvelle son budget après une progression native confirmée et enregistrée. Les sondages et messages d’attente ne le prolongent pas. Une pause après six retraits conserve les quatre restants et évite de répéter une commande incertaine. Les échéances récurrentes partent de la fin du cycle ; un second départ est refusé pendant une collecte et une restauration en attente suspend les collectes automatiques. Les données déjà reçues et les opérations locales restent accessibles. Les formulaires propres suivent les nouvelles révisions ; les brouillons modifiés gardent leur protection.

Les [notes de version](RELEASE-4.0.15.md) détaillent ces choix et les sources officielles GLPI consultées.

## Résultats observés

Les journaux officiels du run 44735890895 établissent les résultats suivants. Aucun de ses 16 jobs n’est ignoré, annulé ou en échec.

| Contrôle | Résultat |
| --- | --- |
| Navigateur et exports | **65/65** |
| Automatisation générale, dont neuf scénarios de secours | **63/63** |
| Colonnes et restauration navigateur | **75/75** |
| Attente des exports natifs | **11/11** |
| Acquisition de bibliothèque, dont quatorze scénarios de pagination native | **60/60** |
| Parcours complets de collecte et reprise | **15/15** |
| Configuration / règles pures de restauration | **96/96** et **27/27** |
| Détection de villes et disponibilité des sources | **116/116** |
| Lecture directe des fiches | **36/36** |
| Isolation et arrêt du moteur | **215/215** |
| Données et sources, Standard puis Admin | **60/60** dans chaque édition |
| Actualisation de l’espace, Standard puis Admin | **19/19** dans chaque édition |
| Structure de distribution / hôtes Windows WebView réels | **14/14** et **72/72** |
| Mise à jour Windows et retour arrière | **4/4** |
| PostgreSQL 17 : bibliothèque / partage / corpus SQL / administration | **55/55**, **44/44**, **12/12** et **19/19** |
| Règles de publication et sélection des packs | **42 tests**, résultat `OK` |

Les neuf scénarios d’automatisation couvrent HTTP 500, rupture réseau, expiration du délai, CSV invalide, CSV partiel, commandes d’export ambiguës avant action, secours désactivé, changement de périmètre et perte d’une colonne initiale. Les cas de réussite vérifient la couverture 45/45, la commande de pagination unique, l’absence d’un second export, la conservation de tout reçu effectivement téléchargé et le résultat normal. Les erreurs de périmètre et de colonnes restent des refus.

Les quatorze scénarios de pagination couvrent notamment les contrôles natifs modernes et historiques, Select2, la limite 2 000 en l’absence de 1 000, le maximum 50, les grandes limites existantes, les pagers concordants ou ambigus, le refus d’une modification, les réponses tardives et les dérives de critères. Les cas 20/50/9 999 999 et les limites existantes 2 000/9 999 999 sont inclus dans les 60 contrôles réussis.

La restauration vérifie dix retraits uniques, une confirmation retardée et une interruption après six retraits suivie de la reprise des quatre restants. Les garde-fous d’inactivité restent en place. Les essais de qualité vérifient la conservation du CSV et la séparation entre réception, périmètre, couverture et restauration.

## Portée des essais et suivi terrain

Les scénarios GLPI utilisent des pages et des données synthétiques. Le portail privé de l’organisation et son SSO n’ont pas été ouverts pour cette qualification. Les exécutables Windows ont été essayés sur les runners de qualification ; l’inspection locale du ZIP n’a pas exécuté de binaire.

Le diagnostic des sources de villes indique les rôles disponibles et les nombres de références et variantes sans journaliser les valeurs privées. Treize références synthétiques sont vérifiées avec accents, apostrophes, tirets, casse et alias, ainsi que les cas vides et ambigus. Le moteur de correspondance n’a pas été modifié : **la cause du cas privé 0/17 reste à confirmer** à partir du prochain diagnostic. Le [suivi #2](https://github.com/M4D135/GLPI-Pilot/issues/2) reste ouvert pour ce point. La demande de pagination et de secours automatique du [suivi #4](https://github.com/M4D135/GLPI-Pilot/issues/4) est livrée.

## Vérification du pack et de sa publication

L’inspection du ZIP original confirme la version, le commit source, la distribution PROD-LIGHT et les **375 fichiers** du manifeste. Aucun export de sources n’est recensé. Les exécutables `GLPI-Pilot.exe` et `GLPI-Pilot-Admin.exe` ont des empreintes distinctes et sont distribués avec `wad/` à la racine.

Les deux modes du sélecteur de release vérifiée ont réussi. Le reçu officiel du job Windows et le digest de l’artefact concordent avec l’empreinte inspectée. Le workflow de publication vérifie l’archive avant publication, puis télécharge à nouveau les fichiers publics et les compare octet pour octet aux originaux. Aucun ZIP n’a été reconstruit ou recompressé pour la promotion.

La page de release a également été vérifiée visuellement ; une [capture de la publication publique](evidence/GLPI-Pilot-4.0.15-publication.jpg) accompagne ce reçu.

## Mise à jour et reprise

Fermer Pilot, puis extraire le ZIP publié dans un nouveau dossier local en gardant ensemble les deux exécutables et `wad/`. Les données et réglages du profil utilisateur sont conservés. PROD-LIGHT utilise le runtime WebView2 disponible sur Windows ; les essais réels ont utilisé le runtime Evergreen du runner.

Ce reçu et le suivi de publication sont ajoutés après la construction. Le ZIP conserve les documents présents dans son commit source, et le tag `v4.0.15` reste attaché à ce commit. La release 4.0.14 demeure inchangée.
