# Dashboards et fiabilité de l’extraction

Ce document décrit les capacités effectivement intégrées au dépôt. Les calculs
reposent sur les colonnes du relevé accessible à l’utilisateur. Un champ absent,
ambigu ou illisible reste indisponible ; il n’est pas remplacé par une valeur
inventée.

## Composer et filtrer les tableaux de bord

Les dispositions globale et personnelle restent indépendantes. Les widgets
peuvent être déplacés, redimensionnés, dupliqués, masqués et réglés. La nouvelle
composition ajoute :

- des sections nommées et repliables, dont l’état est mémorisé, déplaçables comme
  un bloc dans le tableau de bord ;
- la sélection de plusieurs widgets pour modifier leur section, leur style,
  leurs dimensions ou les aligner en lignes ; les widgets verrouillés restent
  protégés de ces modifications ;
- un catalogue avec favoris mémorisés, trente derniers widgets utilisés et
  filtre « disponible sur ce relevé » ;
- des filtres communs au dashboard, combinés par **ET** ;
- des filtres supplémentaires propres à chaque widget ;
- des filtres croisés par clic sur une catégorie ou une population d’indicateur ;
- une remise à zéro et une commande pour retrouver les lignes correspondantes ;
- jusqu’à 30 contextes de filtres nommés, enregistrés dans la disposition ;
- une comparaison entre deux périmètres A et B du même relevé.

Les filtres proposent une valeur de répartition, une population d’indicateur ou
une période datée. Les conditions d’exclusion sont disponibles. Les dates
absentes ou illisibles ne deviennent pas implicitement membres d’une période,
y compris lorsque cette période est exclue. Une condition dont la colonne ou
la population n’est plus disponible provoque un état explicite ; elle n’est pas
silencieusement ignorée.

Le périmètre B est indépendant des filtres A et reste borné à la source de la
vue. Sur une vue personnelle, il ne permet donc pas de récupérer des tickets
extérieurs à la population personnelle. Les filtres d’un widget s’ajoutent aux
filtres communs. Les recalculs conservent les indices des lignes d’origine et
l’heure du calcul initial. Pendant un recalcul, les anciens chiffres ne sont
pas affichés sous les nouveaux filtres.

Le mode « Cliquer pour filtrer les widgets » permet de choisir entre filtrage
du dashboard et ouverture des lignes. Les matrices, boîtes statistiques et
points individuels ouvrent leurs lignes de détail. Une sélection dans le
dashboard ne change pas le fichier natif reçu.

## Analyses ajoutées au catalogue

| Famille | Contenu disponible | Conditions et unités |
| --- | --- | --- |
| Matrices | Techniciens × villes, villes × statuts, catégories × statuts, urgence × impact | Les deux dimensions doivent être identifiées ; la matrice techniciens/villes porte sur les actifs reconnus |
| Présentations matricielles | Matrice colorée, barres empilées en volume ou en pourcentage, liste chiffrée | Les valeurs et le mode de comptage restent visibles ; le mode volume conserve une échelle commune entre lignes |
| Histogrammes | Âge des actifs, ancienneté de la dernière modification, ouverture → résolution, retard des échéances | Bornes réglables dans le widget ; jours ou heures calendaires selon la série |
| Dispersion par segment | Minimum, Q1, médiane, Q3, P90 et maximum, par ville, affectation complète ou catégorie | Séries âge et ouverture → résolution ; effectifs affichés ; interpolation linéaire des quantiles |
| Nuage de points | Âge × temps avant échéance | Tickets actifs disposant des deux dates ; une ordonnée négative désigne une échéance dépassée |
| Répartitions | Barres, listes, colonnes, anneau et Pareto | Anneau et Pareto réservés aux comptages ; seuil cumulatif du Pareto configurable ; les agrégats non additifs ne sont pas transformés en parts du total |
| Historique local | Séries des indicateurs archivés, mini-courbes des cartes d’indicateur et comparaison de deux périodes | Archives comparables uniquement ; période réglable par widget et ruptures visibles |
| Météos spécialisées | Synthèse, échéances, qualité des données, localisation et charge observée | Unité, couverture et méthode affichées ; indisponibilité explicite si les données requises manquent |
| Échéances exclusives | Retards, tranches de temps restant configurables et échéances absentes ou illisibles | Chaque actif appartient à une seule tranche ; jours/heures calendaires, sans règle SLA supposée |
| Satisfaction | Moyenne, nombre de réponses, couverture du relevé, réponse aux invitations observées | Échelle configurable ; champs réels nécessaires ; absence de réponse distincte de zéro |
| Carte hors ligne | Points issus des coordonnées des sites ou villes, zoom et sélection reliée aux autres widgets | Coordonnées renseignées dans le référentiel ; couverture affichée, sans fond routier ni service externe |
| Ratios de sites | Tickets pour 100 personnes ou postes | Inventaire daté, propre au contexte GLPI ; dénominateurs absents ou périmés laissés indisponibles |

