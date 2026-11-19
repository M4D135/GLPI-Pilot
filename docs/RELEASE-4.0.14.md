# GLPI Pilot 4.0.14 — opérateur natif de l’identifiant

## Blocage corrigé

Un retour sur la 4.0.13 montre un arrêt de la collecte de bibliothèque à « Préparation du critère Identifiant », après attente sans progression. Le message indique que l’opérateur d’égalité n’a pas été observé. Le journal confirme ensuite la restauration du filtre précédent.

Dans les sources officielles de GLPI, l’identifiant est un champ numérique sans bornes. `Search::getActionsFor` ne lui propose pas `equals` : il donne à l’opérateur interne `contains` le libellé d’égalité « is », traduit par « est ». Une valeur composée uniquement de chiffres est alors comparée numériquement par `=`, sans recherche partielle. Pilot exigeait uniquement `equals` et pouvait attendre une option que GLPI ne produirait jamais. Les précédents scénarios ciblés proposaient tous cette option et ne couvraient pas la variante numérique native.

Sources primaires vérifiées : [définition de l’ID dans GLPI 9.5.13](https://github.com/glpi-project/glpi/blob/9.5.13/inc/commonitilobject.class.php#L3442-L3449), [opérateurs numériques](https://github.com/glpi-project/glpi/blob/9.5.13/inc/search.class.php#L7183-L7204), [comparaison numérique](https://github.com/glpi-project/glpi/blob/9.5.13/inc/search.class.php#L4688-L4736), [traduction française](https://github.com/glpi-project/glpi/blob/9.5.13/locales/fr_FR.po#L14393-L14409). Le même comportement est présent dans [GLPI 10.0.20](https://github.com/glpi-project/glpi/blob/10.0.20/src/Search.php#L8624-L8646).

## Comportement de la correction

La collecte ciblée accepte un unique opérateur `equals`, ou la variante native `contains` lorsque son libellé est exactement « est » ou « is » après normalisation. Elle ne déduit pas le numéro du champ : l’option ID reste observée dans le formulaire. Les contrôles natifs masqués par Select2 restent lisibles ; les contrôles et options désactivés, les doublons et les candidats ambigus sont refusés.

L’opérateur réellement observé est utilisé dans tous les critères du lot, puis conservé pour vérifier chaque ligne de chaque formulaire de résultat. Une réponse modifiant cet opérateur, son libellé d’égalité ou une seule ligne du groupe OU est refusée. La valeur textuelle générique « contient » n’autorise aucune recherche ciblée.

Les identifiants restent validés avant navigation, les requêtes limitées à 25 identifiants, les résultats comparés à la sélection et le périmètre vérifié. Le chemin global Statut = Tous conserve `equals`. La capture et la restauration du filtre précédent, les délais et l’absence de nouvelle tentative automatique sont conservés.

## Qualification et publication vérifiées

La [qualification Windows et PostgreSQL](https://github.com/M4D135/GLPI-Pilot/actions/runs/44556615470), tentative `1`, a réussi ses **16/16 jobs** sur le commit `4457ac5773ad0cec700883e11b4799edd1d44e01`. L’acquisition de bibliothèque réussit **46/46 contrôles**, dont les cinq nouveaux scénarios dédiés à l’égalité numérique en français et en anglais, à Select2, aux lots, aux ambiguïtés et aux changements de formulaire. Les scénarios utilisent des pages synthétiques et ne se connectent pas au portail privé de l’organisation.

La [release v4.0.14](https://github.com/M4D135/GLPI-Pilot/releases/tag/v4.0.14) est publiée le **19 novembre 2026 à 17 h 15 (Europe/Paris)**. Le [run de publication 44563776360](https://github.com/M4D135/GLPI-Pilot/actions/runs/44563776360), tentative `1`, a réussi et vérifié le ZIP original après téléchargement public, sans reconstruction ni recompression. Le [reçu de qualification et publication](QUALIFICATION-4.0.14.md) conserve les résultats détaillés.

- Pack : [GLPI-Pilot_4.0.14_PROD-LIGHT_Windows-x64.zip](https://github.com/M4D135/GLPI-Pilot/releases/download/v4.0.14/GLPI-Pilot_4.0.14_PROD-LIGHT_Windows-x64.zip).
- Taille : `138691673` octets ; **372 fichiers** du manifeste interne vérifiés.
- SHA-256 : `8698d3dcf74df4f1bb97439dd263a0376570491b07dac471429a96c3547a75cc`.

Le tag `v4.0.14` reste sur le commit qualifié ci-dessus. Ce complément documentaire est postérieur au binaire ; le pack conserve les documents de sa construction.

## Mise à jour

Fermer Pilot, extraire tout le pack 4.0.14 publié dans un dossier local et conserver ensemble les deux exécutables et le dossier `wad/`. Les données, les contenus déjà enregistrés et les réglages utilisateur sont conservés. Reprendre ensuite la collecte depuis sa sélection existante.
