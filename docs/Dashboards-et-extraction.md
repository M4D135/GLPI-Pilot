# Dashboards et fiabilité de l’extraction

Ce document décrit les capacités effectivement intégrées au dépôt. Les calculs
reposent sur les colonnes du relevé accessible à l’utilisateur. Un champ absent,
ambigu ou illisible reste indisponible ; il n’est pas remplacé par une valeur
inventée.

## Composer et filtrer les tableaux de bord

Les dispositions globale et personnelle restent indépendantes. Les widgets
peuvent être déplacés, redimensionnés, dupliqués, masqués et réglés. La nouvelle
composition ajoute :

- des sections nommées et repliables, dont l’état est mémorisé ;
- la sélection de plusieurs widgets pour modifier leur section ou leur style ;
- un catalogue avec favoris mémorisés et filtre « disponible sur ce relevé » ;
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
| Présentations matricielles | Matrice colorée, barres empilées, liste chiffrée | Les valeurs et le mode de comptage restent visibles |
| Histogrammes | Âge des actifs, ancienneté de la dernière modification, ouverture → résolution, retard des échéances | Bornes réglables dans le widget ; jours ou heures calendaires selon la série |
| Dispersion par segment | Minimum, Q1, médiane, Q3, P90 et maximum, par ville, affectation complète ou catégorie | Séries âge et ouverture → résolution ; effectifs affichés ; interpolation linéaire des quantiles |
| Nuage de points | Âge × temps avant échéance | Tickets actifs disposant des deux dates ; une ordonnée négative désigne une échéance dépassée |
| Répartitions | Barres, listes, colonnes, anneau et Pareto | Anneau et Pareto réservés aux comptages ; les agrégats non additifs ne sont pas transformés en parts du total |
| Historique local | Stock, actifs, échéances dépassées, priorités hautes actives | Archives comparables uniquement ; voir les règles ci-dessous |
| Météos spécialisées | Synthèse, échéances, qualité des données, localisation | Unité, couverture et méthode affichées ; indisponibilité explicite si les données requises manquent |

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
dans la courbe. Les sources différentes ne sont pas fusionnées. Une disparition
de la liste n’est jamais interprétée comme une résolution ou une clôture.

Les courbes historiques des vues personnelles ou filtrées ne sont pas
alimentées avec des chiffres globaux. Leur recalcul historique spécifique
n’est pas implémenté ; le widget indique l’absence de série exploitable. Les
histogrammes de dates présents dans un relevé restent distincts des archives
successives d’une population.

## Exports du dashboard

L’export PNG capture le contenu du dashboard avec son thème et sa disposition.
L’export de bilan CSV/Excel contient les valeurs, unités, effectifs et méthodes,
y compris les matrices, quantiles, points, séries historiques et météos
spécialisées disponibles. Une estimation exacte du nombre de lignes de bilan
permet de proposer CSV lorsque la capacité d’une feuille Excel serait dépassée.

L’image décrit le contenu affiché. Les lignes détaillées et les explications
de calcul restent accessibles dans l’interface et le bilan structuré.

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

## Limites maintenues explicitement

- Le portail réel et son SSO d’entreprise ne sont pas accessibles aux tests.
  Les tests utilisent des pages synthétiques ; ils ne constituent pas une
  validation de la politique de connexion de l’organisation.
- Le contrôle de périmètre dépend des informations rendues observables par
  GLPI. Un contexte non vérifiable interdit une comparaison historique fiable.
- Une recherche vivante peut évoluer pendant la lecture. Les compteurs,
  échantillons et empreintes réduisent ce risque sans produire un instantané
  transactionnel du serveur.
- La reprise au milieu d’une ancienne pagination n’est pas implémentée.
  Une relance repart du début ; elle ne fusionne pas de vieilles pages avec une
  population qui a pu changer.
- Les cartes géographiques, Sankey, projections, détection statistique
  d’anomalies et cohortes longitudinales ne sont pas ajoutés par ce lot.
- Les indicateurs de traitement, validation ou satisfaction nécessitent leurs
  champs réels. Ce lot n’invente pas ces informations à partir du seul titre,
  du statut ou de la date de dernière modification.
- Le rendu WPF et le comportement du gestionnaire de fenêtres sont à valider
  sous Windows. Les tests navigateur ne mesurent pas le placement réel de la
  fenêtre GLPI Pilot au-dessus d’Edge ou Chrome.

## Vérification reproductible

Les suites `DashboardChecks` et `DashboardLayoutChecks` vérifient les calculs,
les périmètres et la persistance. `BrowserChecks` vérifie les gardes du moteur.
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
