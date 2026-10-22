# Acquisition de la bibliothèque des tickets

Cette suite utilise un vrai Chromium piloté par Playwright. Toutes les requêtes sont interceptées et reçoivent des pages synthétiques ; aucun portail GLPI privé, compte réel, document réel ou donnée utilisateur n’entre dans les fixtures.

## Exécution

Renseigner `GLPI_TEST_CHROMIUM` avec le chemin d’un Chromium installé, puis lancer :

```sh
dotnet run --project tests/TicketLibraryAcquisitionChecks/TicketLibraryAcquisitionChecks.csproj
```

`GLPI_TEST_FILTER` permet de sélectionner un intitulé de contrôle. Le résumé complet attendu est `20/20 contrôles d’acquisition de bibliothèque réussis.`

## Contrat testé

`BrowserService.ReadTicketLibraryIndexAsync(settings, progress, ct, sourceDateOrder = "")` ouvre une page dédiée et observe le formulaire GET natif des tickets. Il utilise l’option `Tous` réellement présente pour le critère de statut, vérifie l’absence d’autre critère et parcourt les liens de pagination observés. L’index contient les tickets résolus et clos, leurs références, les colonnes d’origine, le total constaté et la couverture. Les liens de fiche doivent désigner le même ticket dans la même installation. L’appel existant avec trois arguments reste compatible.

La portée est **tous les statuts hors corbeille, dans les entités actives de la session**. La sélection de toutes les entités n’est pas supposée. Un total qui change, un doublon d’identité, une ligne non identifiée, une limite ou un changement de périmètre empêche `Complete=true`.

La page de recherche affichée, son DOM et son focus sont conservés. Comme GLPI mémorise les recherches dans la session, les champs GET du filtre précédent sont observés avant l’indexation puis restaurés sur une autre page temporaire. Cette restauration possède un budget indépendant de trois secondes en `finally`, y compris après annulation. `SearchRestored` indique sa confirmation ; un échec produit une indication sans prétendre que la session a été restaurée.

`BrowserService.ReadTicketLibraryDetailsAsync(ticket, settings, progress, ct)` réutilise le lecteur de fiche et charge les onglets de consultation reconnus. Il conserve la description, les champs, suivis, tâches, solutions, historique et autres sections lisibles. La pagination d’un onglet n’est parcourue que si le contrôle natif observé, le panneau, la plage et l’identité du ticket concordent. Les actions métier, destinations étrangères, contrôles ambigus et JavaScript non canonique ne sont jamais cliqués.

`TicketLibraryDetailRead.Coverage` expose les nombres d’onglets découverts et entièrement lus, `Complete` et les limites. Un onglet inconnu, contenu masqué, page répétée, chargement non reconnu, compteur incohérent ou section limitée empêche la couverture complète. `ReaderSignature` identifie cette version du lecteur pour la politique de cache.

Les pièces jointes sont conservées comme **métadonnées et liens**. Leur contenu binaire n’est jamais téléchargé automatiquement. Cette limite informative est exposée même lorsque les sections consultables et références ont toutes été lues ; elle ne transforme pas la couverture textuelle en couverture binaire.

## Modification et fraîcheur

Le champ de modification doit être une colonne native `Ticket`, identifiée sans ambiguïté par `data-searchopt-id="19"`. Son texte original est conservé. `LastModifiedReliable` décrit la preuve du champ et d’un format interprétable ; il n’autorise aucun saut de lecture à lui seul.

| Observation | Précision | Instant fourni par l’acquisition |
| --- | --- | --- |
| Date et heure à la minute, format non ambigu | `minutes` | Aucun sans décalage UTC explicite |
| Date et heure avec secondes, format non ambigu | `seconds` | Aucun sans décalage UTC explicite |
| ISO avec secondes et décalage UTC explicite | `seconds` | Instant observé |
| Jour/mois autrement ambigus, ordre `dmy` ou `mdy` explicitement configuré | Précision observée | Aucun sans décalage UTC explicite |
| Champ absent, multiple, masqué, texte libre ou jour/mois ambigus sans ordre choisi | `unknown` | Aucun |

Le fuseau de l’ordinateur n’est pas appliqué par l’acquisition. L’ordre des dates peut être choisi explicitement (`dmy`, `mdy` ou `ymd`) ; vide, il ne résout pas arbitrairement `05/06`. Un ordre configuré ne retombe pas silencieusement sur l’autre ordre et n’autorise ni une date impossible ni une colonne de modification non prouvée. Le scénario des dates exerce les deux ordres sur de vraies pages Chromium. La politique de cache relève du stockage et du coordinateur : fuseau GLPI configuré, intervalle correspondant à la précision, période calme, version du lecteur, couverture précédente et réconciliation périodique. Une date absente ou encore ambiguë exige une nouvelle lecture.

## Sources primaires du protocole

Les règles reposent sur les sources publiques de GLPI 10.0.22, puis sont revérifiées dans le DOM de chaque page :

- [Ticket.php](https://github.com/glpi-project/glpi/blob/10.0.22/src/Ticket.php) : `getDefaultSearchRequest` utilise par défaut `notold` ou `notclosed` ; `getAllStatusArray(true)` expose `all`. Une simple remise à zéro n’inclut donc pas tous les statuts.
- [Search.php](https://github.com/glpi-project/glpi/blob/10.0.22/src/Search.php) : formulaire natif GET, critères, mémorisation de session, remise à zéro et rendu des dates.
- [CommonITILObject.php](https://github.com/glpi-project/glpi/blob/10.0.22/src/CommonITILObject.php) : options de recherche `12` pour le statut et `19` pour `date_mod`.
- [display_data.html.twig](https://github.com/glpi-project/glpi/blob/10.0.22/templates/components/search/display_data.html.twig) et [table.html.twig](https://github.com/glpi-project/glpi/blob/10.0.22/templates/components/search/table.html.twig) : propriétaire `Ticket`, total, départ, limite et identifiants de colonnes.
- [pager.html.twig](https://github.com/glpi-project/glpi/blob/10.0.22/templates/components/pager.html.twig) et [Html.php](https://github.com/glpi-project/glpi/blob/10.0.22/src/Html.php) : pagination native et `printAjaxPager`. `convDateTime` omet les secondes par défaut ; elles ne sont jamais reconstituées.

Les contrôles couvrent aussi les deux formes de succès et d’annulation, les limites configurées, la conservation des colonnes pour les statistiques et l’absence de contenu de ticket dans les journaux généraux.