Les résultats de requêtes numériques utilisent leur valeur réelle, leur unité
et leur effectif. Une moyenne, médiane ou autre agrégation indisponible reste
indisponible : elle n’affiche pas le nombre de tickets à la place du résultat.
Les catégories issues des référentiels de villes et de sites peuvent être
ajoutées par le catalogue lorsqu’elles sont présentes dans le relevé enrichi.

### Affectations multiples

Trois modes sont proposés pour les matrices par technicien :

1. **Participations** : chaque technicien explicitement nommé compte une
   participation. La somme peut dépasser le nombre de tickets distincts.
2. **Parts égales** : un ticket partagé entre deux techniciens contribue pour
   0,5 à chacun. L’unité est affichée comme une part.
3. **Cellule complète** : la valeur entière de l’affectation forme une catégorie.

Les autres répartitions qui conservent la cellule complète le précisent. Ces
volumes ne mesurent ni la capacité disponible ni la productivité individuelle.

Les séparateurs des cellules multivaleurs se règlent dans l’assistant de
correspondances. Les matrices du dashboard et les conditions/regroupements
de techniciens dans les requêtes utilisent ces mêmes séparateurs. Les cellules
du relevé original restent intactes.

### Carte et repérage des lieux

Le widget de carte utilise une projection longitude/latitude calculée
localement. Il affiche un site lorsque ses coordonnées sont exploitables,
sinon les coordonnées disponibles de sa ville. Une ligne n’est pas répartie
entre plusieurs points. Les lieux ambigus ou sans coordonnées n’apparaissent
pas comme des positions inventées : le rapport localisé/total rend cette
couverture visible.

Le bouton de zoom, le recentrage et la liste de toutes les localisations
permettent de retrouver les points, y compris lorsqu’ils se superposent.
L’option « Filtrer les autres widgets au clic » réutilise les filtres croisés
du dashboard. Sans cette option, le clic ouvre les lignes correspondantes.
Les coordonnées d’une ville représentent cette ville, pas une adresse de
ticket. Aucun fournisseur de cartes ne reçoit les données de cette vue.

## Météo, seuils et règles locales

Dans les réglages du dashboard, les cinq poids de la météo globale sont
modifiables : échéances dépassées, priorité haute, absence d’affectation,
ancienneté de la dernière modification et âge. Un poids nul désactive le
signal. Les seuils vigilance/pluie/orage, la couverture minimale de chaque
signal et la part minimale des poids exploitables sont également réglables.

La couverture indique quelle population possède réellement les champs
nécessaires. Une colonne manquante ne produit pas un signal rassurant de zéro.
Les critères de qualité et d’exhaustivité peuvent empêcher une météo de se
présenter comme concluante. La météo est une synthèse descriptive du relevé,
pas une prévision.

Une matrice de priorité locale peut être saisie avec des lignes de la forme
`impact:urgence=priorité`. Elle sert à repérer les écarts à **ces règles locales
explicites**. Aucune matrice GLPI présumée n’est appliquée par défaut.

L’indicateur de fraîcheur expose l’âge du relevé. Les dates futures ou
incohérentes restent signalées. Les durées ouverture/résolution et les écarts
aux échéances sont calendaires : les calendriers ouvrés, pauses et règles SLA
ne peuvent pas être reconstruits à partir d’une simple liste de tickets.

La météo de charge compare les volumes d’affectation observés à leur médiane
et expose les actifs sans affectation. Elle exige au moins deux techniciens
effectivement observés. Une concentration supérieure à deux fois la médiane
est un signal descriptif, sans déduction de disponibilité, de productivité ou
de surcharge contractuelle.

Les bornes d’échéance se saisissent en heures dans les réglages du dashboard.
Les intervalles sont fermés à gauche et ouverts à droite ; la dernière tranche
n’a pas de borne haute. Les retards et les échéances absentes ou illisibles
ont chacun une catégorie distincte. Les autres widgets « aujourd’hui »,
« demain » ou « bientôt » restent disponibles avec leur propre définition.

