# Requêtes avancées, référentiel des lieux et tournées

Ce document décrit les fonctions effectivement présentes dans cette version. Les traitements portent sur les lignes du relevé extrait. Ils ne donnent pas accès à des tickets absents du portail autorisé et ne modifient aucun ticket dans GLPI.

## Atelier de requêtes

La bibliothèque fournit 80 modèles : les 64 modèles existants et 16 modèles supplémentaires d’âge, de délai, de regroupement et de recherche paramétrée. Le bouton d’ajout des modèles manquants conserve les requêtes déjà personnalisées. Une requête qui attend une colonne ou un paramètre absent reste indisponible avec sa raison.

Les anciennes conditions ET/OU restent compatibles. Une expression facultative permet, par exemple, **1 ET (2 OU NON 3)**. Les nombres désignent les conditions de la grille. Toutes les conditions doivent être mentionnées ; une condition oubliée ne disparaît pas du filtre silencieusement. La profondeur maximale est de huit niveaux. Aucun SQL, JavaScript ou code arbitraire n’est exécuté.

Les bornes peuvent être des valeurs, des champs ou des paramètres. Les comparaisons numériques et de dates utilisent leur type réel. Les intervalles sont inclusifs. Les choix de IN / NOT IN sont séparés par des points-virgules ou des sauts de ligne ; les virgules restent utilisables dans les noms de personnes.

L’onglet **Avancé** comprend :

- Les paramètres texte, nombre ou date, avec une valeur par défaut enregistrée et une valeur temporaire utilisée uniquement par l’aperçu.
- Jusqu’à douze champs calculés : addition, soustraction, multiplication, division, durée entre deux dates, ancienneté, longueur Unicode, minuscules, concaténation et remplacement d’une valeur vide.
- L’explication d’un ticket par son ID unique, ou par **#n** pour sa ligne source à partir de 1.

Les calculs sont bornés et les dépendances cycliques sont refusées. Une division par zéro reste inconnue. Une concaténation de plus de 100 000 caractères rend la requête indisponible. Les durées représentent des jours calendaires ; elles ne reconstituent pas les horaires d’ouverture, jours ouvrés ou suspensions de SLA.

Trois niveaux de regroupement peuvent être combinés. Chaque niveau peut utiliser une période de date explicitement choisie : jour, semaine ISO, mois, trimestre ou année. Les valeurs manquantes et dates illisibles restent visibles.

Les agrégats sont le nombre, le pourcentage, les valeurs distinctes, la somme, la moyenne, le minimum, le maximum, la médiane et un percentile configurable entre 0 et 100. Médiane et percentile utilisent l’interpolation linéaire sur les valeurs numériques finies. Chaque groupe conserve son résultat, le nombre de lignes retenues, son échantillon numérique et ses contributeurs. Le pourcentage d’un groupe utilise sa propre population avant les conditions. Les affectations multiples peuvent faire participer un ticket à plusieurs groupes : ces résultats ne sont pas toujours additionnables.

Une condition illisible reste indéterminée, y compris sous NON. Une branche OU vraie peut décider d’une ligne malgré une autre valeur illisible. Une colonne nécessaire absente ou ambiguë rend toutefois la requête entière indisponible.

## Villes, règles et sites

Les noms et alias sont recherchés par mots entiers, sans tenir compte des accents et de la casse. Le dictionnaire peut contenir 1 000 villes, chacune avec douze alias simples et seize règles contextuelles. Le nom de la ville est reconnu automatiquement, sauf si une règle explicite porte sur ce même terme.

Une règle précise :

- Le terme à reconnaître dans le titre.
- Les termes tous requis dans le contexte et les termes tous interdits.
- Une colonne de contexte exacte facultative ; sinon, le contexte est le titre.
- Une priorité explicite comprise entre 0 et 100.

Une exclusion suffit à refuser la règle. Une colonne de contexte absente, vide ou ambiguë ne confirme jamais la ville. Un alias conditionnel remplace l’alias simple portant sur le même terme. Les termes admissibles les plus longs supplantent les termes plus courts qui se chevauchent. Les priorités explicites départagent ensuite les villes ; une égalité reste ambiguë.

