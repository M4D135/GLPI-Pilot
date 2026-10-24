# Requêtes avancées, référentiel des lieux et tournées

Ce document décrit les pages de la version 3, affichées dans l’hôte WinForms/WebView2. Leurs opérations et leurs calculs s’exécutent dans les moteurs C#. Les traitements portent sur les lignes du relevé extrait. Ils ne donnent pas accès à des tickets absents du portail autorisé et ne modifient aucun ticket dans GLPI.

## Atelier de requêtes

La bibliothèque fournit 80 modèles : les 64 modèles existants et 16 modèles supplémentaires d’âge, de délai, de regroupement et de recherche paramétrée. Le bouton **Packs métier** permet de prévisualiser les modèles par catégorie avant d’ajouter les définitions manquantes, en conservant les requêtes déjà personnalisées. Une requête qui attend une colonne ou un paramètre absent reste indisponible avec sa raison.

Les anciennes conditions ET/OU restent compatibles. Une expression facultative permet, par exemple, **1 ET (2 OU NON 3)**. Les nombres désignent les conditions de la grille. Toutes les conditions doivent être mentionnées ; une condition oubliée ne disparaît pas du filtre silencieusement. La profondeur maximale est de huit niveaux. Aucun SQL, JavaScript ou code arbitraire n’est exécuté.

Les bornes peuvent être des valeurs, des champs ou des paramètres. Les comparaisons numériques et de dates utilisent leur type réel. Les intervalles sont inclusifs. Les choix de IN / NOT IN sont séparés par des points-virgules ou des sauts de ligne ; les virgules restent utilisables dans les noms de personnes.

L’éditeur comprend :

- Les paramètres texte, nombre ou date, avec une valeur par défaut enregistrée et une valeur temporaire utilisée uniquement par l’aperçu.
- Jusqu’à douze champs calculés : addition, soustraction, multiplication, division, durée entre deux dates, ancienneté, longueur Unicode, minuscules, concaténation et remplacement d’une valeur vide.
- Les rubriques **Paramètres nommés**, **Champs calculés autorisés** et **Valeurs de paramètres pour cet aperçu**. Après calcul, **Expliquer la ligne** accepte le numéro de ligne source à partir de 1 ; **Expliquer cette décision** montre les valeurs et les conditions évaluées.

Les calculs sont bornés et les dépendances cycliques sont refusées. Une division par zéro reste inconnue. Une concaténation de plus de 100 000 caractères rend la requête indisponible. Les durées représentent des jours calendaires ; elles ne reconstituent pas les horaires d’ouverture, jours ouvrés ou suspensions de SLA.

Trois niveaux de regroupement peuvent être combinés. Chaque niveau peut utiliser une période de date explicitement choisie : jour, semaine ISO, mois, trimestre ou année. Les valeurs manquantes et dates illisibles restent visibles.

Le résultat propose **Afficher les sous-totaux hiérarchiques** ; décocher cette option affiche les catégories finales. Le tableau paginé déroule les niveaux choisis et affiche le résultat, le nombre de lignes et la taille de l’échantillon numérique de chaque branche. Les commandes **Lignes** et **Contributeurs mesurables** ouvrent le détail d’une branche. Les sous-totaux sont recalculés sur les lignes uniques de chaque préfixe : une moyenne, une médiane ou un nombre distinct n’est jamais obtenu en additionnant les résultats enfants. Le dénominateur d’un pourcentage est la population de ce préfixe avant les conditions. Un ticket affecté à plusieurs techniciens peut appartenir à plusieurs branches ; la somme des branches n’est donc pas nécessairement égale au total général. **Exporter les résultats** enregistre toutes les lignes ou tous les sous-totaux en CSV/XLSX, au-delà de la page affichée.

Les agrégats sont le nombre, le pourcentage, les valeurs distinctes, la somme, la moyenne, le minimum, le maximum, la médiane et un percentile configurable entre 0 et 100. Médiane et percentile utilisent l’interpolation linéaire sur les valeurs numériques finies. Chaque groupe conserve son résultat, le nombre de lignes retenues, son échantillon numérique et ses contributeurs. Le pourcentage d’un groupe utilise sa propre population avant les conditions. Les affectations multiples peuvent faire participer un ticket à plusieurs groupes : ces résultats ne sont pas toujours additionnables.

