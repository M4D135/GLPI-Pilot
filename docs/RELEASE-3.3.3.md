# GLPI Pilot 3.3.3 — Simulation, sites et tournées

Cette version complète le travail en simulation à partir d’un CSV, simplifie le menu et rétablit la continuité entre sites reconnus, communes et tournées. Elle corrige aussi les aperçus de lignes et les compteurs de localisation. Le filtre existant permet de cumuler plusieurs équipes et groupes.

## Tout essayer depuis un import CSV

**Importer en simulation** crée un espace de travail à partir des seules lignes du fichier. Les 35 écrans restent accessibles, y compris les écrans masqués par défaut dans le menu, que la palette **Ctrl+K** permet d’ouvrir. Les tableaux de bord, la météo, les tickets, les agents, la bibliothèque, les sites, les tournées, les archives et les exports utilisent ce même relevé.

Les réglages du profil habituel sont repris au début de la simulation, notamment les correspondances et le référentiel géographique. Les modifications faites pendant l’essai restent dans cet espace. **Quitter la simulation** restitue le contexte habituel. Les exports enregistrés explicitement peuvent être conservés.

Les **effectifs des sites** peuvent maintenant être saisis, datés, corrigés et supprimés en simulation. Les ratios du relevé courant sont recalculés ; les archives conservent les effectifs observés à leur date. Un effectif absent reste indisponible. Le fichier importé ne devient pas une extraction GLPI certifiée après une modification d’effectif ou de site.

Les échanges GLPI, le central et les services d’essai utilisent leurs adaptateurs simulés. Google Maps est une ouverture Internet facultative, déclenchée uniquement par son bouton. Les données absentes du CSV restent signalées : la simulation n’invente ni historique, ni description, ni unité de durée.

## Un menu plus simple

Les groupes du menu se replient. La navigation ouvre le groupe de la page consultée ; les épingles, le classement personnalisé et la palette sont conservés. Le clavier, le mode compact, les thèmes, le contraste renforcé et la réduction des animations restent pris en charge.

**Assistant** et **Lectures ciblées** sont masqués par défaut, ainsi que leurs raccourcis contextuels concernés. Ils peuvent être réaffichés dans la personnalisation du menu. **Effectifs** devient **Effectifs des sites**, accessible depuis les outils de **Sites** et masqué du menu par défaut. Il s’agit du nombre de personnes rattachées à un lieu, utilisé pour certains ratios, et non du suivi des techniciens.

Ces valeurs par défaut sont appliquées une fois aux profils existants. Les choix explicites effectués ensuite dans la personnalisation restent mémorisés.

## Lire les aperçus de lignes

Les listes ouvertes depuis les dashboards, requêtes et analyses partagent une fenêtre plus large. Les identifiants, dates et statuts gardent des colonnes adaptées ; les titres et descriptions disposent de davantage de place. Les cellules sont alignées en haut pour permettre de comparer les lignes.

Les textes longs sont limités à **trois lignes dans l’aperçu**. Cliquer une valeur ouvre sa valeur brute complète et son interprétation. Les commandes de pagination et le défilement restent disponibles. Les exports conservent toutes les données du périmètre sélectionné.

## Comprendre les compteurs de Sites

Le compteur **Localisation reconnue** utilisait une mesure réservée aux communes : un site reconnu sans commune renseignée pouvait donc être présent dans le tableau des sites tout en restant absent de ce compteur. Le compteur à vérifier ne regroupait pas non plus tous les cas à examiner.

Les deux cartes utilisent maintenant une répartition complète du stock actif sélectionné, lorsque le repérage est disponible :

| Carte | Lignes comptées |
| --- | --- |
| **Localisation reconnue** | Commune unique ou site unique reconnu, même sans commune ou coordonnées renseignées, sans ambiguïté de site |
| **Localisation à vérifier** | Absence de correspondance, titre absent ou ambiguïté de localisation, y compris plusieurs sites dans une même commune |

Une **localisation reconnue** ne garantit pas toujours un **site précis**. Un ticket mentionnant uniquement une commune peut entrer dans la première carte tout en restant dans la ligne des sites non renseignés du tableau de planification. Les sondes propres aux communes conservent leur définition.

Le tableau conserve les sites à zéro et les cas sans site précis. Les identifiants, les préconisations et la copie de la liste complète portent sur les tickets du périmètre courant.

Les deux cartes de localisation des tableaux natifs déjà enregistrés reprennent les mesures corrigées en conservant leur disposition et leurs paramètres.

## Cumuler plusieurs équipes et groupes

Dans **Groupes**, **Plus anciens**, **Suivi agents**, **Agents actifs** et **Sites**, le filtre existant accepte maintenant plusieurs équipes et groupes :

1. Ouvrir **Équipes et groupes** puis cocher les choix à réunir. Une recherche est proposée lorsque la liste est longue.
2. Cliquer **Appliquer** pour actualiser le périmètre. **Annuler** conserve la sélection précédente.
3. Utiliser **Toutes** pour retrouver toutes les équipes et tous les groupes.