Les exclusions au niveau d’une ville refusent cette ville lorsque l’un de leurs termes est présent dans le titre. La région, le département et le secteur peuvent être renseignés et deviennent des colonnes du relevé enrichi.

Le référentiel accepte 2 000 sites, rattachés chacun à une ville. Un site possède un nom, des alias, une adresse de référence et des coordonnées facultatives. Latitude et longitude vont ensemble. Aucun géocodage n’est effectué. Un site unique peut identifier sa ville même lorsque le titre ne cite pas le nom de celle-ci. Des références à des villes différentes restent ambiguës. Plusieurs sites d’une même ville conservent la ville reconnue, avec un site précis indéterminé.

### Vérifier les règles et corriger une ligne

**Simuler et expliquer** applique le même moteur que l’extraction à un titre saisi, avec des contextes facultatifs au format **En-tête exact=valeur**. La preuve indique les termes rencontrés, les règles et les décisions de contexte. La vérification du dictionnaire signale les termes partagés et les alias très courts.

**Lieux à vérifier** présente les lignes non reconnues, ambiguës, sans titre, avec des sites ambigus, ainsi que les corrections locales. Choisissez une ville et éventuellement un site pour corriger la ligne. L’action **Revenir à la détection** retire la correction.

Les corrections sont mémorisées uniquement sur le poste, par installation GLPI et ID unique. Un ID dupliqué ne reçoit aucune correction arbitraire. Si la ville ou le site d’une correction est retiré du dictionnaire, la ligne redevient à vérifier. Les cellules source sont conservées. Les corrections ne sont pas incluses dans la configuration partagée.

### Importer un référentiel

**Référentiel CSV** exporte le modèle de colonnes et permet de comparer un CSV avant de l’appliquer au brouillon. Le comparatif distingue les ajouts, modifications et lignes identiques. Les villes absentes du CSV sont conservées. Une colonne absente conserve sa valeur existante ; une colonne présente mais vide peut l’effacer.

Colonnes reconnues : **Type, Id, VilleId, Nom, Alias, CodePostal, Region, Departement, Secteur, Adresse, Latitude, Longitude, Exclusions, Regles**. Type vaut Ville ou Site. Nom est obligatoire. Les alias et exclusions se séparent par **|** ou **;** à l’intérieur de leur cellule CSV. Les règles sont représentées dans une cellule JSON. Le site référence une ville unique par son identifiant ou son nom. Les identifiants exportés permettent de conserver les relations malgré un changement de nom.

L’export CSV commun protège les valeurs susceptibles d’être interprétées comme des formules par un tableur. Le lecteur du référentiel reconnaît cette protection pour les coordonnées négatives. Pour transmettre exactement toute la configuration, notamment des libellés atypiques commençant par un signe de formule, utilisez l’import/export de configuration JSON.

Les villes et sites invalides, relations absentes et doublons sont refusés avant de modifier le dictionnaire. Un comparatif devient périmé lorsque le brouillon change ; rechargez alors le CSV. Cliquez ensuite sur **Enregistrer et appliquer au relevé** pour recalculer les villes et tableaux de bord.

### Indicateurs de lieux

Le moteur conserve les indicateurs de couverture et les états exclusifs de reconnaissance. Il ajoute les sites reconnus, sites ambigus, corrections locales et villes reconnues par contexte, ainsi que des répartitions par site, région, département et secteur. Les lignes sans valeur restent dans une catégorie explicite. Les filtres du dashboard s’appliquent aux indices source de ces résultats.

## Tournées datées

L’onglet **Villes et tournées → Tournées datées** complète le plan par villes existant.

1. Dans **Panier**, cochez les tickets réellement retenus pour une intervention. Le périmètre provient du plan d’équipe ou du technicien sélectionné.
2. Cliquez sur **Préparer les tickets cochés**. Les tickets sont regroupés par site précis lorsqu’il est connu, puis par ville. Les tickets sans lieu restent identifiés dans le panier.
3. Renseignez le nom, la date, le début et la fin de journée. Ajoutez éventuellement des points de départ et de retour à partir du référentiel ou de coordonnées saisies.
4. Pour chaque étape, indiquez le technicien, la durée d’intervention, le temps de trajet avant l’étape, une heure minimale de début et une heure limite de fin. Ajoutez les consignes utiles.
5. Calculez le planning, examinez les attentes, fenêtres dépassées et retours après la fin de journée, puis enregistrez.