Une condition illisible reste indéterminée, y compris sous NON. Une branche OU vraie peut décider d’une ligne malgré une autre valeur illisible. Une colonne nécessaire absente ou ambiguë rend toutefois la requête entière indisponible.

### Packs métiers JSON

**Packs métier** regroupe les modèles par catégorie. **Exporter la bibliothèque** enregistre les requêtes mémorisées dans un fichier JSON réutilisable, avec une destination choisie dans le dialogue Windows. Le format 1 contient le nom et la description du pack, sa date d’export et ses définitions de requêtes ; il ne contient aucun résultat ni ligne de ticket. Les valeurs par défaut des paramètres font partie des définitions exportées : relisez-les avant de partager un pack.

**Importer un pack** valide le document, puis présente les ajouts, les requêtes identiques ignorées et les conflits. Une définition différente portant un identifiant déjà présent reçoit un nouvel identifiant et un nom suffixé par « import ». L’aperçu conserve les définitions existantes. **Ajouter ces définitions** confirme leur enregistrement dans la bibliothèque ; aucune requête n’est exécutée à l’import. Ouvrez ensuite chaque définition pour la prévisualiser sur votre relevé. Une bibliothèque modifiée depuis l’aperçu exige une nouvelle comparaison.

La limite est de **2 Mio par fichier** et **128 requêtes dans la bibliothèque résultante**. Un dépassement refuse la fusion entière. Le pack utilise les mêmes règles de validation des champs, opérateurs, paramètres et expressions que l’atelier. Un modèle nécessitant une colonne absente reste indisponible avec une explication.