Jusqu’à 64 équipes ou groupes peuvent être cumulés. Chaque rubrique conserve sa sélection pendant la session de l’interface. L’espace habituel et la simulation ont des sélections distinctes ; ces choix ne sont pas des réglages persistants après redémarrage. Si un choix disparaît après un changement de relevé ou de paramètres, il est retiré de la sélection avec un avertissement.

Une équipe correspond à l’union de ses groupes configurés. Toutes les équipes et tous les groupes sélectionnés se combinent par **OU**. Une même ligne correspondant à plusieurs choix, par exemple une équipe et l’un de ses groupes, est retenue une seule fois. La sélection s’applique aux cartes, répartitions, préconisations, listes de tickets et exports, avec les autres filtres du tableau.

La couverture des sites dépend des tickets sélectionnés. Si certaines équipes n’inscrivent pas de site dans les titres, les inclure dans une vue globale augmente les lieux à vérifier. Un focus par équipe permet de comparer des populations équivalentes.

## Détection et référentiel

Les règles de détection dans les titres sont **inchangées** : noms et variantes explicites du référentiel, mots entiers, normalisation de la casse, des accents et de la ponctuation, et signalement des ambiguïtés. Cette livraison corrige les compteurs et le périmètre présenté ; elle n’introduit pas de rapprochement approximatif pour augmenter la couverture.

Les sites saisis, importés depuis un profil de classeur ou initialisés depuis le fichier facultatif `wad/sites.csv` restent modifiables dans le référentiel.

### Communes et adresses dans le fichier de sites

Le format de démarrage reste compatible avec les deux colonnes d’origine. Il accepte maintenant les colonnes géographiques facultatives suivantes :

```text
Site;Termes de détection;Ville;Code postal;Adresse;Latitude ville;Longitude ville;Latitude site;Longitude site
```

La commune est créée une seule fois puis reliée à ses sites. Le code postal est celui de la commune ; il peut rester vide pour une commune qui en possède plusieurs. L’adresse du site peut préciser son propre code. Les coordonnées de commune servent aux regroupements de villes ; celles du site servent à localiser le lieu. Les deux ne sont pas interchangeables.

Au premier import, le fichier crée les références et complète les variantes. Pour un profil déjà initialisé, une nouvelle version géographique du fichier **complète seulement les champs manquants des références toujours présentes**. Elle conserve leurs identifiants, les valeurs saisies et les rattachements personnalisés. Elle ne recrée pas les sites supprimés ou renommés et ne réintroduit pas les alias retirés. Une sauvegarde complète précède l’enregistrement. Un fichier identique ne déclenche aucune réécriture ; un fichier absent ne bloque pas le démarrage.

Le [complément de sites](https://github.com/M4D135/GLPI-Pilot/tree/main/distribution/sites) est un fichier de données séparé. Le logiciel reste générique. Installer ce complément puis redémarrer complètement l’application permet à un nouvel import CSV de reprendre les communes, adresses et coordonnées dans la simulation.

## Google Maps pour les sites et les tournées

Les outils de **Sites**, le référentiel géographique et les tournées proposent des accès Google Maps. L’aperçu présente les lieux du parcours avant l’ouverture. Il fonctionne localement, y compris en simulation. Le bouton **Ouvrir Google Maps** lance le navigateur et fonctionne sans clé API.

Pour une tournée, l’ordre des visites est conservé, avec un parcours distinct par journée et affectation. Les longs parcours sont découpés en parties compatibles avec les limites d’URL et d’étapes de Google Maps ; la dernière étape d’une partie devient le départ de la suivante. Aucun arrêt n’est supprimé. Une carte des communes représente explicitement leur ordre de visite et ne prétend pas localiser chaque bâtiment.

La **carte intégrée** est facultative et demande une clé **Maps Embed API** fournie dans **Réglages Google Maps**. Elle s’ouvre dans une fenêtre aux couleurs de l’application, séparée de l’interface métier. La clé peut être conservée pour le compte Windows avec la protection locale du système ; elle reste en dehors des exports de configuration. Une clé saisie en simulation reste en mémoire jusqu’à la fermeture de cet espace.

L’ouverture transmet uniquement les adresses ou coordonnées affichées, sans tickets, descriptions ni noms d’agents. Elle nécessite Internet. La préparation des tournées, les calculs locaux et la consultation des données importées restent disponibles hors ligne. Une adresse manquante produit une recherche par nom à confirmer ; le centre d’une commune n’est pas utilisé silencieusement comme position d’un site.

## Validation et publication

Les contrôles ciblés portent sur les aperçus partagés, la navigation, les états de localisation, les filtres multiples et leurs exports, le fichier géographique, les effectifs et le parcours complet après import CSV. Les opérations Google Maps vérifient notamment l’ordre des étapes, les limites des URL et l’ouverture à la demande. Le [rapport de vérification](Rapport-verification.txt) consigne les résultats locaux et les limites de validation.

Les packs PROD et DEV sont publiés après réussite des contrôles Windows et PostgreSQL du commit livré. Le commit inscrit dans `wad/version.json`, les journaux de son workflow et les inventaires SHA-256 identifient cette livraison.