Les durées de trajet sont saisies par l’utilisateur. Les distances sont à vol d’oiseau lorsque les coordonnées sont disponibles. La proximité propose un ordre glouton indicatif ; elle ne fournit ni itinéraire routier, trafic, géocodage ou garantie d’optimalité. Aucun fournisseur de routage externe n’est configuré dans cette version.

Une étape verrouillée garde sa position et son affectation dans les propositions automatiques. La reconstruction refuse une modification qui déplacerait, viderait ou changerait le panier d’une étape verrouillée ; déverrouillez-la après vérification. Les réglages d’une étape peuvent être ajustés explicitement dans son éditeur.

### Répartir et comparer

**Répartition et scénarios** propose une répartition entre un à trente-deux techniciens sélectionnés, en équilibrant les minutes d’intervention et de trajet saisies. Les étapes verrouillées sont conservées. Cette proposition n’intègre pas automatiquement les compétences, absences ou capacités définies ailleurs, et ne change pas les affectations GLPI.

**Créer un scénario** copie la tournée avec une nouvelle identité. Le comparatif présente les tickets, étapes, minutes et fenêtres dépassées des deux scénarios. Des paniers différents doivent être interprétés comme des charges différentes.

Le registre local accepte cent tournées, deux mille tickets et cinq cents étapes par tournée. **Retirer du registre** retire la copie mémorisée en laissant le brouillon affiché pour pouvoir l’enregistrer de nouveau. L’enregistrement est atomique. Le registre contient les ID sélectionnés, références de lieux, empreintes de contrôle, horaires et consignes ; il n’enregistre pas automatiquement les titres de tickets et ne fait pas partie de la configuration d’équipe.

### Rapprocher un nouveau relevé

Le rapprochement utilise l’installation GLPI et l’ID unique, jamais l’ancienne position d’une ligne. Il détecte notamment les tickets résolus depuis la préparation, changements de lieu, titre, statut, priorité, échéance ou affectation.

Un ticket absent du relevé courant n’est pas déclaré supprimé dans GLPI. Une source partielle reste signalée. Des ID dupliqués ne sont pas rapprochés arbitrairement et une autre installation GLPI ne fournit aucune information aux tickets mémorisés.

**Actualiser les étapes déverrouillées** reconstruit explicitement les lieux depuis le nouveau relevé, en conservant les réglages des étapes restantes. L’option de retrait des résolus ou clos est explicite. Des références absentes ou ambiguës bloquent cette reconstruction. Les tickets nouveaux dans le relevé ne sont pas ajoutés automatiquement à un panier préparé.

### Calendrier et briefing

L’export ICS crée un événement par étape, avec une identité stable, des dates UTC calculées à partir du fuseau de la tournée et les ID des tickets en description. Les horaires ambigus ou inexistants lors d’un changement d’heure sont refusés. Le format utilise des lignes CRLF, un pliage de 75 octets respectant l’UTF-8 et l’échappement des textes définis par la [RFC 5545](https://www.rfc-editor.org/rfc/rfc5545.html).

Un lieu modifié, une référence absente, un ticket sans localisation ou un ID non rapproché empêchent l’export calendrier. L’import du fichier dans un agenda reste une action de l’utilisateur ; aucune invitation n’est envoyée.

Le briefing HTML contient le planning, les tickets retrouvés dans le relevé actuel, les consignes et les écarts de rapprochement. Les textes source sont encodés pour ne pas exécuter de contenu HTML. Son bouton d’impression permet l’impression ou l’enregistrement PDF via le navigateur. Il ne s’agit pas d’un générateur PDF natif. Aucun navigateur n’est ouvert automatiquement par cet export.

## Vérifications

Les suites BCL passent avec **91/91 contrôles de requêtes**, **75/75 contrôles de lieux** et **59/59 contrôles de tournées**. Elles utilisent des données synthétiques et aucun portail privé.

Les nouveaux scénarios WPF de WindowsStartupChecks passent par les contrôles réels de l’atelier de requêtes, la simulation des villes, les événements de correction, le panier daté, l’édition de durée avant et après sauvegarde et les scénarios. Leur exécution Windows constitue une vérification distincte des tests du moteur et de la compilation croisée.
