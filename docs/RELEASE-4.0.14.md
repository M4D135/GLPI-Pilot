# GLPI Pilot 4.0.14 — opérateur natif de l’identifiant

## Blocage corrigé

Un retour sur la 4.0.13 montre un arrêt de la collecte de bibliothèque à « Préparation du critère Identifiant », après attente sans progression. Le message indique que l’opérateur d’égalité n’a pas été observé. Le journal confirme ensuite la restauration du filtre précédent.

Dans les sources officielles de GLPI, l’identifiant est un champ numérique sans bornes. `Search::getActionsFor` ne lui propose pas `equals` : il donne à l’opérateur interne `contains` le libellé d’égalité « is », traduit par « est ». Une valeur composée uniquement de chiffres est alors comparée numériquement par `=`, sans recherche partielle. Pilot exigeait uniquement `equals` et pouvait attendre une option que GLPI ne produirait jamais. Les précédents scénarios ciblés proposaient tous cette option et ne couvraient pas la variante numérique native.

Sources primaires vérifiées : [définition de l’ID dans GLPI 9.5.13](https://github.com/glpi-project/glpi/blob/9.5.13/inc/commonitilobject.class.php#L3442-L3449), [opérateurs numériques](https://github.com/glpi-project/glpi/blob/9.5.13/inc/search.class.php#L7183-L7204), [comparaison numérique](https://github.com/glpi-project/glpi/blob/9.5.13/inc/search.class.php#L4688-L4736), [traduction française](https://github.com/glpi-project/glpi/blob/9.5.13/locales/fr_FR.po#L14393-L14409). Le même comportement est présent dans [GLPI 10.0.20](https://github.com/glpi-project/glpi/blob/10.0.20/src/Search.php#L8624-L8646).

## Comportement de la correction

La collecte ciblée accepte un unique opérateur `equals`, ou la variante native `contains` lorsque son libellé est exactement « est » ou « is » après normalisation. Elle ne déduit pas le numéro du champ : l’option ID reste observée dans le formulaire. Les contrôles natifs masqués par Select2 restent lisibles ; les contrôles et options désactivés, les doublons et les candidats ambigus sont refusés.

L’opérateur réellement observé est utilisé dans tous les critères du lot, puis conservé pour vérifier chaque ligne de chaque formulaire de résultat. Une réponse modifiant cet opérateur, son libellé d’égalité ou une seule ligne du groupe OU est refusée. La valeur textuelle générique « contient » n’autorise aucune recherche ciblée.

Les identifiants restent validés avant navigation, les requêtes limitées à 25 identifiants, les résultats comparés à la sélection et le périmètre vérifié. Le chemin global Statut = Tous conserve `equals`. La capture et la restauration du filtre précédent, les délais et l’absence de nouvelle tentative automatique sont conservés.

## Qualification

La suite d’acquisition comporte 46 contrôles, dont cinq nouveaux scénarios dédiés à l’égalité numérique en français et en anglais, aux lots, aux ambiguïtés et aux changements de formulaire. Les scénarios utilisent des pages synthétiques et ne se connectent pas au portail privé de l’organisation.

La publication du pack exige la réussite de la qualification complète Windows et PostgreSQL sur le commit exact, puis l’inspection et la promotion de son ZIP original. La compilation et l’ajout des scénarios ne constituent pas à eux seuls une qualification ou une publication. Les résultats et l’identité du pack seront consignés après exécution.

## Mise à jour après publication

Fermer Pilot, extraire tout le pack 4.0.14 publié dans un dossier local et conserver ensemble les deux exécutables et le dossier `wad/`. Les données, les contenus déjà enregistrés et les réglages utilisateur sont conservés. Reprendre ensuite la collecte depuis sa sélection existante.