La satisfaction accepte les notes finies comprises entre zéro et l’échelle
choisie. Une moyenne est accompagnée de son effectif. La couverture rapporte
les réponses exploitables à tous les tickets du relevé. Le **taux de réponse
aux invitations** utilise uniquement les invitations explicitement datées ou
marquées envoyées : sans ce champ, il reste indisponible.

## Interprétation des colonnes sans modifier les valeurs brutes

L’assistant de correspondances permet de vérifier les rôles, les formats de
nombres et de dates, les séparateurs multivaleurs et les dictionnaires de
valeurs avant application. Il affiche une cellule brute et son interprétation.
Un dictionnaire s’applique d’abord au rôle, puis, à défaut, au nom exact de la
colonne. Le dashboard et les requêtes utilisent cette interprétation commune.

Le fuseau source est facultatif et reste vide tant qu’il n’a pas été choisi.
Lorsqu’il est défini, une date sans décalage explicite est interprétée dans ce
fuseau ; une heure inexistante ou ambiguë lors d’un changement d’heure reste
inexploitable. Une date qui contient déjà son décalage le conserve. Les
groupements numériques invalides sont refusés au lieu de supprimer des
séparateurs au hasard. Ces réglages ne réécrivent pas le fichier reçu.

## Historique et comparabilité

Les courbes globales utilisent les métadonnées des archives locales. Au plus
500 archives récentes de la même recherche et du même contexte sont retenues.
Un point est comparable seulement si :

- le relevé courant et l’archive sont complets ;
- l’adresse de recherche et le contexte GLPI observé sont confirmés ;
- les identifiants sont lisibles et uniques ;
- les colonnes et les règles de normalisation sont compatibles ;
- l’indicateur existe dans l’archive.

Les archives incomplètes ou incompatibles de ce contexte créent des ruptures
dans la courbe. Une période de plus d’une fois et demie la cadence attendue
entre deux collectes insère également une rupture explicite, sans point zéro
ni interpolation. Les sources différentes ne sont pas fusionnées. Une
disparition de la liste n’est jamais interprétée comme une résolution ou une
clôture.

Chaque carte d’indicateur ou jauge peut montrer sa mini-courbe et comparer
deux fenêtres de durée égale, réglables dans le widget. La comparaison indique
les moyennes des observations disponibles, leurs effectifs et le nombre de
ruptures. Il ne s’agit ni d’une moyenne pondérée par le temps ni d’un décompte
d’événements sur toute la période.

Les courbes historiques des vues personnelles ou filtrées ne sont pas
alimentées avec des chiffres globaux. Leur recalcul historique spécifique
n’est pas implémenté ; le widget indique l’absence de série exploitable. Les
histogrammes de dates présents dans un relevé restent distincts des archives
successives d’une population.

### Analyses longitudinales

Dans **Historique → Analyses historiques**, choisissez un relevé de référence,
de 2 à 120 archives et une cadence attendue de 1 à 720 heures. Le logiciel
prend les archives antérieures de la même recherche et du même contexte,
dans la limite de deux millions de lignes cumulées. Le récapitulatif montre
la période, l’âge de sa dernière observation, les intervalles comparables,
les ruptures et la couverture temporelle.

