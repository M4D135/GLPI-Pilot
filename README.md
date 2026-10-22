# GLPI Pilot 3.0.2 — Windows x64

**Un espace de travail pour exploiter les recherches GLPI accessibles par navigateur et SSO.** Extraction complète, dashboards global et personnel, gestion locale des techniciens, lecture des tickets, requêtes, lieux, tournées, historiques et alertes. Aucun accès API, base de données ou serveur GLPI n’est nécessaire.

La version 3 utilise un **hôte C# WinForms avec WebView2** et une interface locale HTML/CSS/JavaScript. Les calculs, la pagination, les validations, la persistance et les accès au navigateur restent en C#. Le runtime WebView2 fixe est livré dans le ZIP ; les pages de l’application n’ont besoin ni d’un site hébergé ni d’un téléchargement au premier lancement. Le modèle de migration est documenté dans le [contrat WebView](docs/WebView-contract.md), à partir de Domenia 2.

## Télécharger le pack complet

Les packs publiés sont disponibles dans les [Releases](https://github.com/M4D135/GLPI-Pilot/releases). **La validation Windows de la version 3 dépend du résultat du workflow de son commit**, indiqué dans le [rapport de vérification](docs/Rapport-verification.txt) et les journaux de la livraison. La présence de sources, d’un EXE compilé ou d’un ZIP local ne signifie pas que cette validation a réussi.

| Pack | Pour qui | Contenu à la racine |
| --- | --- | --- |
| **PROD — Windows x64** | Utiliser le logiciel | `GLPI-Pilot.exe` + `wad/` |
| **DEV — Windows x64** | Utiliser le même logiciel et poursuivre son développement | `GLPI-Pilot.exe` + `wad/`, sources et tests dans `wad/sources/` |

Extraire **tout le ZIP** dans un dossier local puis ouvrir **GLPI-Pilot.exe**. Garder `wad` à côté. Aucun Python, Node.js, .NET ou installateur WebView2 à ajouter pour utiliser le pack. **Edge ou Chrome reste nécessaire pour la connexion au portail GLPI et son SSO** ; le runtime de l’interface est livré séparément. **Code → Download ZIP** fournit les sources, les packs exécutables sont dans les Releases. Les deux éditions utilisent le même EXE et conservent les empreintes SHA-256 de leurs fichiers.

| Emplacement dans le pack | Rôle |
| --- | --- |
| `GLPI-Pilot.exe` | Application C# autonome, bibliothèques .NET et chargeur natif WebView2 intégrés |
| `wad/web/` | Pages locales, styles, modules JavaScript et ressources visuelles |
| `wad/runtime/webview2/` | Runtime fixe Microsoft 154.0.4258.62 x64, complet et vérifié |
| `wad/.playwright/` | Pilote Windows de la connexion au navigateur GLPI |
| `wad/docs/`, `wad/support/`, `wad/licenses/` | Guides, diagnostics et licences des composants |
| `wad/version.json`, `wad/SHA256SUMS.txt` | Version, provenance, dépendances et inventaire d’intégrité |
| `wad/sources/` dans DEV | Sources C#, sources web, tests et scripts de reconstruction |

## Premier réglage, puis un clic

1. Dans **Collecte GLPI**, renseigner l’adresse **complète de la recherche**, filtres compris, et choisir Edge ou Chrome. Enregistrer la source ; le dossier des exports se règle dans Configuration.
2. Cliquer **Lancer la collecte**. Le logiciel ouvre son navigateur, attend le SSO, retrouve la recherche et récupère toutes les pages par la méthode disponible.
3. Examiner le relevé, son état d’exhaustivité et le dashboard. Cliquer une valeur retrouve les tickets correspondants.
4. Dans **Équipe**, choisir son nom et **C’est moi** pour le travail personnel. Définir les villes, sites, bâtiments et variantes dans **Référentiel géographique** si besoin.
5. Aux sessions suivantes, réutiliser la source enregistrée. Les vues nommées, tableaux de bord et réglages de présentation restent mémorisés.

**Afficher le navigateur** permet d’intervenir si le SSO attend une saisie ; le logiciel ne renseigne aucun identifiant. Le navigateur piloté et la fenêtre de travail restent distincts. Une page GLPI personnalisée peut nécessiter l’apprentissage du tableau ou des sections une première fois.

## Passage à la version 3

La version **3.0.2** corrige une régression d’usage par rapport à la version 2.0 : un CSV déjà téléchargé et lu correctement pouvait rester inutilisable dans l’application parce que certaines valeurs différaient de leur affichage HTML. Lorsque les identifiants de l’échantillon correspondent, les lignes sont désormais disponibles pour le relevé, Excel, les dashboards et les villes, avec l’état **À vérifier** si des écarts subsistent. Les contrôles de périmètre restent actifs et le CSV natif reste intact. La lecture des demandeurs multiples et des textes GLPI abrégés par `(...)` est également corrigée. Les [notes 3.0.2](docs/RELEASE-3.0.2.md) précisent les conditions et les vérifications restantes.

Les corrections de profils hérités de la version 2.0, de fermeture après panne WebView2 et de reconstruction depuis les sources DEV restent décrites dans les [notes historiques 3.0.1](docs/RELEASE-3.0.1.md).

- Interface locale dans WebView2 : navigation, thèmes, densité, zoom, formulaires, tableaux paginés et dialogues utilisent les modules web du dépôt. Les opérations autorisées sont exposées par un pont C# fermé, avec annulation et événements d’état.
- Paquet autonome avec runtime fixe Microsoft, contrôlé par une empreinte du CAB et les 257 empreintes de ses fichiers. La compilation peut utiliser exclusivement le SDK et les paquets NuGet déjà présents dans la VM hors ligne.
- Tests séparés pour les moteurs C#, leurs opérations exposées aux pages, les interactions web et le vrai hôte Windows. Après une mise à jour, le démarrage de l’application n’est confirmé qu’après l’initialisation et le premier affichage de l’interface.

Les fonctions métier de la version précédente sont raccordées aux nouvelles pages ; leur couverture et leurs limites sont détaillées dans le suivi des 200 points :

- Profils complets par installation GLPI, campagnes avec bilan autonome par source, délais séparés, contrôle d’espace libre et reprise de pagination après vérification des pages conservées. La reconnexion SSO ou réseau est bornée et refuse un périmètre différent.
- Assistant de correspondance avec aperçu brut/interprété, conventions de nombres et de dates, fuseau, séparateurs multiples et dictionnaires propres à la source. Changements de colonnes détectés, propositions de renommage soumises à validation, panier persistant et lecture universelle des cellules par F2.
- Analyses longitudinales : périodes sans observation, mouvements, transferts, cohortes, incertitude des durées par statut, scénarios de stock et anomalies. Cartes locales reliées aux widgets, taux par effectifs datés et mini-historiques. Exports PNG/PDF avec cartouche et rapport HTML complet autonome.
- Tâches, attentes, validations, relations et documents structurés à partir des seules informations lues. Lecture restaurée par ticket, briefing de session, enrichissement ciblé et brouillons de suivis. Les affectations, suivis et tâches peuvent être préparés puis confirmés individuellement dans les formulaires GLPI reconnus, avec reçu et relecture.
- Calendriers de présence et capacités par demi-journée, estimations par catégorie, hiérarchie jusqu’au bâtiment, horaires des sites, états des tournées et répartition sur plusieurs jours. Adaptateurs facultatifs OSRM et Open-Meteo, avec choix du service et consentement explicite à l’envoi des coordonnées.
- Horaires fixes multiples, recettes conditionnelles, alertes de variation et de fraîcheur, résolution motivée, import avec différences détaillées et arbitrage des conflits. Assistant de requêtes et de synthèse local ou raccordé à un fournisseur choisi, avec aperçu et références contrôlées.
- Concentration F11, navigation personnalisable, palettes de couleurs adaptées, placement sur un moniteur disponible, diagnostic relisible et mise à jour vérifiée avec sauvegarde et retour arrière.

Le [suivi des 200 points](docs/Suivi-200-ameliorations.md) indique le code livré et les prérequis restants. La validation du portail/SSO et du matériel de l’entreprise nécessite cet environnement ; la signature de confiance de l’EXE nécessite un certificat de l’éditeur. Aucun de ces deux prérequis n’est présenté comme accompli par les tests synthétiques.

## Fonctions disponibles

### Interface et relevé

- Thèmes clair/sombre complets, bouton soleil/lune, suivi facultatif du thème Windows, cinq couleurs d’accent, contraste renforcé et réduction des animations.
- Zoom **75–150 %**, boutons − / 100 % / + et raccourcis ; densité compacte, équilibrée ou confortable, colonnes figées et mémorisation de la fenêtre.
- Navigation adaptée à la largeur, journal repliable, palette **Ctrl+K**, éditeurs de colonnes et de configuration intégrés à la fenêtre.
- Recherche, filtres par colonne et tri multiple calculés en C# sur **toutes les lignes chargées**, avec pages d’affichage bornées. Vues nommées mémorisant filtres, recherche, tri et présentation pour la structure du relevé.
- Export complet ou export explicite de la vue filtrée ; import local CSV et XLSX. Le fichier natif reçu reste intact.

### Dashboard global et espace personnel

- Dispositions indépendantes, modèles de départ, widgets déplaçables, redimensionnables, duplicables, masquables et verrouillables ; sections repliables et édition groupée.
- Catalogue avec favoris persistants, disponibilité réelle des sondes, filtres communs et par widget, filtres croisés, contextes nommés et comparaison A/B.
- Volumes, actifs, attentes, priorités, échéances, affectations, ancienneté, dates, lieux et couverture des données ; méthodes et effectifs accessibles.
- Anneaux, jauges, histogrammes, matrices colorées, barres empilées, Pareto, boîtes statistiques, médiane/P90 et nuage âge × échéance.
- Météos de santé du relevé configurables : synthèse, échéances, qualité, localisation. Elles ne représentent pas la météo climatique.
- Courbes et mini-historiques sur archives comparables, avec ruptures explicites. Export PNG/PDF avec date, filtres et complétude ; bilan CSV/Excel et rapport HTML autonome complet.

### Techniciens et travail personnel

- Identité personnelle mémorisée, tableaux de bord par technicien, gestion de l’équipe, favoris et objectifs locaux.
- Compétences, territoires, relais, absences, capacité déclarée et estimations de durée. Scénarios de répartition avec explications et comparaison avant/après.
- **Ma journée** : file ordonnée, sélection depuis le relevé, notes, checklists, modèles, rappels locaux, report et passation.
- Jusqu’à 12 onglets de tickets, épingles et sections préférées ; chronologie du traitement, filtres et recherche, changements observés entre deux lectures.
- Cache de lecture facultatif, explicitement daté ; rapprochements locaux entre tickets et solutions réellement lues, avec leurs références.

### Villes, sites et tournées

- Plusieurs termes pour une ville : `MA ; Mars ; Marseille = Marseille`. Reconnaissance des mots entiers, accents et noms composés ; une ambiguïté reste visible.
- Régions, départements, secteurs, hiérarchie libre, sites et bâtiments ; adresses et coordonnées facultatives, priorités des sources, conditions et exclusions des règles, simulateur avec termes surlignés et preuves.
- File des lieux à vérifier, corrections locales par installation et identifiant unique ; import CSV du référentiel avec comparatif avant application.
- Colonnes enrichies, indicateurs de couverture et répartitions par ville/site/territoire.
- Tournées datées à partir d’un panier explicite, états et historique motivé, départ/retour, durées, calendriers de sites et étapes verrouillées. Répartition sur plusieurs jours et techniciens selon compétences, absences, plages de présence et budgets du matin et de l’après-midi ; les capacités inconnues restent exclues de la proposition.
- Rapprochement avec un nouveau relevé, reconstruction explicite des étapes, briefing HTML imprimable et calendrier ICS. Les trajets sont saisis ou indicatifs par défaut. Un service OSRM compatible peut fournir distances et durées routières après configuration et consentement ; sa provenance et sa date restent visibles.

### Atelier de requêtes

**80 modèles** disponibles, jusqu’à 128 définitions mémorisées. Les modèles manquants peuvent être ajoutés au brouillon sans remplacer les requêtes existantes.

Le constructeur propose expressions imbriquées **ET / OU / NON**, comparaison entre champs, listes et intervalles, paramètres typés, champs calculés d’un catalogue borné, trois niveaux de regroupement, périodes, sommes/moyennes/médianes/percentiles et explication du résultat par ticket. Les résultats alimentent les widgets. Aucune requête SQL ni aucun script arbitraire n’est exécuté.

### Historique, qualité, alertes et automatisation

- Relevés locaux datés avec provenance, contrôle d’intégrité, règles d’interprétation conservées, protection et rétention paramétrable.
- Comparaison de relevés compatibles : apparitions, absences, champs modifiés, résolutions observées, réouvertures et réaffectations. **Une absence ne signifie pas une résolution.**
- Diagnostic de qualité par colonne : couverture, valeurs illisibles, identifiants répétés, dates incohérentes et accès aux lignes à examiner.
- Alertes locales sur indicateurs et requêtes : seuil, effectif minimal, exigence de complétude, prise en compte et report. Une donnée devenue indisponible ne résout pas une alerte.
- Plusieurs recherches mémorisées et campagnes séquentielles. Programmation facultative **pendant que l’application reste ouverte**, plages silencieuses et suspension lorsque la session Windows est verrouillée.
- Journal d’opérations, détection des opérations interrompues, sauvegardes versionnées des réglages et restauration avec aperçu.
- Import de configuration par sections, comparatif et fusion facultative. Identité, SSO, dossiers, zoom, notes personnelles et planification locale restent sur le poste.

## Fiabilité de l’extraction

Le mode automatique privilégie le CSV **toutes les pages** proposé par GLPI. S’il est indisponible, il peut utiliser une option d’affichage complet existante puis, si autorisé, parcourir la pagination depuis la première page. **Un fichier corrompu ou un export ambigu n’entraîne pas de repli silencieux.**

Le moteur vérifie la recherche, le contexte observable, les totaux, les colonnes et les doublons de pages. Un échantillon de 25 identifiants au maximum et les valeurs des colonnes comparables sont rapprochés du CSV. Un identifiant attendu absent lors du rapprochement initial fait refuser l’import. Lorsque les identifiants correspondent mais que des valeurs diffèrent, le CSV valide est chargé avec `Complete=false` et l’état **À vérifier** : les données, les exports et les analyses restent utilisables avec cette réserve. Aucun écart ni caractère de contrôle C1 n’est effacé pour obtenir une concordance.

Les textes complets présents dans les popups GLPI sont lus seulement lorsque leur structure et leur association au texte visible sont prouvées. Les abréviations non développées ne sont exclues du contrôle de valeur que dans les cas reconnus au préfixe compatible. Le contrôle final du contexte, du total et de l’échantillon reste actif ; une variation ou une vérification devenue indisponible empêche de certifier un relevé complet. Le classeur automatique d’un résultat à contrôler porte `a-verifier` dans son nom ; le CSV natif conserve son nom et ses octets. Un fichier local importé n’est pas certifié comme extraction exhaustive du portail.

La limite de lignes de l’application peut réduire le volume chargé ; elle ne tronque pas le CSV natif téléchargé. Une annulation ne déclenche pas d’annonce de réussite. Le diagnostic technique distingue les étapes sans inclure les cellules, cookies ou jetons de la session.

## Données et partage

Les données de travail résident dans `%LOCALAPPDATA%\GLPI-Pilot`, indépendamment du dossier de l’EXE : profil du navigateur, préférences, archives, cache facultatif, notes et tournées. Les exports vont dans le dossier choisi. La configuration partageable contient les définitions d’équipe et les adresses autorisées, pas la session SSO.

Dans **Mise à jour et retour arrière**, examiner la release officielle ou choisir un ZIP local, vérifier le pack puis confirmer son installation. L’assistant contrôle l’archive, son commit et chaque fichier, conserve une sauvegarde complète et exige un démarrage confirmé de l’interface. Après un échec, il restaure l’ancien pack ; si un fichier reste verrouillé, il conserve la sauvegarde et signale la récupération nécessaire. Aucun contrôle de release n’est lancé automatiquement au démarrage.

Le remplacement manuel reste possible : fermer l’application et extraire le nouveau ZIP dans un nouveau dossier. Ne pas mélanger ses fichiers avec un ancien `wad`. Une sauvegarde des préférences existantes précède la première ouverture d’une nouvelle version. Un second lancement rappelle la fenêtre déjà ouverte. Si aucune fenêtre n’apparaît, utiliser `wad/support/DIAGNOSTIC_DEMARRAGE.cmd` ; le dernier journal est `Logs/demarrage-dernier.log` dans les données locales.

## Documentation

- [Guide utilisateur](docs/Guide-GLPI-Pilot.html)
- [Notes de version 3.0.2](docs/RELEASE-3.0.2.md)
- [Historique — version 3.0.1](docs/RELEASE-3.0.1.md)
- [Migration initiale vers WebView2 — version 3.0.0](docs/RELEASE-3.0.0.md)
- [Architecture et contrat des opérations WebView](docs/WebView-contract.md)
- [Compilation, caches hors ligne et chaîne Windows](docs/GitHub-et-compilation.txt)
- [Dashboards, indicateurs et extraction](docs/Dashboards-et-extraction.md)
- [Travail personnel, tickets et techniciens](docs/Travail-personnel-et-techniciens.md)
- [Requêtes, villes, sites et tournées](docs/Requetes-villes-tournees.md)
- [Historique, qualité et automatisation](docs/Historique-qualite-automatisation.md)
- [Protocole et résultats de vérification](docs/Rapport-verification.txt)
- [Suivi des 200 améliorations : couverture et reste à réaliser](docs/Suivi-200-ameliorations.md)

## Développer et vérifier

Projet **C# .NET 10, WinForms et WebView2**. Les sources d’interface se trouvent dans `web/` ; aucune chaîne npm n’est nécessaire pour construire le produit. Le SDK WebView2 est épinglé à `1.0.4258.31`, le runtime fixe à `154.0.4258.62` et Playwright à `1.63.0`.

### VM Windows hors ligne

Avec PowerShell 7.2 ou ultérieur, depuis la racine des sources, utiliser les chemins du laboratoire décrits dans [REPRISE.md de Domenia 2](https://github.com/M4D135/domenia-d120/blob/v2/v2/REPRISE.md) :

```powershell
$env:DOTNET_CLI_HOME = 'C:\DomeniaDev\dotnet-home'
$env:NUGET_PACKAGES = 'C:\DomeniaDev\nuget-packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

.\build.ps1 `
  -DotNetPath 'C:\DomeniaDev\dotnet\dotnet.exe' `
  -NuGetOfflineSource 'C:\DomeniaDev\cache\nuget' `
  -RuntimePath 'C:\DomeniaDev\cache\webview2\runtime' `
  -OutputDirectory 'C:\DomeniaDev\builds\glpi-pilot-3.0.2'
```

Le runtime doit être déjà extrait, soit directement dans `RuntimePath`, soit dans son unique sous-dossier. Tous les paquets nécessaires doivent être présents dans le cache NuGet. Une dépendance absente arrête la compilation ; aucun téléchargement de secours n’est tenté. Le script refuse d’écraser une distribution existante : choisir un nouveau dossier de sortie pour une nouvelle construction.

La VM de référence dispose de Windows 11, 4 vCPU et 8 Go. Conserver la session console ouverte et réserver les essais interactifs en VM à la fin du chantier. Cette documentation décrit les commandes à y exécuter ; elle n’atteste aucune connexion à cette VM depuis un autre atelier.

### Préparation connectée et CI

Sur une machine Windows autorisée à télécharger les dépendances :

```powershell
$runtime = .\build-tools\Get-WebView2Runtime.ps1 `
  -CacheDirectory 'C:\GLPI-BuildCache\webview2'

.\build.ps1 -RuntimePath $runtime -AllowOnlineRestore `
  -OutputDirectory '.\artifacts\release-3.0.2'
```

`Get-WebView2Runtime.ps1` utilise uniquement l’URL Microsoft épinglée, vérifie le CAB et chaque fichier extrait, puis réutilise le cache contrôlé. `-CabPath` permet de fournir le CAB par transfert local ; `-Offline` interdit son téléchargement. `build.ps1` ne télécharge jamais le runtime. L’accès NuGet en ligne nécessite `-AllowOnlineRestore` ; il est incompatible avec `-NuGetOfflineSource`.

Le [workflow Windows](.github/workflows/windows.yml) prévoit les moteurs C#, les pages branchées aux vrais services, les parcours navigateur synthétiques, puis les packs et le vrai hôte WinForms/WebView2. Les contrôles natifs vérifient thèmes, zoom, navigation, fermeture et mise à jour/rollback sur des copies temporaires. La promotion en Release n’utilise que les archives de ce run après réussite de ses étapes. Le détail des commandes, des caches et des prérequis figure dans [GitHub et compilation](docs/GitHub-et-compilation.txt).

Une compilation croisée ne prouve pas le fonctionnement de l’EXE sous Windows. Les résultats définitifs sont les journaux du [workflow associé au commit livré](https://github.com/M4D135/GLPI-Pilot/actions/workflows/windows.yml). Le portail d’entreprise et son SSO réel restent à confirmer dans cet environnement.

## Limites explicites

Les données absentes restent indisponibles. Les dates sont calendaires ; un stock ou une estimation ne mesure pas la productivité d’un technicien. Les analyses historiques décrivent des observations et leurs incertitudes : une disparition n’est pas une résolution, une projection n’est pas un engagement de résultat.

Les notes, tournées, corrections et scénarios restent locaux. Seule une action GLPI préparée dans l’assistant dédié puis confirmée explicitement peut écrire dans le portail, selon les droits du compte. Un résultat d’envoi ambigu est signalé et n’est jamais relancé automatiquement.

Les services de routage, météo et génération externe sont désactivés tant qu’aucun fournisseur n’est configuré et autorisé. Le mode local de l’assistant repose sur des règles explicites et une synthèse extractive ; il n’est pas un modèle généraliste exécuté hors ligne. Une réponse externe doit respecter le schéma borné et citer des références présentes dans le contexte ; sa pertinence doit être relue avant usage.

Les packs conservent commit et empreintes. La chaîne Authenticode est prête, mais aucun certificat de confiance n’a été fourni pour cette livraison : les empreintes ne constituent pas une signature d’éditeur. Le comportement du portail privé, de son SSO et des configurations physiques de plusieurs écrans reste à vérifier sur les postes concernés. Outil indépendant, sans affiliation au projet GLPI.