Sources : [service des packs](../src/GlpiPilot/Core/QueryPackService.cs), [opérations C# de requêtes, import et sous-totaux](../src/GlpiPilot/Runtime/PilotApplication.Analytics.Queries.cs) et [atelier WebView](../web/js/pages/analytics-queries.js).

## Villes, règles et sites

Les noms et alias sont recherchés par mots entiers, sans tenir compte des accents et de la casse. Le dictionnaire peut contenir 1 000 villes, chacune avec douze alias simples et seize règles contextuelles. Le nom de la ville est reconnu automatiquement, sauf si une règle explicite porte sur ce même terme.

Une règle précise :

- Le terme à reconnaître dans le titre.
- Les termes tous requis dans le contexte et les termes tous interdits.
- Une colonne de contexte exacte facultative ; sinon, le contexte est le titre.
- Une priorité explicite comprise entre 0 et 100.
- Une position : n’importe où, au début, à la fin ou à un numéro de mot donné à partir de 1.
- Un format de titre facultatif sous forme d’expression régulière. Il est limité à 500 caractères, à 20 000 caractères de titre analysé et à 30 ms par évaluation ; un dépassement ne confirme pas la règle.

Une exclusion suffit à refuser la règle. Une colonne de contexte absente, vide ou ambiguë ne confirme jamais la ville. Un alias conditionnel remplace l’alias simple portant sur le même terme. Les termes admissibles les plus longs supplantent les termes plus courts qui se chevauchent. Les priorités explicites départagent ensuite les villes ; une égalité reste ambiguë.

Les exclusions au niveau d’une ville refusent cette ville lorsque l’un de leurs termes est présent dans le titre. La région, le département et le secteur peuvent être renseignés et deviennent des colonnes du relevé enrichi. Le champ **Territoire** permet en plus un chemin hiérarchique libre, par exemple `France / Île-de-France / Agences Est`, jusqu’à douze niveaux. La ville s’inscrit dans ce chemin, puis les sites et leurs bâtiments précisent le lieu.

Le référentiel accepte 2 000 sites, rattachés chacun à une ville. Un site possède un nom, des alias, une adresse de référence et des coordonnées facultatives. Latitude et longitude vont ensemble. Aucun géocodage n’est effectué. Un site unique peut identifier sa ville même lorsque le titre ne cite pas le nom de celle-ci. Des références à des villes différentes restent ambiguës. Plusieurs sites d’une même ville conservent la ville reconnue, avec un site précis indéterminé.

Il accepte également **5 000 bâtiments**, chacun rattaché à un site, avec un nom, des alias et un étage ou une zone. La reconnaissance d’un bâtiment retrouve son site et sa ville ; un bâtiment orphelin ou ambigu ne crée pas de localisation certaine. Les sites peuvent aussi fournir un contact, le matériel ou les conditions d’accès, des horaires hebdomadaires et des dates de fermeture.

### Choisir les sources de localisation

La rubrique des sources permet d’ajouter, retirer et ordonner : **correction locale**, **champ GLPI de localisation**, **bâtiment**, **site**, **ville dans le titre**, et **titre complet** combinant les références. Le champ GLPI utilise le nom exact de la colonne configurée. Le premier résultat reconnu ou ambigu arrête la recherche ; une source vide ou sans correspondance laisse décider la suivante. Une ambiguïté prioritaire reste visible au lieu d’être masquée par une autre source.

L’ordre initial conserve les corrections locales, puis la reconnaissance combinée du titre. Si vous placez le champ de localisation avant le titre, il devient prioritaire. Si vous retirez les corrections locales de cet ordre, elles restent mémorisées mais ne décident plus de la localisation. L’ordre et la colonne choisie font partie du référentiel enregistré.

### Vérifier les règles et corriger une ligne

Dans le référentiel, **Tester le brouillon** applique le même moteur que l’extraction à un titre saisi. **Ajouter une colonne de contexte** permet de renseigner séparément son en-tête exact et sa valeur. La preuve indique les termes rencontrés, les règles et les décisions de contexte ; le titre conserve sa graphie d’origine et les passages reconnus sont surlignés. La vérification du dictionnaire signale les termes partagés et les alias très courts.

**Localisations à revoir** présente les lignes non reconnues, ambiguës, sans titre, avec des sites ambigus, ainsi que les corrections locales. **Examiner** ouvre le détail d’une ligne. Choisissez une ville et éventuellement un site, renseignez le **Motif de la correction**, puis utilisez **Enregistrer la correction**. L’action **Retirer la correction locale** rétablit la détection.

Les corrections sont mémorisées uniquement sur le poste, par installation GLPI et ID unique, avec leur date et leur motif. Un ID dupliqué ne reçoit aucune correction arbitraire. Si la ville ou le site d’une correction est retiré du dictionnaire, la ligne redevient à vérifier. Les cellules source sont conservées. Les corrections ne sont pas incluses dans la configuration partagée.

Pour généraliser une correction, renseignez un terme, éventuellement un contexte requis et sa colonne, puis cliquez sur **Ajouter la règle au brouillon du référentiel**. Cette action n’apprend pas automatiquement à partir de tous les tickets et ne modifie pas directement le référentiel appliqué.

**Simuler et enregistrer le référentiel** recalcule l’ensemble du relevé chargé et présente une comparaison avant/après : nombre exact de lignes affectées, anciennes et nouvelles localisations, états et explications. Le détail affiché est limité aux 1 000 premières modifications, mais le total porte sur toutes les lignes. **Enregistrer et appliquer**, dans cet aperçu, confirme le changement. Si le relevé, le brouillon ou son contexte d’interprétation a changé, une nouvelle simulation est nécessaire. Si aucun relevé n’est chargé, le référentiel peut être enregistré sans prétendre avoir mesuré son impact sur des tickets.

### Importer un référentiel

**Exporter le CSV du brouillon…** fournit le modèle de colonnes et les références présentes. **Importer un CSV…** ouvre le dialogue de sélection Windows, puis compare le fichier avant de le fusionner au brouillon. Le comparatif distingue les ajouts, modifications et lignes identiques. Les villes absentes du CSV sont conservées. Une colonne absente conserve sa valeur existante ; une colonne présente mais vide peut l’effacer.

Colonnes reconnues : **Type, Id, VilleId, Nom, Alias, CodePostal, Region, Departement, Secteur, Adresse, Latitude, Longitude, Exclusions, Regles, Territoire, SiteId, Etage, Ouvertures, Fermetures, Contact, Materiel**. Type vaut **Ville**, **Site** ou **Bâtiment**. Nom est obligatoire. Les alias, exclusions et dates de fermeture se séparent par **|** ou **;** à l’intérieur de leur cellule CSV. Les règles et ouvertures sont des listes JSON. Le site référence une ville unique par son identifiant ou son nom ; le bâtiment référence de même un site unique par **SiteId**. Les identifiants exportés permettent de conserver les relations malgré un changement de nom.

L’export CSV commun protège les valeurs susceptibles d’être interprétées comme des formules par un tableur. Le lecteur du référentiel reconnaît cette protection pour les coordonnées négatives. Pour transmettre exactement toute la configuration, notamment des libellés atypiques commençant par un signe de formule, utilisez l’import/export de configuration JSON.

Les villes, sites et bâtiments invalides, relations absentes, horaires incohérents et doublons sont refusés avant de modifier le dictionnaire. Un comparatif devient périmé lorsque le brouillon change ; rechargez alors le CSV. Après **Fusionner dans le brouillon**, utilisez **Simuler et enregistrer le référentiel** et vérifiez l’impact global avant l’application.

Sources : [modèle du référentiel](../src/GlpiPilot/Core/CitySettings.cs), [détection, corrections et comparaisons](../src/GlpiPilot/Core/CityDetectionService.cs), [échanges CSV](../src/GlpiPilot/Core/CityReferenceService.cs), [opérations C# du référentiel et des corrections](../src/GlpiPilot/Runtime/PilotApplication.Terrain.cs), [éditeur WebView](../web/js/pages/terrain-reference.js) et [file de révision](../web/js/pages/terrain-review.js).

### Indicateurs de lieux

Le moteur conserve les indicateurs de couverture et les états exclusifs de reconnaissance. Il ajoute les sites reconnus, sites ambigus, corrections locales et villes reconnues par contexte, ainsi que des répartitions par site, région, département et secteur. Les lignes sans valeur restent dans une catégorie explicite. Les filtres du dashboard s’appliquent aux indices source de ces résultats.

## Tournées datées

La page **Tournées et calendrier** rassemble **Panier et villes**, **Planning daté**, **Tournées et calendrier** et **Routes et météo**.

1. Dans **Panier et villes**, choisissez le périmètre de l’équipe ou du technicien, puis cochez les tickets réellement retenus pour une intervention.
2. Renseignez le nom et le premier jour, puis cliquez sur **Créer une tournée avec ce panier**. Les tickets sont regroupés par site précis lorsqu’il est connu, puis par ville. Les tickets sans lieu restent identifiés dans le panier.
3. Dans **Planning daté**, renseignez le début et la fin de journée. Ajoutez éventuellement des points de départ et de retour à partir du référentiel ou de coordonnées saisies.
4. Pour chaque étape, indiquez le technicien, éventuellement une date propre, la durée d’intervention, le temps de trajet avant l’étape, une heure minimale de début et une heure limite de fin. Vérifiez les compétences requises, contacts, matériel et consignes.
5. Calculez le planning, examinez les attentes, fenêtres dépassées et retours après la fin de journée, puis enregistrez.

Sans service routier configuré, les durées de trajet sont saisies par l’utilisateur et les distances disponibles sont à vol d’oiseau. La proximité propose un ordre glouton indicatif, sans trafic, géocodage ou garantie d’optimalité. Le calcul routier facultatif décrit ci-dessous utilise un fournisseur choisi explicitement ; il ne transforme pas une distance à vol d’oiseau en durée routière supposée.

Une étape verrouillée garde sa position et son affectation dans les propositions automatiques. La reconstruction refuse une modification qui déplacerait, viderait ou changerait le panier d’une étape verrouillée ; déverrouillez-la après vérification. Les réglages d’une étape peuvent être ajustés explicitement dans son éditeur.

### Préparer, valider et clôturer

Une tournée commence à l’état **Brouillon**. **Changer d’état** ouvre le choix du nouvel état et de son motif. L’état **Prête** exige des tickets rapprochés et localisés, un technicien sur chaque étape et un planning compatible avec les fenêtres horaires, les ouvertures connues et la journée déclarée. L’état **Effectuée** exige l’état Prête et refuse une étape encore future. Le retour au brouillon reste explicite ; une modification du planning remet également la tournée en brouillon pour éviter de conserver une validation périmée.

Chaque changement conserve l’état précédent, le nouvel état, sa date et son motif, avec les cent transitions les plus récentes. Le statut « effectuée » est une déclaration locale de préparation et de réalisation ; il ne clôture aucun ticket GLPI.

### Ouvertures des sites

Dans la fiche du site, **Ajouter une plage** permet de choisir le jour et ses heures, par exemple lundi 08:00–12:00 puis lundi 13:00–17:00. Plusieurs plages d’un même jour sont possibles. Les fermetures exceptionnelles utilisent des dates **AAAA-MM-JJ**. L’intervention entière doit tenir dans une plage : si elle ne tient pas avant une fermeture de midi, le calcul attend la plage suivante. Un jour sans ouverture dans un calendrier renseigné, une fermeture explicite ou une durée impossible produit un signal d’incompatibilité.

Si aucun horaire n’est déclaré pour un site, aucune contrainte d’ouverture n’est inventée. Vérifiez cette information avec le contact du site avant de valider la tournée. Le planning distingue trajet, intervention et attente, pour chaque couple date/technicien.

### Répartir et comparer

**Répartir sur plusieurs jours** prépare une proposition sur **un à quatorze jours**, pour **un à trente-deux techniciens**, à partir du premier jour de la tournée. Choisissez les techniciens, utilisez **Consulter les capacités**, puis **Calculer la proposition dans le brouillon**.

Ce calcul utilise les compétences déclarées, les absences, les plages de présence et les capacités journalières, du matin et de l’après-midi des fiches techniciens. Il tient compte des fenêtres d’intervention, des ouvertures et fermetures de sites, de l’attente, des trajets et du retour. Une capacité non déclarée est exclue de l’allocation automatique ; une compétence requise ne devient pas acquise par défaut. Les étapes verrouillées gardent leur date et leur technicien ; si leurs contraintes sont déjà incompatibles, le calcul refuse de les déplacer silencieusement.

Le trajet vers une étape et son intervention doivent tenir dans une même plage de présence. Une présence déclarée uniquement l’après-midi reporte le départ ; une pause entre deux plages n’est pas comptée comme attente sur site. Le retour doit lui aussi respecter une plage et son budget. Une exception de capacité augmente ou réduit le budget de sa demi-journée, mais n’invente pas une présence sur un jour sans plage dans un calendrier renseigné. Sans calendrier récurrent, les horaires de la tournée bornent le calcul ; les horaires de présence restent inconnus. Les disponibilités utilisées sont conservées avec le scénario et les fiches techniciens sont relues lors du recalcul et de la validation dans l’interface.

Les échéances connues donnent l’ordre de priorité des étapes. L’algorithme recherche ensuite un créneau compatible, en privilégiant les journées et en équilibrant la capacité utilisée ; les territoires déclarés servent de préférence. Il conserve l’ordre final des étapes pour vérifier les horaires. **C’est une heuristique, sans garantie de solution globale optimale** : une étape peut rester sans affectation alors qu’un réarrangement manuel permettrait de la placer. Les étapes sans créneau portent une explication et demandent une décision humaine. Les affectations GLPI restent indépendantes de cette proposition locale.

**Dupliquer** copie la tournée avec une nouvelle identité. **Comparer deux tournées** présente tickets, étapes, intervention, trajets, attente, fenêtres dépassées, sites fermés, échéances dépassées et échéances inconnues. Les kilomètres routiers ne sont comparables que si tous les segments des parcours ont été calculés avec des coordonnées toujours valables. Une valeur manquante reste inconnue. Des paniers différents doivent être interprétés comme des charges différentes.

Le registre local accepte cent tournées, deux mille tickets et cinq cents étapes par tournée. **Retirer du registre** retire la copie mémorisée en laissant le brouillon affiché pour pouvoir l’enregistrer de nouveau. L’enregistrement est atomique. Le fichier `scheduled-tours.json` contient les ID sélectionnés, références de lieux, empreintes de contrôle, horaires, états, contacts, matériel et consignes, ainsi que les résultats routiers ou météo enregistrés. Il n’enregistre pas automatiquement les titres de tickets et ne fait pas partie de la configuration d’équipe. Les contacts et consignes peuvent contenir des informations de terrain : partagez le briefing uniquement avec les personnes prévues.

Les tournées enregistrées du jour sont aussi accessibles dans **Mon espace**, avec leurs ID de tickets, leur état et leur technicien. Ce raccord utilise les dates propres aux étapes, et non seulement la date de début de la tournée.

### Routes et météo facultatives

Dans l’onglet **Routes et météo**, renseignez une adresse de service autorisé. Les deux adresses sont **vides par défaut**. Le bouton **Enregistrer les adresses** les conserve localement dans `terrain-services.json`. La case d’autorisation d’envoi des coordonnées et des dates est liée aux adresses saisies et à la demande ; elle est réinitialisée après enregistrement, changement d’adresse ou calcul réussi. Chaque demande nécessite un clic sur le calcul souhaité ; aucune transmission automatique n’est déclenchée par l’ouverture d’une tournée.

Pour le routage, renseignez l’adresse de base d’un serveur compatible **OSRM v5**, par exemple une instance interne, et le profil qu’il accepte, initialement `driving`. Le client demande le parcours dans l’ordre des étapes et utilise les distances et durées de chaque segment reçues du serveur. Il transmet les coordonnées utiles, pas les titres, ID de tickets, noms de techniciens ou identifiants GLPI. Les segments restent liés au jour, au technicien, à l’ordre et aux coordonnées : une modification invalide les résultats concernés. Les durées saisies redeviennent alors applicables et le planning signale le calcul périmé. La source et la date du calcul sont conservées.

Pour la météo, renseignez l’adresse complète d’un point d’accès de prévisions quotidiennes compatible **Open-Meteo**. Les demandes portent sur les coordonnées et la date de l’étape. Le résultat présente le code météo du fournisseur, les températures minimale et maximale, la probabilité maximale de précipitations quand elle existe, la source et la date de consultation. Une absence de prévision, une date hors couverture ou une réponse inexploitable ne devient pas une météo inventée. Les valeurs manquantes restent inconnues ; la fiche terrain n’utilise plus une prévision après changement de la date ou des coordonnées de l’étape.

Les adaptateurs acceptent HTTP ou HTTPS selon le service explicitement choisi, sans identifiant dans l’URL, paramètre secret ou clé intégrée au programme. Utilisez un point d’accès interne ou un fournisseur dont les conditions autorisent cet usage ; l’interface ne gère pas les services imposant une clé dans la requête. Les redirections sont refusées. Les réponses sont bornées à 2 Mio, les requêtes à 30 secondes et une opération complète à deux minutes, avec annulation. Un échec conserve le brouillon précédent ; il n’applique pas une réponse partielle.

Sources : [service de planning et exports](../src/GlpiPilot/Core/ScheduledTourService.cs), [états, contraintes et comparaison](../src/GlpiPilot/Core/ScheduledTourPlanning.cs), [adaptateurs HTTP](../src/GlpiPilot/Core/TourExternalServices.cs), [opérations C# des tournées](../src/GlpiPilot/Runtime/PilotApplication.TerrainTours.cs) et [page WebView](../web/js/pages/terrain-tours.js). Protocoles des fournisseurs : [OSRM](https://project-osrm.org/docs/v5.24.0/api/) et [Open-Meteo](https://open-meteo.com/en/docs).

### Rapprocher un nouveau relevé

Le rapprochement utilise l’installation GLPI et l’ID unique, jamais l’ancienne position d’une ligne. Il détecte notamment les tickets résolus depuis la préparation, changements de lieu, titre, statut, priorité, échéance ou affectation.

Un ticket absent du relevé courant n’est pas déclaré supprimé dans GLPI. Une source partielle reste signalée. Des ID dupliqués ne sont pas rapprochés arbitrairement et une autre installation GLPI ne fournit aucune information aux tickets mémorisés.

**Actualiser les étapes depuis le relevé** ouvre un aperçu des règles de reconstruction, puis **Actualiser le brouillon** reconstruit explicitement les lieux, en conservant les réglages des étapes restantes et les verrous. L’option de retrait des résolus ou clos est explicite. Des références absentes ou ambiguës bloquent cette reconstruction. Les tickets nouveaux dans le relevé ne sont pas ajoutés automatiquement à un panier préparé.

### Calendrier et briefing

L’export ICS crée un événement par étape, avec une identité stable, des dates UTC calculées à partir du fuseau de la tournée et les ID des tickets en description. Les horaires ambigus ou inexistants lors d’un changement d’heure sont refusés. Le format utilise des lignes CRLF, un pliage de 75 octets respectant l’UTF-8 et l’échappement des textes définis par la [RFC 5545](https://www.rfc-editor.org/rfc/rfc5545.html).

Un lieu modifié, une référence absente, un ticket sans localisation ou un ID non rapproché empêchent l’export calendrier. L’import du fichier dans un agenda reste une action de l’utilisateur ; aucune invitation n’est envoyée.

Le briefing HTML contient le planning, les tickets retrouvés dans le relevé actuel, les contacts, le matériel ou les accès à prévoir, les consignes et les écarts de rapprochement. À la création, les champs de contact et matériel sont recueillis depuis les colonnes reconnues du relevé et le référentiel du site ; ils restent modifiables dans la fiche d’étape et une valeur absente reste « Non renseigné ». Une prévision météo correspondant encore à la date et aux coordonnées peut compléter cette fiche, avec sa source et sa date de consultation.

Les textes source sont encodés pour ne pas exécuter de contenu HTML. Son bouton d’impression permet l’impression ou l’enregistrement PDF via le navigateur. Cet export de tournée est un document HTML imprimable ; aucun navigateur n’est ouvert automatiquement par l’export.

## Vérifications

Les suites BCL validées comprennent **99/99 contrôles de requêtes**, **91/91 contrôles de lieux** et **87/87 contrôles de tournées**. Elles utilisent des données synthétiques et aucun portail privé. Les contrôles de routage et météo injectent des réponses HTTP de test : absence de consentement, hôtes configurés, annulation, bornes, erreurs, données manquantes et invalidation des résultats périmés. Ils ne constituent pas une validation d’un fournisseur externe dans votre environnement.

`TerrainBridgeChecks` vérifie les opérations C# réellement exposées aux pages : versions de brouillon, simulation complète, corrections motivées, imports, paniers, verrous, disponibilité et consentement. Les **18/18 parcours de TerrainWebChecks** utilisent les modules de production dans Chromium, reliés à cette fixture C# ; ils manipulent les formulaires et leurs résultats persistés. `AnalyticsBridgeChecks` complète la couverture de l’atelier, des packs, des sous-totaux et des exports.

`WindowsStartupChecks` lance le vrai pack WinForms/WebView2 avec son runtime fixe et un profil temporaire : premier affichage, navigation des pages, thèmes, zoom natif, instance unique et fermeture. Cette exécution Windows est distincte des tests C# et Chromium ; sa réussite doit être établie par le [rapport](Rapport-verification.txt) et les journaux du commit livré.

Suites : [requêtes](../tests/QueryChecks), [détection géographique](../tests/CityDetectionChecks), [tournées et fournisseurs simulés](../tests/TourPlanningChecks), [opérations terrain C#](../tests/TerrainBridgeChecks), [pages terrain](../tests/TerrainWebChecks), [atelier et analyses](../tests/AnalyticsBridgeChecks), [protocole Windows](../tests/WindowsStartupChecks/README.md).