| Vue | Mesure et limites |
| --- | --- |
| Mouvements | Apparitions, absences, résolutions et réouvertures réellement observées entre paires comparables. Un intervalle incompatible ne reçoit pas de mouvements supposés. |
| Flux d’affectation | Transferts constatés de cellule à cellule, pour techniciens ou groupes. Chaque flux compte des tickets distincts ; une affectation multiple reste une cellule complète, sans transfert individuel inventé. Le clic retrouve les contributeurs présents dans le relevé courant du même contexte. |
| Calendrier observé | Nombre de collectes et changements constatés par jour. Un jour sans collecte reste inconnu, même si aucun changement n’a été enregistré. |
| Cohortes J+1 / J+7 / J+30 | Tickets regroupés par première observation dans la sélection. Les horizons exigent une chaîne comparable continue ; actifs, résolus/clos, absents et statuts inconnus restent séparés. « Première observation » ne signifie pas « date de création ». |
| Intervalles de statut | États bruts aux deux extrémités et durée maximale de l’intervalle. Sans événements intermédiaires, la borne minimale générale reste zéro. |
| Durées conditionnelles par statut | Bornes cumulées sous l’hypothèse explicitement affichée d’absence de transition cachée : un même état aux deux observations couvre alors l’intervalle ; un changement ne fixe pas sa date exacte. Ces durées ne sont ni des SLA ni du temps de travail. |
| Variations expliquées | Valeurs avant/après, entrées, sorties et changements des contributeurs. Une décomposition additive exacte est fournie pour les comptages ; moyennes, pourcentages et autres agrégats non additifs ne reçoivent pas une fausse attribution par ticket. |
| Variations inhabituelles | Signal exploratoire sur stock, actifs, retards et priorités hautes, avec au moins sept observations antérieures comparables. Référence glissante de trente valeurs au maximum, médiane et dispersion robuste ; une rupture réinitialise la référence. |
| Scénarios du stock | Horizons 1, 7 et 30 jours à partir d’au moins huit observations continues couvrant trois jours, soixante au maximum. Tendance linéaire et bande prédictive à 95 % du modèle, sous hypothèses affichées de résidus indépendants et de variance stable. Ni saisonnalité ni changement d’organisation ne sont modélisés ; les valeurs sont bornées à zéro. |

Le diagramme montre les douze premiers flux de la dimension choisie ; sa
table conserve les flux calculés. Le calendrier est borné à dix ans. Les
intervalles détaillés de statut sont bornés à 100 000, avec avertissement si
la sélection excède cette limite. Réduisez la période pour examiner davantage
de détails dans un autre calcul.

La commande **Vérifier les absents dans GLPI**, dans la comparaison, prépare
une file de lectures de tickets, bornée aux mille premières références sûres.
La personne choisit sa limite et lance ensuite la lecture. Les états lus et
leur date restent distincts des archives ; ils ne réécrivent pas ce qui était
observé historiquement.

### Catalogue et recherche indexés

Le catalogue conserve un index JSON versionné et contrôlé. La recherche
**Historique d’un ticket** réutilise des index par archive contenant
l’identifiant normalisé et les valeurs observées de statut et d’affectation.
Elle inspecte jusqu’à cinq cents archives récentes du contexte sélectionné,
sans devoir décompresser chaque relevé intact à chaque recherche.

Les index sont produits à l’enregistrement, écrits atomiquement, associés à
l’empreinte et aux caractéristiques du fichier source, puis retirés avec leur
archive lors de la rétention. Un index absent, ancien ou endommagé est
reconstruit depuis l’archive dont l’intégrité est vérifiée. Les fichiers
d’archive restent la source de vérité ; l’index n’est pas un journal exhaustif
des événements GLPI.

## Effectifs et taux pour cent unités

**Historique → Effectifs des sites** permet de saisir, remplacer, supprimer,
importer et exporter l’inventaire local au format JSON. Chaque ligne comporte
un contexte GLPI confirmé, un site, l’unité personnes ou postes, un effectif
entier positif, deux dates ISO de validité inclusives et une référence. Deux
périodes du même site, de la même unité et du même contexte ne peuvent pas se
chevaucher. L’import présente le nombre de périodes et de remplacements avant
son enregistrement.

Le dénominateur est choisi à la **date d’extraction**. Un effectif absent,
expiré, ambigu ou appartenant à un autre contexte rend le taux indisponible.
Un filtre de tickets ne réduit pas l’effectif du site : le taux reste le
nombre de tickets sélectionnés pour cent unités du site. Dans un dashboard
filtré, un site sans ticket observé est exclu du dénominateur couvert. Dans
le relevé global, un site référencé sans ticket peut contribuer zéro ticket
et son effectif valide.

Ces valeurs décrivent un stock présent dans la recherche. Elles ne deviennent
pas des taux d’incidence sur une période. La couverture en tickets et le
dénominateur utilisé restent visibles. Le référentiel accepte jusqu’à dix
mille périodes et quatre Mio ; un fichier illisible est signalé et ne bloque
pas les autres calculs du dashboard.

## Exports du dashboard

L’export visuel propose **PNG** et **PDF natif**. Les deux contiennent un
cartouche avec dates d’extraction et de calcul, disposition, périmètre,
recherche, filtres communs et croisés, filtres des widgets, nombre de lignes,
total annoncé et complétude. Le PDF pagine le rendu sans imprimante virtuelle
ni dépendance installée. Ses pages sont des images du dashboard, pas du texte
sélectionnable. Le PNG est adapté à une dimension maximale de 8 192 pixels ;
préférez le PDF pour une grande disposition.

