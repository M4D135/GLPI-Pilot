# GLPI Pilot 3.1.1 — dashboard adapté aux données et diagnostic des écarts

Cette version corrige la présentation des champs indisponibles et des réserves d’import. Elle ajoute un diagnostic local des cellules qui diffèrent entre la liste GLPI et le CSV reçu, pour examiner leur cause à partir des valeurs réellement comparées.

## Dashboard et champs facultatifs

Les widgets sans données calculables sont masqués par défaut en lecture. Leurs réglages et leur position restent enregistrés. **Modifier le dashboard** les réaffiche avec une explication ; **Afficher les widgets indisponibles** permet de les conserver visibles. Ce choix est mémorisé par tableau, participe à l’annulation/rétablissement et aux transferts de configuration. Un indicateur calculé à zéro reste visible. L’historique différé n’est pas masqué pendant son chargement.

Le widget **Qualité** affiche un résumé des champs reconnus et de la réception. Les correspondances et les champs facultatifs absents sont repliés, avec des libellés français. Les mots techniques `missing` et `auto` ne servent plus de libellés. Les champs absents ne sont pas présentés comme des cellules perdues lors de l’import ; une correspondance ambiguë ou configurée mais absente reste identifiable.

La météo décrit les indicateurs disponibles, leurs échantillons et leurs pondérations. Les termes absents sont exclus du calcul, sans être remplacés par zéro. Les états prudents et les conditions de validation des historiques restent conservés.

## Observations consultables sans bandeau répété

La réception des lignes figure dans le bilan de collecte. Les différences de présentation et les caractères C1 ne produisent plus un grand bandeau répété dans le dashboard, l’accueil et le relevé. **Détails des contrôles** et **Qualité du relevé** permettent de consulter les observations. Une réception incomplète, un total contradictoire, un problème d’identifiants ou un périmètre non confirmé reste signalé.

Le résultat strict `Complete`, les vérifications d’identité, de nombre de lignes et de contexte, les conditions des alertes métier et les règles de comparaison historique ne sont pas assouplis. Le CSV natif reste intact. Une collecte reçue avec un périmètre stable peut être indiquée comme terminée dans les opérations tout en conservant des réserves de concordance dans ses détails.

## Journal local des cellules divergentes

Lors d’une **nouvelle collecte native**, chaque comparaison réellement divergente peut alimenter un fichier JSON dédié. Les équivalences déjà reconnues par le comparateur ne sont pas enregistrées comme des erreurs. Le document comprend :

- le contrôle initial ou final, l’identifiant rapproché et les positions de ligne/colonne dans chaque source ;
- les en-têtes, le rôle reconnu et les valeurs brutes de l’affichage et du CSV ;
- la catégorie de différence, les longueurs, la première position différente dans les textes bruts et les codes Unicode voisins ;
- des pistes descriptives, par exemple la présence de contrôles C1, d’espaces particuliers ou de notation HTML, sans supposer une cause ni appliquer une conversion.

**Qualité du relevé → Voir les cellules divergentes** présente les valeurs côte à côte. **Exporter le diagnostic des écarts** enregistre un JSON à l’emplacement choisi. Le fichier local d’origine se trouve dans `%LOCALAPPDATA%\GLPI-Pilot\Diagnostics\Import`. Il contient des valeurs métier et des identifiants de tickets. Le journal général et le diagnostic ordinaire conservent uniquement des compteurs et l’indication de disponibilité ; aucune transmission automatique n’est ajoutée.

Le rapport retient au maximum **80 comparaisons**, avec un préfixe de **4 096 unités UTF-16 par valeur**, une fenêtre de **160 unités** autour du premier écart et jusqu’à **24 codes Unicode** voisins. La recherche du premier écart est bornée à **1 048 576 unités UTF-16** ; un fichier lu ne dépasse pas **8 Mio**. Les troncatures sont explicites. Les lignes et colonnes du JSON sont numérotées à partir de 0 ; l’interface les présente à partir de 1. Les positions de texte restent en UTF-16, base 0. Les contrôles initial et final sont conservés séparément : un même écart peut donc figurer deux fois.

La lecture impose un nom de fichier contrôlé, un document borné et une liaison au `RunId` du relevé affiché. Les opérations exigent la bonne révision de données. Un échec d’écriture du diagnostic n’annule pas un import déjà exploitable. Les archives anciennes n’acquièrent pas rétroactivement les valeurs de comparaison ; une nouvelle collecte est nécessaire.

## Vérifications et distribution

Les régressions utilisent exclusivement des données synthétiques. Elles couvrent les widgets indisponibles et les zéros valides, la préférence persistée et son annulation, les libellés et détails repliés, la visibilité des vrais défauts de réception, les paires de cellules exactes, les caractères Unicode, les limites de capture, les lectures/exportations du bon relevé et l’absence des valeurs dans le diagnostic général.

Les résultats Windows et les packs correspondent au [workflow du commit livré](https://github.com/M4D135/GLPI-Pilot/actions/workflows/windows.yml). La compilation locale et les fixtures ne prouvent pas la cause des divergences d’un portail privé. Le rapport de la prochaine collecte doit fournir les valeurs nécessaires à cette analyse.

Les packs **PROD** et **DEV** contiennent le même exécutable. Extraire le ZIP complet et conserver `wad` à côté de `GLPI-Pilot.exe`. Les configurations personnelles et le profil de connexion restent dans leurs emplacements habituels.
