# GLPI Pilot 2.1.0 — Windows x64

**Un espace de travail pour exploiter les recherches GLPI accessibles par navigateur et SSO.** Extraction complète, dashboards global et personnel, gestion locale des techniciens, lecture des tickets, requêtes, lieux, tournées, historiques et alertes. Aucun accès API, base de données ou serveur GLPI n’est nécessaire.

## Télécharger le pack complet

Les [Releases](https://github.com/M4D135/GLPI-Pilot/releases) contiennent les packs Windows validés. La publication de la version 2.1.0 exige les contrôles de ses moteurs, de son interface et du démarrage de son EXE. Une archive nommée **PREVERSION** indique une compilation locale dont l’exécution Windows reste à vérifier ; son statut figure dans `wad/version.json` et `wad/docs/ETAT-PREVERSION.txt`.

| Pack | Pour qui | Contenu à la racine |
| --- | --- | --- |
| **PROD — Windows x64** | Utiliser le logiciel | `GLPI-Pilot.exe` + `wad/` |
| **DEV — Windows x64** | Utiliser le même logiciel et poursuivre son développement | `GLPI-Pilot.exe` + `wad/`, sources et tests dans `wad/sources/` |

Extraire **tout le ZIP** dans un dossier puis ouvrir **GLPI-Pilot.exe**. Garder `wad` à côté. Aucun Python, Node.js ou .NET à installer pour utiliser le pack. Edge ou Chrome doit être installé et autorisé sur le poste. **Code → Download ZIP** fournit les sources, les packs exécutables sont dans les Releases. Les deux éditions contiennent le même EXE et leurs empreintes SHA-256.

## Premier réglage, puis un clic

1. Coller l’adresse **complète de la recherche GLPI**, filtres compris. Choisir Edge ou Chrome et le dossier des exports dans Réglages si nécessaire.
2. Cliquer **Extraire tout**. Le logiciel mémorise la source, ouvre son navigateur, attend le SSO, retrouve la recherche et récupère toutes les pages par la méthode disponible.
3. Examiner le relevé, son état d’exhaustivité et le dashboard. Cliquer une valeur retrouve les tickets correspondants.
4. Dans Techniciens, choisir son nom et **C’est moi** pour le tableau de bord personnel. Définir ses villes et variantes dans le référentiel si besoin.
5. Aux sessions suivantes, **Extraire tout** reprend les réglages. Les vues nommées, tableaux de bord et réglages de présentation restent mémorisés.

Le navigateur reste ouvert derrière GLPI Pilot pendant l’extraction. **Voir le navigateur / Revenir à GLPI Pilot** permet d’intervenir si le SSO attend une saisie ; le logiciel ne renseigne aucun identifiant. La priorité de fenêtre est relâchée à la fin du traitement. Une page GLPI personnalisée peut nécessiter l’apprentissage du tableau ou des sections une première fois.

## Nouveautés de la version 2.1

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
- Recherche, filtres par colonne et tri multiple sur **toutes les lignes chargées**, avant la limite d’affichage de 10 000 lignes. Vues nommées mémorisant filtres, recherche, tri et présentation pour la structure du relevé.
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
- Régions, départements, secteurs, sites, adresses et coordonnées facultatives ; priorités, conditions et exclusions des règles, simulateur avec preuves.
- File des lieux à vérifier, corrections locales par installation et identifiant unique ; import CSV du référentiel avec comparatif avant application.
- Colonnes enrichies, indicateurs de couverture et répartitions par ville/site/territoire.
- Tournées datées à partir d’un panier explicite, départ/retour, durées, fenêtres horaires, étapes verrouillées, scénarios et répartition par durées.
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

Le moteur vérifie la recherche, le contexte observable, les totaux, les colonnes et les doublons de pages. Un échantillon de 25 identifiants au maximum et les valeurs des colonnes comparables sont rapprochés du CSV. Les cellules visiblement abrégées sont exclues de la comparaison de valeurs. Une variation du périmètre interrompt la lecture ; les lignes déjà récupérées restent identifiées comme partielles. Les exports à contrôler portent `a-verifier` dans leur nom. Un fichier local importé n’est pas certifié comme extraction exhaustive du portail.

La limite de lignes de l’application peut réduire le volume chargé ; elle ne tronque pas le CSV natif téléchargé. Une annulation ne déclenche pas d’annonce de réussite. Le diagnostic technique distingue les étapes sans inclure les cellules, cookies ou jetons de la session.

## Données et partage

Les données de travail résident dans `%LOCALAPPDATA%\GLPI-Pilot`, indépendamment du dossier de l’EXE : profil du navigateur, préférences, archives, cache facultatif, notes et tournées. Les exports vont dans le dossier choisi. La configuration partageable contient les définitions d’équipe et les adresses autorisées, pas la session SSO.

Dans **Confort et maintenance → Vérifier une mise à jour**, examiner la release officielle, vérifier le pack puis confirmer son installation. L’assistant conserve une sauvegarde et exige un démarrage confirmé ; un échec déclenche une restauration. Le remplacement manuel reste possible : fermer l’application et extraire le nouveau ZIP dans un nouveau dossier. Ne pas mélanger ses fichiers avec un ancien `wad`. Une sauvegarde des préférences existantes précède la première ouverture d’une nouvelle version. Un second lancement rappelle la fenêtre déjà ouverte. Si aucune fenêtre n’apparaît, utiliser `wad/support/DIAGNOSTIC_DEMARRAGE.cmd` ; le dernier journal est `Logs/demarrage-dernier.log` dans les données locales.

## Documentation

- [Guide utilisateur](docs/Guide-GLPI-Pilot.html)
- [Notes de version 2.1.0](docs/RELEASE-2.1.0.md)
- [Nouveaux parcours, services facultatifs et maintenance](docs/Guide-2.1.md)
- [Dashboards, indicateurs et extraction](docs/Dashboards-et-extraction.md)
- [Travail personnel, tickets et techniciens](docs/Travail-personnel-et-techniciens.md)
- [Requêtes, villes, sites et tournées](docs/Requetes-villes-tournees.md)
- [Historique, qualité et automatisation](docs/Historique-qualite-automatisation.md)
- [Protocole et résultats de vérification](docs/Rapport-verification.txt)
- [Suivi des 200 améliorations : couverture et reste à réaliser](docs/Suivi-200-ameliorations.md)

## Développer et vérifier

Projet C# WPF, .NET 10. Avec le SDK .NET 10 et PowerShell 7.2 ou ultérieur sous Windows :

```powershell
.\build.ps1
```

Le script fabrique PROD et DEV avec l’EXE autonome et le pilote Playwright externe dans `wad/.playwright`. Le workflow Windows exécute les tests métier, les parcours navigateur sur pages synthétiques, le rendu réel des vues dans les deux thèmes, le démarrage du pack extrait et des remplacements/restaurations réels sur des copies de ce pack. Il vérifie également un second lancement, le zoom et sa persistance, les empreintes des fichiers et le commit source avant de publier les deux ZIP dans une Release.

Une compilation croisée ne prouve pas le fonctionnement de l’EXE sous Windows. Les résultats définitifs sont les journaux du [workflow associé au commit livré](https://github.com/M4D135/GLPI-Pilot/actions/workflows/windows.yml). Le portail d’entreprise et son SSO réel restent à confirmer dans cet environnement.

## Limites explicites

Les données absentes restent indisponibles. Les dates sont calendaires ; un stock ou une estimation ne mesure pas la productivité d’un technicien. Les analyses historiques décrivent des observations et leurs incertitudes : une disparition n’est pas une résolution, une projection n’est pas un engagement de résultat.

Les notes, tournées, corrections et scénarios restent locaux. Seule une action GLPI préparée dans l’assistant dédié puis confirmée explicitement peut écrire dans le portail, selon les droits du compte. Un résultat d’envoi ambigu est signalé et n’est jamais relancé automatiquement.

Les services de routage, météo et génération externe sont désactivés tant qu’aucun fournisseur n’est configuré et autorisé. Le mode local de l’assistant repose sur des règles explicites et une synthèse extractive ; il n’est pas un modèle généraliste exécuté hors ligne. Une réponse externe doit respecter le schéma borné et citer des références présentes dans le contexte ; sa pertinence doit être relue avant usage.

Les packs conservent commit et empreintes. La chaîne Authenticode est prête, mais aucun certificat de confiance n’a été fourni pour cette livraison : les empreintes ne constituent pas une signature d’éditeur. Le comportement du portail privé, de son SSO et des configurations physiques de plusieurs écrans reste à vérifier sur les postes concernés. Outil indépendant, sans affiliation au projet GLPI.