La commande de bilan propose aussi un **rapport HTML complet**, autonome et
imprimable : critères, qualité des données, avertissements, indicateurs,
graphiques vectoriels de répartition, catégories détaillées et toutes les
lignes du périmètre. Les cellules sont échappées ; le rapport n’exécute pas
de script et ne charge aucune ressource externe. Sa taille est bornée à
256 Mio. Il peut contenir les données métier du relevé : le choix de sa
destination reste explicite.

L’export de bilan CSV/Excel contient les valeurs, unités, effectifs et méthodes,
y compris les matrices, quantiles, points, séries historiques et météos
spécialisées disponibles. Une estimation exacte du nombre de lignes de bilan
permet de proposer CSV lorsque la capacité d’une feuille Excel serait dépassée.

L’image décrit le contenu affiché, y compris la vue courante de la carte.
Les lignes détaillées et les explications de calcul restent accessibles dans
l’interface, le bilan structuré et le rapport HTML. Les écritures finales
utilisent un fichier temporaire puis un remplacement atomique ; une erreur
de rendu ne remplace pas un fichier déjà présent par un export incomplet.

## Assistant facultatif de requêtes et de synthèse

L’assistant s’ouvre depuis **Sources et organisation** sur un relevé déjà
chargé. Son ouverture ne contacte aucun fournisseur. Le moteur local propose
une grammaire française documentée, par exemple `tickets actifs par catégorie`,
`tickets dont le titre contient «VPN»` ou
`moyenne de «Coût» des tickets actifs`. Une clause non reconnue est refusée,
jamais supprimée pour élargir silencieusement une requête. Il s’agit d’un
moteur déterministe, pas d’un modèle de langage embarqué.

Le fournisseur compatible facultatif accepte un endpoint complet
`/chat/completions`, un nom de modèle et, si nécessaire, une clé. Il doit
prendre en charge le format JSON et `max_completion_tokens`. HTTPS est requis,
sauf HTTP sur la boucle locale pour un serveur choisi par la personne.
Les URL contenant identifiants, paramètres ou fragments sont refusées. Le
client demande `store=false`, refuse les redirections, ne réessaie pas
automatiquement et permet l’annulation avec un délai de 10 à 180 secondes.
Les conditions de traitement et de conservation restent celles du fournisseur.

Le parcours distingue trois étapes visibles :

1. **Préparer le contenu** affiche la destination, le modèle, les instructions
   et les données exactes. Pour une requête, seul le schéma disponible, ses
   bases/opérateurs et les métadonnées de périmètre sont fournis, sans cellules
   de tickets. Pour une synthèse, la personne choisit le dashboard capturé à
   l’ouverture ou tout le relevé, éventuellement des identifiants précis,
   de 1 à 200 tickets au maximum et les champs facultatifs. Titres, personnes,
   dates et lieux sont exclus par défaut. Les doublons d’identifiants sont
   exclus, les cellules sont bornées à mille caractères et toute réduction
   est indiquée.
2. **Générer la proposition** reste une action explicite après préparation.
   Un envoi externe exige aussi l’activation visible de cette destination.
   Modifier une entrée invalide la préparation. Les propositions sont limitées
   à un DSL de requête validé ou à une synthèse documentée ; aucun code, SQL,
   appel d’outil ou modification de GLPI n’est exécuté.
3. Pour une requête, **Prévisualiser sur le relevé** exécute le calcul local
   explicitement. L’ajout au brouillon dans l’atelier exige un aperçu disponible.
   L’enregistrement de la bibliothèque reste une action distincte. Pour une
   synthèse, chaque constat cite des identifiants effectivement fournis, des
   champs sélectionnés et des extraits exacts. Une référence inconnue ou un
   extrait inventé rejette la synthèse. Le clic retrouve la ligne originale
   tant que le relevé n’a pas changé.

Le contrôle des références ne prouve pas la justesse sémantique d’une phrase
ou d’une conclusion : la synthèse doit être relue. Le moteur local produit
des décomptes de valeurs observées, avec toutes leurs références. Les données
de tickets restent des données, même lorsqu’elles contiennent du texte qui
ressemble à une instruction.

