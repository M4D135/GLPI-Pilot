# GLPI Pilot 3.0.2 — CSV utilisable avec réserves et textes GLPI complets

Cette version corrige une régression d’usage du parcours de collecte par rapport à la version 2.0. Elle conserve l’architecture C# / WinForms / WebView2, les fonctions métier et les garanties de la [3.0.1](RELEASE-3.0.1.md).

## Un CSV valide reste disponible lorsque des valeurs diffèrent

Le fichier natif pouvait être téléchargé et lu correctement, avec le nombre de lignes attendu et les bons identifiants, puis être bloqué par la comparaison de ses valeurs avec la liste HTML. L’interruption se produisait après la lecture du CSV, avant la mise à disposition du relevé, du classeur Excel et de l’enrichissement des villes.

La version 3.0.2 distingue désormais ces situations :

| Situation | Résultat |
| --- | --- |
| CSV valide, identifiants et valeurs concordants, autres contrôles satisfaits | Parcours habituel, avec complétude établie selon l’ensemble des contrôles. |
| CSV valide, identifiants concordants, valeurs différentes ou comparaison de valeurs indisponible | Données chargées ; relevé, dashboards, requêtes, villes et exports utilisables avec `Complete=false`, l’état **À vérifier** et les avertissements correspondants. |
| Identifiant attendu absent lors du rapprochement initial | Import refusé ; fichier natif conservé, aucun repli silencieux. |
| CSV corrompu ou document reçu dans un autre format | Import refusé ; fichier reçu conservé, aucun remplacement par une autre méthode. |

Le contrôle final du contexte, du total et de l’échantillon reste actif. Une évolution de la recherche ou un contrôle devenu indisponible empêche toujours de certifier un relevé complet. Le classeur automatique d’un résultat à vérifier porte `a-verifier` dans son nom. Un écart conserve son diagnostic ; il n’est pas déclaré égal pour permettre l’utilisation des données.

Le CSV natif garde son nom et ses octets. Les enrichissements et le classeur sont des fichiers distincts. Aucun recodage Windows-1252 ni remplacement automatique des caractères C1 n’est ajouté. Les mécanismes qui exigent un relevé complet conservent cette exigence.

## Demandeurs multiples et textes longs : adaptateur DOM 1.1.2

Les structures publiques GLPI 9.5 et 10 permettent de reproduire des cas précis que la lecture précédente ne traitait pas complètement :

- Des demandeurs multiples disposent de fiches d’information masquées contenant leurs propres sauts de ligne. Ces fiches ne doivent ni être incorporées à la valeur ni empêcher de retrouver la liste complète déjà présente dans la cellule.
- Un texte long est abrégé par `&nbsp;(...)`. Son texte complet est lu dans le popup statique immédiatement associé, après contrôle de la structure et de l’intégralité du préfixe affiché.
- Une cellule de plusieurs textes longs peut recopier le premier aperçu dans un popup extérieur, avec les mêmes identifiants HTML. L’association suit les frères DOM et vérifie leur unicité dans leur parent propriétaire.
- Les séparateurs `hr` préservent une frontière entre les textes. La commande GLPI de lecture supplémentaire est exclue uniquement dans son wrapper reconnu.

Ces lectures n’ajoutent aucun clic ni téléchargement. Un popup non associé, ambigu ou incompatible avec le préfixe reste exclu. Le contenu situé après le premier élément est réellement comparé ; un changement à cet endroit reste détectable.

## Limites des équivalences de présentation

Dans GLPI 10, la liste et le CSV n’utilisent pas toujours le même traitement du texte riche. Par exemple, un export peut représenter l’italique avec des underscores ou convertir du gras en majuscules. Ces conventions ne permettent pas d’effacer globalement la ponctuation ou la casse d’une valeur reçue. La version 3.0.2 conserve les différences résiduelles et leurs réserves, ainsi que les contrôles C1.

Références primaires utilisées pour construire les fixtures synthétiques :

- [GLPI 9.5.13 — aperçus des champs texte](https://github.com/glpi-project/glpi/blob/9.5.13/inc/search.class.php#L6335-L6387) et [suffixe de troncature](https://github.com/glpi-project/glpi/blob/9.5.13/inc/html.class.php#L262-L268).
- [GLPI 10.0.22 — rendus HTML et CSV des champs texte](https://github.com/glpi-project/glpi/blob/10.0.22/src/Search.php#L7635-L7686), [popup multivalué](https://github.com/glpi-project/glpi/blob/10.0.22/src/Search.php#L8889-L8920) et [commande de lecture supplémentaire](https://github.com/glpi-project/glpi/blob/10.0.22/src/RichText/RichText.php#L288-L323).
- [GLPI 10.0.22 — conversion du texte riche](https://github.com/glpi-project/glpi/blob/10.0.22/src/RichText/RichText.php#L103-L174) et [Html2Text 4.3.1 — conventions de présentation](https://github.com/mtibben/html2text/blob/61ad68e934066a6f8df29a3d23a6460536d0855c/src/Html2Text.php#L55-L102).

## Vérification et livraison

Une fixture synthétique reproduit le cas de **788 lignes CSV, 20 identifiants visibles concordants et 32 écarts sur 260 comparaisons**. L’échec du parcours antérieur a été reproduit sur cette fixture, puis le parcours corrigé a réussi localement : 788 lignes disponibles et enrichies pour les villes, classeur nommé `a-verifier`, CSV original et contrôles C1 intacts, `Complete=false` et contrôle final maintenu. Les résultats Windows de 3.0.2 sont ceux du workflow du commit livré.

Les contrôles négatifs couvrent notamment le mauvais échantillon initial, le CSV corrompu, les changements de contexte et les différences après le premier élément d’une cellule multiple. Les résultats et nombres exacts figurent dans les journaux ; aucun succès Windows n’est déduit d’une compilation ou d’un test Chromium local. Voir le [rapport de vérification](Rapport-verification.txt) et le [workflow Windows](https://github.com/M4D135/GLPI-Pilot/actions/workflows/windows.yml).

Les fixtures ne contiennent aucune donnée de tickets du portail privé. Elles ne prouvent ni l’origine de ses 32 écarts ni leur résolution complète. La validation du portail, de son SSO et du poste réel reste distincte.

Les packs PROD et DEV utilisent le même exécutable et ne sont promus qu’après les contrôles du commit correspondant. Extraire tout le ZIP en conservant `wad` à côté de `GLPI-Pilot.exe`. La provenance des sources DEV existe à partir de 3.0.1 ; les instructions de reconstruction figurent dans [GitHub et compilation](GitHub-et-compilation.txt).
