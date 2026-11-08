# GLPI Pilot 3.1.0 — interface, sections et contrôles de réception

Cette version poursuit le modèle C# / WinForms / WebView2 de Domenia 2. Elle refond le shell et l’organisation des dashboards, corrige la présentation des tickets et distingue les preuves de réception du rapprochement des valeurs. Les garanties et corrections de la [3.0.2](RELEASE-3.0.2.md) restent conservées.

## Une interface plus lisible

- Navigation complète ou réduite en rail sur grand écran ; tiroir sur fenêtre étroite, fermeture par Échap et retour du focus à la commande d’ouverture.
- Thèmes clair et sombre cohérents, surfaces et titres mieux hiérarchisés, contrôles compacts, focus visible et textes de navigation sur une ligne.
- Transitions d’ouverture, de navigation et de déplacement arrêtées lorsque la réduction des animations est activée dans l’application ou dans le système.
- Liste de tickets avec identifiants entiers sur une ligne, titres conservant l’espace disponible, pagination hors de la zone défilante et onglet actif maintenu visible.
- Suppression des `null` et `undefined` produits par les enfants optionnels des composants. Un texte utilisateur contenant littéralement « null » reste une donnée et n’est pas supprimé.

La suppression des espaces de groupement des identifiants numériques sert à leur présentation. Elle n’utilise pas une conversion numérique susceptible de perdre de grands identifiants ; les valeurs originales et l’identité de la ligne utilisée pour ouvrir le ticket sont conservées.

Les principes de densité, de navigation et d’animation ont été examinés dans les sources de [Domenia 2](https://github.com/M4D135/domenia-d120/tree/4349a0007a75e88559ccb50c968622c6e2cd464e/v2/web). Les traitements métier et la persistance restent en C#.

## Des sections de dashboard indépendantes des widgets

Une section possède désormais sa propre identité, son titre, sa description, sa couleur d’accent, sa visibilité et sa position. Elle subsiste lorsqu’elle est vide. La masquer retire son contenu de la lecture ; le mode édition permet de la retrouver. Le repli et les widgets masqués restent conservés séparément.

En mode **Modifier le dashboard** :

- **+ Section** crée une section ; **Régler la section** permet de la nommer, la décrire, la colorer, la masquer ou choisir sa position.
- Une poignée déplace une section ou un widget ; les zones vides acceptent un widget. Le dialogue de déplacement offre aussi un choix explicite de section et de position.
- Sur la poignée d’un widget, les flèches haut/bas changent l’ordre dans sa section et gauche/droite changent de section. Sur une poignée de section, les flèches changent son ordre.
- La poignée de redimensionnement ajuste la largeur et la hauteur. Au clavier, gauche/droite changent la largeur ; haut/bas changent la hauteur, avec un pas plus grand sous Maj. Le formulaire conserve les dimensions précises et la hauteur automatique. Échap annule un redimensionnement en cours.
- Les actions **Annuler la modification** et **Rétablir la modification** restent disponibles pendant la session. Un widget verrouillé reste protégé des déplacements, du redimensionnement et des actions groupées.

Les anciens tableaux au **format 1** sont repris : les libellés de section existants deviennent des sections explicites, avec conservation des widgets, de leur ordre, des éléments masqués et du repli. Renommer le titre d’une section ne change pas son identité. Les sections vides et leurs réglages participent à la sauvegarde et aux échanges de disposition. Les données de tickets ne font pas partie de ces définitions.

## Réception et concordance sont deux constats distincts

Le bilan distingue désormais les observations suivantes, lorsqu’elles existent :

| Observation | Portée |
| --- | --- |
| Réception des lignes | Export demandé sur toutes les pages, nombre de lignes conforme au total annoncé et absence de contradiction du total final. |
| Identité de l’échantillon | Présence des identifiants examinés lors des contrôles initial et final. |
| Concordance des valeurs | Comparaison des cellules associées sans ambiguïté, avec nombres de différences et d’équivalences de format prouvées. |
| Caractères inhabituels, dont C1 | Comptage des caractères reçus, sans remplacement de leur valeur. |

Une **réception complète** peut donc coexister avec une **concordance à vérifier** ou indisponible. Les lignes, villes, analyses et exports restent utilisables suivant les règles introduites en 3.0.2. Le résultat conserve `Complete=false` tant que les contrôles requis ne sont pas satisfaits ; les exigences de complétude des alertes et historiques ne sont pas assouplies.

Un total final contradictoire empêche de confirmer la réception. Un mauvais échantillon initial d’identifiants ou un CSV corrompu reste refusé. Le contexte de la recherche et le contrôle final gardent leurs propres vérifications. Les anciennes archives sans observations structurées ne reçoivent pas de nouveaux constats à partir du texte de leurs avertissements.

Un relevé de pagination déjà déclaré complet, avec des compteurs cohérents mais sans observations détaillées, affiche « Relevé complet · détail des contrôles indisponible ». Les contrôles d’identifiants et de concordance restent explicitement indisponibles.

## Des équivalences de texte riche prouvées par leur cellule

L’adaptateur DOM **1.1.4** peut fournir une représentation CSV pour un sous-ensemble borné des rendus **GLPI 10.0.22** : gras et titres, italiques, éléments de liste, séparateurs et contenu d’un popup associé. Cette représentation exige d’abord le marqueur officiel de cette version, correspondant au convertisseur étudié ; une autre version ou un marqueur absent ou contradictoire n’autorise pas ces règles. Elle est ensuite liée à la ligne, à la colonne et au texte exact de la cellule lue. Le comparateur exige une correspondance exacte avec le CSV reçu ; une structure non prise en charge n’ajoute aucune équivalence. Les autres versions conservent la collecte native et les règles habituelles de comparaison, avec leurs éventuelles réserves.

Ce traitement explique uniquement les différences de présentation démontrées par le contenu DOM de la cellule. Il ne supprime pas globalement les underscores, les puces, la casse ou la ponctuation. Un mot changé, une preuve d’une autre cellule, un format inconnu ou un contrôle C1 divergent reste un écart. Les données importées et le fichier CSV natif ne sont pas réécrits.

Les fixtures reprennent les traitements publics de [GLPI 10.0.22](https://github.com/glpi-project/glpi/blob/10.0.22/src/Search.php), de [RichText](https://github.com/glpi-project/glpi/blob/10.0.22/src/RichText/RichText.php) et les conventions de [Html2Text 4.3.1](https://github.com/mtibben/html2text/blob/61ad68e934066a6f8df29a3d23a6460536d0855c/src/Html2Text.php). Elles ne prouvent pas la cause ni la résolution de tous les écarts d’un portail privé.

## Vérifications et livraison

Les contrôles portent sur les modules web réels et les opérations C# avec des données synthétiques : absence de texte parasite, identifiants et navigation des tickets, reprise des dispositions, sections vides ou masquées, déplacements, redimensionnement, verrouillages, annulation et états de réception. Les cas négatifs de comparaison restent nécessaires pour vérifier qu’une mise en forme ne masque pas un changement de donnée.

Les nombres exacts et les résultats Windows sont ceux du [workflow du commit livré](https://github.com/M4D135/GLPI-Pilot/actions/workflows/windows.yml) et du [rapport de vérification](Rapport-verification.txt). Un test local ou une compilation ne constitue pas une validation Windows ni un essai sur le portail privé.

Extraire le ZIP complet, avec `wad` à côté de `GLPI-Pilot.exe`. PROD et DEV utilisent le même exécutable. La provenance des sources DEV existe **à partir de 3.0.1** ; la reconstruction hors ligne reste décrite dans [GitHub et compilation](GitHub-et-compilation.txt).