Le profil est enregistré séparément de la configuration portable. Une clé
facultativement mémorisée utilise la protection du compte Windows (DPAPI) et
reste liée à l’endpoint exact. Elle n’apparaît ni dans la préparation ni dans
le corps de la requête. L’autorisation d’envoi n’est pas réactivée à la prochaine
ouverture. Une annulation locale ne peut pas retirer ce qui a déjà été envoyé
au fournisseur. Les demandes sont bornées à 256 Kio et les réponses à 512 Kio.

## Parcours automatique d’acquisition

### Avant d’ouvrir le navigateur

Le prévol contrôle l’adresse HTTP(S), le choix Edge/Chrome, le mode
d’extraction, les délais, les limites, le total configuré, la cohérence des
repères personnalisés et l’écriture dans le dossier de sortie.

Un verrou de fichier ouvert exclusivement protège le profil navigateur réservé
à GLPI Pilot. Une autre instance obtient un message compréhensible. Le verrou
est libéré à la fermeture ou par le système après un arrêt du processus : un
ancien fichier seul ne bloque pas la reprise. Aucun profil n’est supprimé pour
forcer l’accès. L’absence du navigateur choisi est signalée clairement.

### Choix de la méthode

L’extraction attend le SSO et revient à l’adresse de recherche mémorisée.
Les commandes CSV « Toutes les pages » restent vérifiées dans le DOM de GLPI.
Lorsque le CSV est réellement indisponible, l’option d’affichage complet
existante peut être utilisée si elle est reconnue et suffisante. Si cette voie
n’existe pas et si le réglage de repli l’autorise, le logiciel revient à la
première page puis suit la pagination.

Le repli ne s’applique qu’à l’absence confirmée d’une commande native : une
ambiguïté, un échec de transfert, un fichier HTML/binaire reçu à la place du CSV,
un échantillon d’identifiants divergent ou un reçu incomplet ne déclenchent pas
une récupération silencieuse. Un fichier natif reçu reste conservé intact.
Un fichier incomplet peut être chargé comme incomplet ; il n’est pas certifié.

Les repères personnalisés incomplets ou dangereux provoquent un refus. Les
formulaires métier, boutons de mise à jour, destinations étrangères et
commandes ambiguës ne sont pas utilisés comme export ou pagination.

### Contrôles du périmètre

Les critères et contrôles de contexte GLPI observables sont condensés dans
une empreinte ; leurs valeurs ne sont pas journalisées. L’adresse effective,
le chemin et les filtres non représentés dans ces contrôles contribuent aussi
à l’empreinte. Les décalages et tailles de page sont exclus. Le tri ne change
pas l’identité de la population.

Lorsqu’un formulaire GLPI recopie un critère visible dans l’URL, seul un couple
nom/valeur identique au contrôle DOM est neutralisé. Une valeur d’URL divergente
reste détectée, même si un ancien formulaire est encore présent. Les filtres
inconnus des extensions ne sont pas retirés arbitrairement.

La pagination vérifie le périmètre, les colonnes, les pages déjà vues, les
identifiants répétés et les totaux. Un total configuré ne masque plus une
variation du total observé dans GLPI. Un changement de périmètre interrompt
la lecture avant d’ajouter les lignes de la nouvelle page.

Pour un CSV natif, jusqu’à 25 identifiants répartis dans la liste visible sont
recherchés dans le fichier. Les séparateurs de milliers et les zéros de tête
sont normalisés pour ce seul rapprochement ; les valeurs exportées ne sont
pas modifiées. Un identifiant manquant provoque un refus explicite. Une colonne
ID absente ou ambiguë empêche la certification. Ce contrôle complète le
comptage ; il ne prouve pas à lui seul l’identité de toute la recherche.

Un contrôle final compare le contexte, le total et, pour le natif, l’échantillon
encore visible. Si le contexte a changé, le résultat reste à vérifier. Si la
page disparaît après une lecture paginée, les lignes déjà lues sont enregistrées
comme partielles avec un nom `GLPI-a-verifier-…`, au lieu d’être perdues pendant
le contrôle final.

Les phases et journaux distinguent prévol, connexion, recherche, récupération,
validation, sauvegarde et fichier prêt. Une annulation n’annonce jamais un
fichier prêt. Les téléchargements déjà terminés restent disponibles.

### Reprise contrôlée de pagination

L’option **Réutiliser un point de pagination après contrôle du périmètre**
conserve un point local après chaque page validée. Son âge maximal est
configurable de 5 à 1 440 minutes. À la relance, le moteur contrôle la cible,
le contexte, la première page, les colonnes, le total puis la dernière page
conservée avant de continuer. La provenance indique qu’il s’agit d’une reprise
et conserve son heure de départ initiale.

Un point incompatible ou trop ancien n’est pas fusionné avec la nouvelle
population : la collecte repart de la première page. Le point compressé est
borné à 32 Mio et les limites de pages/lignes restent applicables. Ces
contrôles ne rendent pas transactionnelle une liste GLPI vivante : un
changement intermédiaire non visible dans les vérifications peut encore
nécessiter une collecte neuve pour l’analyse métier.

## Limites maintenues explicitement

- Le portail réel et son SSO d’entreprise ne sont pas accessibles aux tests.
  Les tests utilisent des pages synthétiques ; ils ne constituent pas une
  validation de la politique de connexion de l’organisation.
- Le contrôle de périmètre dépend des informations rendues observables par
  GLPI. Un contexte non vérifiable interdit une comparaison historique fiable.
- Une recherche vivante peut évoluer pendant la lecture. Les compteurs,
  échantillons et empreintes réduisent ce risque sans produire un instantané
  transactionnel du serveur.
- Les analyses historiques portent sur des observations successives, pas sur
  un journal complet d’événements serveur. Les intervalles inconnus, absences,
  hypothèses conditionnelles et tailles d’échantillon restent visibles.
- Les mini-courbes utilisent des métriques globales archivées comparables.
  Une vue personnelle ou un filtre local sans série recalculée conserve une
  indication d’indisponibilité ; elle ne reçoit pas la courbe globale.
- Les cartes affichent uniquement les coordonnées fournies. La géométrie
  routière, les trajets et la précision d’une adresse n’en sont pas déduits.
- Les indicateurs de traitement, validation ou satisfaction nécessitent leurs
  champs réels. Les informations des détails de ticket effectivement lus
  restent distinctes d’une simple liste ; aucune information n’est déduite
  du seul titre, du statut ou de la dernière modification.
- Les synthèses externes exigent un fournisseur configuré et une autorisation
  explicite. Leur contenu reste à relire même lorsque les références sont
  techniquement valides.
- Le rendu WPF et le comportement du gestionnaire de fenêtres sont à valider
  sous Windows. Les tests navigateur ne mesurent pas le placement réel de la
  fenêtre GLPI Pilot au-dessus d’Edge ou Chrome.

## Vérification reproductible

Les suites `DashboardChecks` et `DashboardLayoutChecks` vérifient les calculs,
les périmètres et la persistance. `AnalyticsChecks` vérifie les ruptures,
cohortes, durées conditionnelles, scénarios, dénominateurs datés, intégrité
des index et exports atomiques HTML/PDF. `QueryChecks` vérifie aussi les
sous-totaux hiérarchiques, les formats partagés et les packs de requêtes.
`LanguageAssistantChecks` utilise uniquement des réponses synthétiques :
grammaire locale, périmètre, citations, refus de réponses invalides,
autorisation explicite, limites de taille, annulation et protection des clés.
Aucun fournisseur réel n’est sollicité par ces contrôles.

`WindowsStartupChecks` exécute les contrôles WPF réels sous Windows : parcours
de l’assistant sans action implicite, ajout au brouillon, navigation des
citations, saisie datée d’effectifs, carte liée aux autres widgets et rendu
PNG/PDF natif avec cartouche. Une compilation croisée sous Linux ne remplace
pas cette exécution Windows.

`BrowserChecks` vérifie les gardes du moteur.
`AutomationChecks` exerce le parcours complet, le CSV reçu, Excel, l’annulation,
les redirections SSO simulées, l’affichage complet et les diagnostics.
`NativeDomChecks` vérifie l’adaptateur avec les structures GLPI 10, GLPI 11 et
legacy représentées dans les fixtures.

Les suites navigateur lancent des contextes éphémères et interceptent toutes
les requêtes des scénarios. Elles n’ouvrent aucun profil utilisateur de GLPI
Pilot. `GLPI_TEST_CHROMIUM` désigne un exécutable Edge/Chrome/Chromium de test.
Le README de chaque suite décrit sa commande. `NativeDomChecks` peut réutiliser
le pilote `.playwright/package` du build .NET via
`GLPI_TEST_PLAYWRIGHT_MODULE`, sans installation Node supplémentaire.
