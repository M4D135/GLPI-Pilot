# Suivi des 200 améliorations de GLPI Pilot

État du code 2.1.0 examiné le **22 octobre 2026**. Ce registre reprend les 200 propositions transmises pour poursuivre le projet ; leurs numéros restent stables. Les intitulés sont conservés pour faciliter le rapprochement avec la demande.

**Ce registre décrit la couverture fonctionnelle du code.** Il ne remplace pas les résultats de compilation, de rendu Windows ou d’essai sur le portail réel. La publication d’un pack et ses contrôles doivent être vérifiés dans le [rapport de vérification](Rapport-verification.txt) et le workflow du commit livré.

## Comment lire les états

| État | Signification | Nombre |
| --- | --- | ---: |
| **Implémenté** | Fonction implémentée et reliée à son interface ou à la chaîne de distribution, dans le périmètre précisé. | **197** |
| **Partiel** | Une partie fonctionne ; une exigence de la proposition initiale reste absente ou plus limitée. | **2** |
| **À poursuivre** | Fonction demandée non implémentée ; les briques voisines ne suffisent pas à la considérer livrée. | **0** |
| **Option externe** | Prérequis externe non fourni ; le code d’intégration peut être présent sans que ce prérequis soit satisfait. | **1** |
| **Total** | Une entrée par proposition, sans double comptage. | **200** |

Les fonctionnalités facultatives raccordées (routage, météo, assistant et formulaires GLPI) restent désactivées tant que leur configuration et leur action explicite ne sont pas réalisées. Leur présence ne prouve pas la validation du portail privé. Le certificat du point 198 et la validation terrain du point 197 restent les deux prérequis ouverts. Les liens de chaque chantier pointent vers les preuves de code les plus pertinentes ; ils ne prétendent pas que chaque fonction existe dans tous ces fichiers.

## 1. Connexion, SSO et collecte

**Repères dans le dépôt :** [Moteur automatique](../src/GlpiPilot/Core/AutomaticExtractionService.cs) · [SSO et navigation](../src/GlpiPilot/Core/BrowserNavigation.cs) · [Prévol et profil](../src/GlpiPilot/Core/ExtractionVerificationService.cs) · [Profils de source](../src/GlpiPilot/Core/AppSettings.cs) · [Campagnes](../src/GlpiPilot/MainWindow.ControlCenter.cs) · [Reprise contrôlée](../src/GlpiPilot/Core/BrowserRecovery.cs) · [Preuves de campagne](../src/GlpiPilot/Core/CampaignReport.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 1 | Plusieurs profils de source | **Implémenté** | Profils complets isolant acquisition, repères, délais, dossier local, conventions et correspondances ; restauration facultative des requêtes et dashboards du profil. |
| 2 | Campagnes en un clic | **Implémenté** | Campagne séquentielle avec poursuite des autres sources après échec et bilan autonome JSON/CSV pour chaque succès, erreur, annulation ou source non commencée. |
| 3 | Vérification avant démarrage | **Implémenté** | Prévol de l’adresse, des plafonds, du profil navigateur, de l’écriture et de l’espace disque libre minimal configurable. |
| 4 | Suivi précis du SSO | **Implémenté** | Étapes SSO réellement observées, durée de chaque étape et durée totale conservées dans le reçu ; aucune identification supposée d’un fournisseur. |
| 5 | Reprise humaine simple | **Implémenté** | Commande Voir le navigateur, suivi des onglets et reprise de l’attente jusqu’au retour à la recherche. |
| 6 | Reconnexion en cours de collecte | **Implémenté** | Reconnexion SSO et réseau bornée pendant la pagination, retour à l’adresse attendue et refus de poursuivre si recherche, contexte, schéma ou pages de contrôle diffèrent. |
| 7 | Délais adaptés aux étapes | **Implémenté** | Délais distincts pour SSO, listes, détails et téléchargement ; bornes validées dans les réglages et profils de source. |
| 8 | Contrôle des filtres effectifs | **Implémenté** | Empreintes de recherche et de contexte, contrôles d’adresse et des critères, interruption lors d’une variation observée. |
| 9 | Isolation des portails | **Implémenté** | Profils navigateur distincts par installation GLPI et par navigateur ; verrou de profil conservé. |
| 10 | Méthode d’extraction expliquée | **Implémenté** | Le parcours indique la méthode utilisée et les raisons d’un repli : export natif, affichage complet ou pagination. |

## 2. Extraction complète et récupération après incident

**Repères dans le dépôt :** [Pagination](../src/GlpiPilot/Core/BrowserService.cs) · [Export natif](../src/GlpiPilot/Core/BrowserNativeExport.cs) · [Export atomique](../src/GlpiPilot/Core/ExportService.cs) · [Import XLSX](../src/GlpiPilot/Core/XlsxImportService.cs) · [Contrôles de fondation](../tests/FoundationChecks/Program.cs) · [Points de reprise](../src/GlpiPilot/Core/PaginationCheckpoint.cs) · [Vérifications navigateur](../tests/BrowserChecks/Program.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 11 | Reprise d’une pagination interrompue | **Implémenté** | Points de reprise compressés et atomiques, bornés en âge/volume ; vérification du périmètre, du total, de la première page et de la dernière page acquise avant reprise. |
| 12 | Pagination automatique de dernier recours | **Implémenté** | Repli lorsque l’export natif est réellement indisponible, avec plafonds, détection de pages répétées et arrêt motivé. |
| 13 | Conditions de collecte visibles | **Implémenté** | Début, fin, méthode, périmètre, tri effectivement observé et durées des phases enregistrés avec la collecte. |
| 14 | Vérification par échantillon | **Implémenté** | Comparaison des identifiants et cellules comparables de 25 lignes visibles au maximum ; divergences bloquantes et textes tronqués exclus du contrôle de valeur. |
| 15 | Protection du dernier relevé valide | **Implémenté** | Les exports sont finalisés atomiquement ; la rétention préserve aussi le dernier relevé complet, vérifié et comparable malgré des essais partiels ultérieurs. |
| 16 | Résultat clairement qualifié | **Implémenté** | Complétude, résultat vide confirmé, résultat partiel et erreur sont distingués dans les résultats, avertissements et journaux. |
| 17 | Annulation pendant l’écriture | **Implémenté** | Les écritures CSV/XLSX et historiques prennent en compte l’annulation ; les fichiers temporaires restent distincts des destinations finalisées. |
| 18 | Progression fondée sur le travail réel | **Implémenté** | La progression utilise les pages et lignes réellement reçues et le total disponible ; elle n’annonce pas de délai restant artificiel. |
| 19 | Identifiants sans collision | **Implémenté** | Identités composées de l’installation, du type d’objet et de l’ID normalisé ; résultats locaux, démonstrations et recherches multi-types ambiguës exclus des cibles distantes. |
| 20 | Import local Excel | **Implémenté** | Import XLSX de la première feuille visible, valeurs enregistrées, limites et erreurs contrôlées, provenance locale explicitement non certifiée GLPI. |

## 3. Qualité et compréhension des données

**Repères dans le dépôt :** [Qualité](../src/GlpiPilot/Core/DataQualityService.cs) · [Vue Qualité](../src/GlpiPilot/UI/DataQualityView.cs) · [Interprétation des colonnes](../src/GlpiPilot/Core/DashboardService.cs) · [Réglages des indicateurs](../src/GlpiPilot/Core/DashboardSettings.cs) · [Schémas et identités](../src/GlpiPilot/Core/DataWorkspaceService.cs) · [Assistant de correspondance](../src/GlpiPilot/UI/MappingAssistantView.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 21 | Détection des changements de colonnes | **Implémenté** | État des colonnes mémorisé par source ; ajouts, retraits, répétitions et réordonnancement détectés, renommages proposés sans application implicite. |
| 22 | Assistant de correspondance des champs | **Implémenté** | Assistant intégré présentant propositions de rôles et aperçu brut/interprété ; application explicite au profil correspondant ou au seul import local, archives en lecture seule. |
| 23 | Profil de qualité par colonne | **Implémenté** | Rapport par colonne : présence, vide, valeurs illisibles, diversité bornée, exemple et accès aux lignes problématiques. |
| 24 | Formats propres à chaque source | **Implémenté** | Culture de dates, convention numérique et fuseau configurables par source ; valeurs illisibles signalées, cellules d’origine préservées. |
| 25 | Valeur d’origine accessible | **Implémenté** | Lecteur de cellule F2 avec valeur brute, interprétation et provenance, accessible depuis tout le tableau du relevé. |
| 26 | Gestion configurable des champs multiples | **Implémenté** | Séparateurs multivaleurs configurables par source et utilisés par les affectations et requêtes ; comptages multiples explicités. |
| 27 | Dictionnaires de normalisation | **Implémenté** | Dictionnaires éditables de normalisation des statuts, catégories et personnes ; aperçu et règles conservées avec la source et les archives. |
| 28 | Règles de cohérence | **Implémenté** | Contrôles des dates futures, dates avant création, clôture avant résolution et valeurs non interprétables ; source conservée. |
| 29 | Analyse des doublons | **Implémenté** | Les identifiants répétés sont distingués entre lignes identiques et lignes divergentes, avec référence à la ligne précédente. |
| 30 | Trois niveaux de confiance | **Implémenté** | L’espace Qualité sépare complétude, couverture des champs et fiabilité d’interprétation. |

## 4. Historique des relevés

**Repères dans le dépôt :** [Archives et comparaisons](../src/GlpiPilot/Core/SnapshotStore.cs) · [Historique dans l’interface](../src/GlpiPilot/UI/HistoryWorkspaceView.cs) · [Courbes globales](../src/GlpiPilot/MainWindow.Dashboard.cs) · [Contrôles de fondation](../tests/FoundationChecks/Program.cs) · [Analyses longitudinales](../src/GlpiPilot/Core/SnapshotAnalytics.cs) · [Historique filtré](../src/GlpiPilot/Core/SnapshotScopedHistory.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 31 | Bibliothèque de relevés datés | **Implémenté** | Archives compressées datées avec provenance, règles, indicateurs, empreinte et catalogue consultable. |
| 32 | Comparaison entre deux relevés | **Implémenté** | Comparaison de deux relevés avec apparitions, absences et changements champ par champ, compteurs complets et affichage borné. |
| 33 | Contrôle de comparabilité | **Implémenté** | Contrôle de source, contexte, complétude, schéma, règles, identité et ordre chronologique avant comparaison métier. |
| 34 | Absence correctement interprétée | **Implémenté** | Les absents restent absents du périmètre ; commande explicite vers la file de lecture ciblée pour vérifier leur état actuel sans réécrire l’archive. |
| 35 | Courbe réelle du stock observé | **Implémenté** | Séries datées et ruptures explicites lorsque la cadence attendue n’est pas couverte ou qu’un relevé est incompatible ; aucune interpolation présentée comme observation. |
| 36 | Entrées et sorties du périmètre | **Implémenté** | Les comparaisons comptent apparitions et absences du périmètre, en les distinguant des créations et résolutions supposées. |
| 37 | Réouvertures observées | **Implémenté** | Réouverture comptée uniquement lorsqu’un même identifiant passe d’un statut résolu/clos à un statut actif entre deux relevés. |
| 38 | Historique des affectations observées | **Implémenté** | Changements de technicien observés entre deux relevés, avec leurs dates de constat ; aucune heure de transition n’est inventée. |
| 39 | Règles attachées aux relevés | **Implémenté** | Correspondances, paramètres de calcul et dictionnaire de villes sont attachés aux archives avec une empreinte des règles. |
| 40 | Politique de conservation | **Implémenté** | Rétention par âge, nombre et budget disque ; protection explicite et conservation des derniers relevés utiles, pouvant dépasser le budget. |

## 5. Interface générale et confort Windows

**Repères dans le dépôt :** [Préférences et fenêtre](../src/GlpiPilot/MainWindow.ControlCenter.cs) · [Réglages de présentation](../src/GlpiPilot/UI/ControlCenterView.cs) · [Navigation et palette](../src/GlpiPilot/MainWindow.Workspace.cs) · [Thèmes](../src/GlpiPilot/App.xaml) · [Moniteurs](../src/GlpiPilot/UI/MonitorPlacement.cs) · [Navigation et reprise](../src/GlpiPilot/MainWindow.Completion.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 41 | Densité indépendante du zoom | **Implémenté** | Trois densités de lignes, indépendante du zoom mémorisé de l’interface. |
| 42 | Mode concentration | **Implémenté** | Mode concentration F11 masquant navigation et panneaux secondaires ; préférence mémorisée et sortie au clavier. |
| 43 | Disposition adaptée aux petits écrans | **Implémenté** | Navigation adaptée à la largeur, panneaux intégrés et contrôles de disposition à plusieurs tailles/zooms ; validation réelle à consulter dans le workflow. |
| 44 | Thème automatique Windows | **Implémenté** | Option de suivi du thème Windows avec maintien du choix manuel clair/sombre. |
| 45 | Couleur d’accent personnalisable | **Implémenté** | Cinq accents configurables appliqués aux ressources de l’interface, avec valeurs adaptées aux thèmes clair et sombre. |
| 46 | Accessibilité des couleurs | **Implémenté** | Palettes adaptées aux déficiences de vision des couleurs, contraste renforcé et repères textuels ; retour au thème standard réapplique toutes les ressources. |
| 47 | Animations réglables | **Implémenté** | Réduction locale des animations ou suivi de la préférence Windows ; application aux transitions de l’interface. |
| 48 | Gestion des écrans multiples | **Implémenté** | Sélection parmi les moniteurs réellement présents, coordonnées physiques et repositionnement dans une zone de travail disponible après déconnexion d’écran. |
| 49 | Navigation personnalisable | **Implémenté** | Ordre, masquage et épinglage des rubriques mémorisés dans une vue intégrée, avec conservation d’un accès aux commandes. |
| 50 | Palette de commandes intégrée | **Implémenté** | Palette de commandes Ctrl+K intégrée dans la fenêtre principale, avec fermeture et retour du focus. |

## 6. Tableaux, colonnes et navigation

**Repères dans le dépôt :** [Tableau du relevé](../src/GlpiPilot/MainWindow.Preview.cs) · [Colonnes](../src/GlpiPilot/UI/PreviewColumnsDialog.cs) · [Vues nommées](../src/GlpiPilot/Core/NamedPreviewStore.cs) · [Filtres et tris](../src/GlpiPilot/Core/PreviewQueryService.cs) · [Lecture des cellules](../src/GlpiPilot/MainWindow.xaml) · [Persistance des tableaux](../src/GlpiPilot/UI/GridWorkspaceMemory.cs) · [Lecteur et panier](../src/GlpiPilot/MainWindow.DataWorkspace.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 51 | Vues de colonnes nommées | **Implémenté** | Vues nommées enregistrant colonnes, dimensions, ordre, recherche, filtres et tri pour une structure d’en-têtes donnée. |
| 52 | Sélecteur de colonnes intégré | **Implémenté** | Sélecteur intégré avec recherche et familles de colonnes, affichage, déplacement et aperçu du choix. |
| 53 | Colonnes figées | **Implémenté** | Nombre de colonnes figées configurable pour le tableau du relevé. |
| 54 | Filtres directement dans les colonnes | **Implémenté** | Filtres dans les en-têtes du relevé, appliqués à toutes les lignes avant la limite d’affichage ; copie des en-têtes conserve les vrais noms de colonnes. |
| 55 | Tri sur plusieurs critères | **Implémenté** | Jusqu’à quatre tris appliqués à toutes les lignes, avec ajout par Maj + clic et réglage dans la vue. |
| 56 | Panier de sélection persistant | **Implémenté** | Panier persistant d’identités vérifiées, indépendant des filtres et isolé par installation/type ; doublons et références ambiguës refusés. |
| 57 | Groupes de colonnes repliables | **Implémenté** | Activation ou masquage groupé par familles identité, affectation, dates et localisation dans le sélecteur de colonnes. |
| 58 | Lecture confortable des cellules longues | **Implémenté** | Lecteur universel F2 des textes longs et valeurs interprétées, avec copie depuis un panneau intégré. |
| 59 | Présentation mémorisée par vue | **Implémenté** | Ordre et largeur de colonnes mémorisés par tableau ; sélection et position de lecture séparées par source et restaurées à la réouverture. |
| 60 | Navigation complète au clavier | **Implémenté** | Raccourcis WPF, activation par Entrée, lecteur F2 et restauration des positions/sélections ; les résultats locaux distincts possèdent un contexte de sélection séparé. |

## 7. Construction et personnalisation des dashboards

**Repères dans le dépôt :** [Périmètres et édition groupée](../src/GlpiPilot/UI/DashboardView.Scopes.cs) · [Éditeur et catalogue](../src/GlpiPilot/UI/DashboardView.Editor.cs) · [Disposition](../src/GlpiPilot/Core/DashboardLayoutSettings.cs) · [Intégration et export](../src/GlpiPilot/MainWindow.Dashboard.cs) · [Export PDF/PNG](../src/GlpiPilot/UI/DashboardView.Export.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 61 | Barre de filtres commune | **Implémenté** | Filtres communs de distribution, population d’indicateur et période appliqués aux widgets compatibles du dashboard. |
| 62 | Filtres croisés entre widgets | **Implémenté** | Clic de filtrage sur catégories et populations, conditions visibles et remise à zéro du périmètre. |
| 63 | Filtres propres à chaque widget | **Implémenté** | Conditions propres à chaque widget ajoutées au périmètre commun ; filtre devenu indisponible signalé explicitement. |
| 64 | Comparaison de deux périmètres | **Implémenté** | Comparaison A/B de deux périmètres sur le même relevé, avec protection du périmètre personnel. |
| 65 | Sections de dashboard | **Implémenté** | Sections nommées repliables et mémorisées, avec déplacement de tous les widgets d’une section en bloc. |
| 66 | Modification de plusieurs widgets | **Implémenté** | Édition groupée de section, style, dimensions et alignement pour les widgets sélectionnés. |
| 67 | Favoris de widgets persistants | **Implémenté** | Favoris persistants et catalogue des widgets récemment utilisés, adapté aux données disponibles. |
| 68 | Catalogue adapté aux données présentes | **Implémenté** | Catalogue filtrable sur la disponibilité réelle et explications des mesures/champs manquants. |
| 69 | Contextes de consultation enregistrés | **Implémenté** | Contextes de filtres nommés mémorisés indépendamment du placement des widgets. |
| 70 | Export visuel du dashboard | **Implémenté** | Exports PNG et PDF natif avec cartouche de date, périmètre, filtres et complétude ; export tabulaire du bilan conservé. |

## 8. Nouveaux widgets visuels

**Repères dans le dépôt :** [Rendus analytiques](../src/GlpiPilot/UI/DashboardView.Analytics.cs) · [Séries et calculs](../src/GlpiPilot/Core/DashboardService.cs) · [Catalogue](../src/GlpiPilot/UI/DashboardView.Editor.cs) · [Contrôles des indicateurs](../tests/DashboardChecks/Program.cs) · [Mini-historiques](../src/GlpiPilot/UI/DashboardView.MiniHistory.cs) · [Carte](../src/GlpiPilot/UI/DashboardView.Map.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 71 | Matrice techniciens × villes | **Implémenté** | Matrice techniciens × villes avec mode de comptage explicite et accès aux tickets de chaque cellule. |
| 72 | Barres empilées | **Implémenté** | Choix entre barres empilées en volume et en pourcentage, avec effectifs et conventions de comptage affichés. |
| 73 | Histogramme d’ancienneté | **Implémenté** | Histogrammes d’ancienneté et autres délais avec bornes configurables dans le widget. |
| 74 | Boîtes à moustaches | **Implémenté** | Boîtes avec min, Q1, médiane, Q3, P90, max et effectif par segment ; accès aux contributeurs. |
| 75 | Diagramme de Pareto | **Implémenté** | Pareto avec cumul et seuil cumulatif configurable, conservé dans la définition du widget. |
| 76 | Nuage de points des tickets | **Implémenté** | Nuage âge × temps avant échéance avec accès à chaque ticket et limites d’affichage indiquées. |
| 77 | Mini-courbes dans les indicateurs | **Partiel** | Mini-historiques globaux présents. Moteur C# de relecture filtrée/personnelle compilable ; raccord aux écrans et scénarios spécifiques en cours de migration WebView. |
| 78 | Calendrier d’activité observée | **Implémenté** | Calendrier des événements réellement observés entre relevés, avec couverture, jours sans collecte et incompatibilités visibles. |
| 79 | Diagramme des transferts | **Implémenté** | Diagramme des transferts observés entre techniciens ou groupes à partir d’identifiants communs à deux relevés comparables. |
| 80 | Carte reliée aux autres widgets | **Implémenté** | Carte locale interactive fondée sur les coordonnées déclarées des sites/villes, reliée aux filtres du dashboard ; aucune géolocalisation déduite ni requête cartographique externe. |

## 9. Indicateurs opérationnels et « météo » du service

**Repères dans le dépôt :** [Indicateurs et météo](../src/GlpiPilot/Core/DashboardService.cs) · [Paramètres météo](../src/GlpiPilot/Core/DashboardSettings.cs) · [Synthèse du traitement](../src/GlpiPilot/Core/TicketReadingService.cs) · [Lecture des sections](../src/GlpiPilot/Core/BrowserTicketDetails.cs) · [Faits du traitement](../src/GlpiPilot/Core/TicketInsightService.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 81 | Dictionnaire des indicateurs | **Implémenté** | Indicateurs accompagnés de méthode, unité, échantillon, population et limites dans l’interface et les exports de bilan. |
| 82 | Météo entièrement paramétrable | **Implémenté** | Poids, seuils et couverture minimale configurables ; règles conservées et empreinte versionnée dans l’historique. |
| 83 | Météos spécialisées | **Implémenté** | Météos spécialisées dont charge observée, avec poids, couverture et données manquantes explicités. |
| 84 | Cohérence urgence–impact–priorité | **Implémenté** | Matrice locale impact/urgence/priorité explicite, avec repérage des écarts ; aucune matrice d’organisation supposée. |
| 85 | Échéances par tranche | **Implémenté** | Tranches d’échéance exclusives configurables ; dépassements et échéances inconnues restent identifiés séparément. |
| 86 | Tâches arrivées à échéance | **Implémenté** | File des tâches avec échéance et état réellement lus ; retard calculé seulement lorsque ces deux preuves sont disponibles. |
| 87 | Dernier échange visible | **Implémenté** | Dernier échange daté avec origine demandeur/technicien uniquement lorsque le portail la prouve ; absence de preuve ou de réponse attendue explicitement inconnue. |
| 88 | Analyse des mises en attente | **Implémenté** | Motifs, début et fin d’attente issus des faits lisibles, durée calculée avec provenance ; aucune durée inventée à partir de la seule ancienneté du ticket. |
| 89 | Validations en attente | **Implémenté** | Validations réellement en attente présentées par responsable et date, avec ancienneté calculable et champs manquants visibles. |
| 90 | Satisfaction contextualisée | **Implémenté** | Satisfaction avec réponses valides, nombre de réponses et couverture ; les non-réponses et valeurs illisibles ne deviennent pas une note nulle. |

## 10. Analyses historiques et tendances

**Repères dans le dépôt :** [Comparaison historique](../src/GlpiPilot/Core/SnapshotStore.cs) · [Séries historiques](../src/GlpiPilot/MainWindow.Dashboard.cs) · [Analyses et comptages](../src/GlpiPilot/Core/DashboardService.cs) · [Agrégations](../src/GlpiPilot/Core/QueryService.cs) · [Cohortes et scénarios](../src/GlpiPilot/Core/SnapshotAnalytics.cs) · [Effectifs datés](../src/GlpiPilot/Core/AnalyticsPopulationStore.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 91 | Cohortes de tickets | **Implémenté** | Cohortes à J+1/J+7/J+30 établies sur les observations disponibles ; statuts et membres non revus restent inconnus. |
| 92 | Délais par segment | **Implémenté** | Médianes/quantiles par segment dans les widgets et requêtes, avec effectif valide et définition calendaire des délais. |
| 93 | Équilibre des mouvements observés | **Implémenté** | Séries d’équilibre des mouvements distinguant entrées/sorties de périmètre, créations et résolutions effectivement observées. |
| 94 | Temps passé dans les statuts | **Implémenté** | Durées minimales/maximales par statut entre observations, avec intervalle d’incertitude et absence de fausse date exacte de transition. |
| 95 | Scénarios d’évolution du stock | **Implémenté** | Scénarios linéaires de stock avec hypothèses et fourchettes explicites ; présentés comme simulations, jamais comme prévisions garanties. |
| 96 | Détection des variations inhabituelles | **Implémenté** | Détection robuste des variations inhabituelles par médiane et écart absolu médian sur une référence comparable et suffisamment fournie. |
| 97 | Explication des variations d’indicateurs | **Implémenté** | Décomposition chiffrée des variations par contributeurs, entrées/sorties et changements observés ; incompatibilités de règles et de périmètre signalées. |
| 98 | Indicateurs rapportés à la taille des sites | **Implémenté** | Référentiel local éditable/importable des effectifs ou postes par site, daté et sourcé ; taux pour cent calculés seulement avec un dénominateur valide à la date du relevé. |
| 99 | Comptage explicite des affectations multiples | **Implémenté** | Comptages explicités, avec participations, parts égales et catégories d’affectation complète ; leurs limites d’addition sont affichées. |
| 100 | Fraîcheur et continuité des analyses | **Implémenté** | Fraîcheur, cadence, couverture et ruptures d’observation affichées ; limites de reconstruction signalées et périodes absentes non lissées. |

## 11. Atelier de requêtes avancé

**Repères dans le dépôt :** [Modèle des requêtes](../src/GlpiPilot/Core/QuerySettings.cs) · [Exécution et explication](../src/GlpiPilot/Core/QueryService.cs) · [Éditeur avancé](../src/GlpiPilot/UI/QueryWorkshopView.Advanced.cs) · [Contrôles des requêtes](../tests/QueryChecks/Program.cs) · [Hiérarchies](../src/GlpiPilot/UI/QueryWorkshopView.Hierarchy.cs) · [Packs](../src/GlpiPilot/Core/QueryPackService.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 101 | Groupes ET/OU imbriqués | **Implémenté** | Expressions numérotées ET/OU avec parenthèses, validation et profondeur bornée dans l’atelier. |
| 102 | Négation de groupes | **Implémenté** | NON disponible dans les expressions ; les valeurs inconnues restent inconnues sous négation. |
| 103 | Comparaison entre champs | **Implémenté** | Bornes issues d’autres champs et comparaisons de valeurs numériques ou de dates compatibles. |
| 104 | Opérateurs enrichis | **Implémenté** | Listes IN/NOT IN, intervalles inclusifs, exclusion et opérateurs de dates/ancienneté dans le moteur typé. |
| 105 | Requêtes à paramètres | **Implémenté** | Paramètres texte/nombre/date avec défaut mémorisé et valeurs temporaires d’exécution dans l’aperçu. |
| 106 | Calculs numériques par groupe | **Implémenté** | Somme, moyenne, minimum et maximum numériques par groupe, distincts du nombre de lignes. |
| 107 | Regroupements à plusieurs niveaux | **Implémenté** | Présentation hiérarchique des trois niveaux de groupes ; sous-totaux recalculés sur les lignes contributrices, y compris pour médianes et percentiles. |
| 108 | Statistiques avancées personnalisables | **Implémenté** | Médiane et percentile configurable, échantillon numérique et valeurs manquantes distingués. |
| 109 | Champs calculés avec types contrôlés | **Implémenté** | Catalogue borné de calculs texte/date/nombre, dépendances contrôlées et gestion des valeurs vides ou non calculables. |
| 110 | Explication du résultat d’une requête | **Implémenté** | Explication par identifiant unique ou ligne source avec conditions vraies, fausses ou indéterminées. |

## 12. Détail du ticket et partie traitement

**Repères dans le dépôt :** [Espace ticket](../src/GlpiPilot/UI/TicketWorkplaceView.cs) · [Lecture et recherche](../src/GlpiPilot/Core/TicketReadingService.cs) · [Persistance de lecture](../src/GlpiPilot/Core/WorkplaceStore.cs) · [Intégration des tickets](../src/GlpiPilot/MainWindow.WorkplaceTickets.cs) · [Lecteur](../src/GlpiPilot/UI/TicketReaderView.xaml.cs) · [Relations et documents](../src/GlpiPilot/Core/TicketLinkPolicy.cs) · [Brouillons](../src/GlpiPilot/UI/TicketDraftView.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 111 | Plusieurs tickets ouverts dans le logiciel | **Implémenté** | Jusqu’à douze onglets internes de tickets, identités isolées par installation et restauration des onglets. |
| 112 | Tickets épinglés | **Implémenté** | Épingles persistantes accessibles depuis l’espace personnel, indépendantes des filtres du relevé. |
| 113 | Recherche dans tout le ticket | **Implémenté** | Recherche dans les sections effectivement collectées, résultats bornés et retour à la section d’origine. |
| 114 | Sections préférées | **Implémenté** | Ordre personnel des rubriques, favorites et rubrique d’ouverture persistants dans le lecteur. |
| 115 | Filtres de chronologie | **Implémenté** | Filtres de chronologie par type, auteur et période sur les événements lus. |
| 116 | Chronologie plus lisible | **Implémenté** | Chronologie regroupée par jour, repère de dernière lecture et position de lecture conservée. |
| 117 | Nouveautés depuis la dernière consultation | **Implémenté** | Événements nouvellement lus et cellules modifiées repérés à l’intérieur des sections par comparaison aux empreintes précédentes. |
| 118 | Synthèse factuelle du traitement | **Implémenté** | Synthèse des faits lus : dernière interaction, prochaine tâche planifiée et validations en attente, avec origine et inconnues explicites. |
| 119 | Relations du ticket | **Implémenté** | Explorateur structuré des relations observées avec tickets, problèmes, changements et équipements ; navigation soumise à l’origine GLPI attendue. |
| 120 | Index des documents | **Implémenté** | Index des documents réellement présents avec ouverture/téléchargement à la demande par la session GLPI ; aucun téléchargement implicite. |

## 13. Tableau de bord personnel et organisation quotidienne

**Repères dans le dépôt :** [Ma journée](../src/GlpiPilot/UI/PersonalDayView.cs) · [Éditeur personnel](../src/GlpiPilot/UI/PersonalTicketEditorView.cs) · [Travail et cache](../src/GlpiPilot/Core/WorkplaceStore.cs) · [Rappels](../src/GlpiPilot/UI/DesktopReminderNotifier.cs) · [Intégration personnelle](../src/GlpiPilot/MainWindow.WorkplaceDay.cs) · [Briefing](../src/GlpiPilot/Core/TicketDailyBriefing.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 121 | Page « Ma journée » | **Implémenté** | Ma journée rassemble tickets, file, rappels et tournées prévues ; mise à jour de la page après sauvegarde des tournées. |
| 122 | Ordre de travail personnel | **Implémenté** | File locale ordonnable indépendamment des priorités GLPI. |
| 123 | Report temporaire d’un ticket | **Implémenté** | Report personnel jusqu’à une date, motif et réapparition dans les périmètres de travail. |
| 124 | Notes personnelles par ticket | **Implémenté** | Notes locales par ticket, recherche dans l’espace personnel et persistance des modifications. |
| 125 | Listes de vérification réutilisables | **Implémenté** | Checklists personnelles avec modèles créables, modifiables et réutilisables. |
| 126 | Rappels Windows | **Implémenté** | Rappels pendant l’ouverture de l’application, notification Windows discrète et accès à Ma journée ; affichage soumis aux réglages Windows. |
| 127 | Briefing de début de journée | **Implémenté** | Briefing des changements depuis la session précédente, isolé par source, contexte et règles ; consulter une archive ne remplace pas sa référence courante. |
| 128 | Fiche de passation | **Implémenté** | Passation préparée avec contexte lu, notes, actions cochées/restantes, rappels et prochaine responsabilité à préciser. |
| 129 | Consultation hors connexion choisie | **Implémenté** | Cache volontaire des fiches et sections déjà lues, daté et borné, distinct des données actuelles du portail. |
| 130 | Reprise du poste de travail | **Implémenté** | Reprise des onglets, filtres personnels, rubriques et positions de lecture ; identités et contextes conservés sans mélange entre portails. |

## 14. Gestion des techniciens et répartition du travail

**Repères dans le dépôt :** [Préférences techniciens](../src/GlpiPilot/Core/TechnicianPreferences.cs) · [Recommandations et scénarios](../src/GlpiPilot/Core/TechnicianPlanningService.cs) · [Organisation](../src/GlpiPilot/UI/TechnicianPlanningView.cs) · [Contrôles métier](../tests/WorkplaceChecks/Program.cs) · [Calendriers](../src/GlpiPilot/Core/TechnicianCalendar.cs) · [Actions GLPI](../src/GlpiPilot/Core/TicketActionService.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 131 | Référentiel de compétences | **Implémenté** | Compétences déclarées par technicien et prise en compte de compétences demandées dans les propositions. |
| 132 | Secteurs principaux et remplaçants | **Implémenté** | Territoires principaux et de remplacement déclarés, utilisés et expliqués dans les recommandations. |
| 133 | Disponibilités déclarées | **Implémenté** | Calendriers récurrents de présence matin/après-midi, absences et exceptions datées par technicien. |
| 134 | Capacité de travail prévisionnelle | **Implémenté** | Capacités déclarées par jour et demi-journée, avec exceptions ; utilisées par la couverture et les propositions de planning. |
| 135 | Charge estimée explicable | **Implémenté** | Estimations par catégorie, durées déclarées et fourchettes d’incertitude ; calcul et hypothèses exposés dans les recommandations. |
| 136 | Matrice de couverture | **Implémenté** | Matrice secteurs/sites × spécialités sur sept jours avec présence, capacité, titulaires et remplaçants déclarés. |
| 137 | Simulation de répartition | **Implémenté** | Scénarios locaux de réaffectation avec comparaison des charges avant/après et exclusion des références non retrouvées. |
| 138 | Suggestions d’affectation expliquées | **Implémenté** | Recommandations exposant compétences, secteur, indisponibilités déclarées, capacité, estimation et données manquantes. |
| 139 | Préparation des absences | **Implémenté** | Préparation de passation pour absence avec tickets concernés et propositions de remplaçants déclarés. |
| 140 | Réaffectation GLPI assistée | **Implémenté** | Adaptateur configurable de formulaire natif GLPI : aperçu exact de l’affectation, confirmation unique, reçu persistant et relecture ; disponibilité conditionnée aux champs et droits observables du portail. |

## 15. Villes, alias, sites et localisation

**Repères dans le dépôt :** [Référentiel](../src/GlpiPilot/Core/CitySettings.cs) · [Détection et corrections](../src/GlpiPilot/Core/CityDetectionService.cs) · [Import CSV](../src/GlpiPilot/Core/CityReferenceService.cs) · [Simulateur et validation](../src/GlpiPilot/UI/CitySettingsView.Advanced.cs) · [Contrôles des lieux](../tests/CityDetectionChecks/Program.cs) · [Simulation globale](../src/GlpiPilot/UI/CitySettingsView.Terrain.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 141 | Hiérarchie géographique complète | **Implémenté** | Hiérarchie territoriale jusqu’à douze niveaux et référentiel des bâtiments reliés aux sites/villes ; import/export et validation des relations. |
| 142 | Alias sensibles au contexte | **Implémenté** | Alias avec contexte requis/interdit, position, jetons et expression régulière bornée ; dépassements et ambiguïtés explicités. |
| 143 | Règles d’exclusion | **Implémenté** | Exclusions au niveau de la ville et de la règle, avec refus explicite des contextes absents ou ambigus. |
| 144 | Priorité entre sources de localisation | **Implémenté** | Ordre configurable des sources correction, champ GLPI, bâtiment, site, titre et combinaison ; première décision connue ou ambiguë conservée avec preuve. |
| 145 | Correction locale par ticket | **Implémenté** | Correction locale isolée par installation et ID unique, avec date et motif obligatoirement conservés ; valeurs source intactes. |
| 146 | Explication de la ville détectée | **Implémenté** | Termes ayant participé à la décision surlignés dans le titre, avec alias, règle, provenance et éventuelle ambiguïté. |
| 147 | Simulation des changements d’alias | **Implémenté** | Simulation globale de nouvelles règles sur tous les tickets avant application ; compteurs complets et détail affiché borné à mille différences. |
| 148 | Import du référentiel géographique | **Implémenté** | Import CSV des villes/sites, validation des relations et conflits, comparatif avant fusion au brouillon. |
| 149 | Coordonnées au niveau du site | **Implémenté** | Coordonnées facultatives au site ou à la ville ; source du lieu et caractère indicatif des distances distingués dans la préparation. |
| 150 | File de validation géographique | **Implémenté** | File des inconnus/ambigus ; promotion d’une correction en proposition de règle puis simulation explicite avant enregistrement. |

## 16. Tournées et interventions sur site

**Repères dans le dépôt :** [Planning et persistance](../src/GlpiPilot/Core/ScheduledTourService.cs) · [Tournées datées](../src/GlpiPilot/UI/ScheduledTourView.cs) · [Périmètres et rapprochement](../src/GlpiPilot/Core/TourPlanningService.cs) · [Contrôles des tournées](../tests/TourPlanningChecks/Program.cs) · [Disponibilités](../src/GlpiPilot/Core/ScheduledTourAvailability.cs) · [Routage et météo](../src/GlpiPilot/Core/TourExternalServices.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 151 | Tournées nommées et datées | **Implémenté** | Tournées nommées, datées et affectées avec états brouillon/prête/effectuée et historique motivé ; modification d’une tournée prête la repasse en brouillon. |
| 152 | Étapes constituées de tickets choisis | **Implémenté** | Panier de tickets choisis puis regroupement par site/ville, avec identifiants exacts conservés par étape. |
| 153 | Départ et arrivée configurables | **Implémenté** | Départ et retour configurables depuis le référentiel ou des coordonnées saisies ; aucun géocodage d’adresse. |
| 154 | Créneaux de visite | **Implémenté** | Calendriers hebdomadaires d’ouverture des sites, plusieurs fenêtres par jour et fermetures datées ; prise en compte dans la planification. |
| 155 | Durée d’intervention par étape | **Implémenté** | Durées d’intervention, trajets saisis, attentes et retour distingués dans le planning. |
| 156 | Calcul routier réel facultatif | **Implémenté** | Client OSRM v5 facultatif, endpoint et profil configurables, consentement explicite avant envoi des coordonnées ; résultat rattaché à l’ordre des étapes et invalidé après modification. |
| 157 | Comparaison de plusieurs scénarios | **Implémenté** | Comparatif des scénarios sur charge, fenêtres, échéances connues/inconnues et kilomètres routiers disponibles ; absence de route réelle conservée comme inconnue. |
| 158 | Répartition sur plusieurs journées ou techniciens | **Implémenté** | Proposition heuristique multi-jours et techniciens tenant compte des compétences, présences et capacités par demi-journée, ouvertures et étapes verrouillées ; aucune garantie d’optimum. |
| 159 | Visites verrouillées | **Implémenté** | Étapes verrouillées conservées lors des réorganisations ; reconstruction refusée si elle invaliderait leur position ou panier. |
| 160 | Fiche de tournée terrain | **Implémenté** | Fiche HTML imprimable avec planning, consignes, contacts et matériel issus des colonnes reconnues ou du référentiel, modifiables par étape. |

## 17. Automatisations et alertes

**Repères dans le dépôt :** [Programmation et campagnes](../src/GlpiPilot/MainWindow.ControlCenter.cs) · [Évaluation des alertes](../src/GlpiPilot/Core/AlertService.cs) · [Journal](../src/GlpiPilot/Core/OperationJournal.cs) · [Vue Alertes](../src/GlpiPilot/UI/AlertsView.cs) · [Lecture ciblée actuelle](../src/GlpiPilot/MainWindow.Tickets.cs) · [Politique d’automatisation](../src/GlpiPilot/Core/AutomationPolicy.cs) · [Enrichissement](../src/GlpiPilot/Core/TicketEnrichmentService.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 161 | Collectes programmées en session Windows ouverte | **Implémenté** | Programmation à intervalle ou plusieurs horaires fixes et jours choisis, pendant l’ouverture de l’application et la disponibilité de la session. |
| 162 | Recettes d’automatisation complètes | **Implémenté** | Recettes ordonnées avec extraction obligatoire puis étapes de contrôle, enrichissement, archive et export conditionnées ; validation de l’ordre et journal par étape. |
| 163 | Alertes basées sur les requêtes | **Implémenté** | Alertes de seuil, variation absolue ou pourcentage entre relevés comparables ; référence inconnue ou dénominateur nul ne déclenche pas un calcul inventé. |
| 164 | Regroupement des alertes répétées | **Implémenté** | Occurrences regroupées par règle/source et nouveaux identifiants contributeurs distingués entre évaluations successives. |
| 165 | Cycle de vie des alertes | **Implémenté** | Prise en compte, report, résolution automatique valide et résolution manuelle avec motif ; historique borné et enregistrement sans état mémoire divergent en cas d’erreur. |
| 166 | Plages de tranquillité | **Implémenté** | Politique commune de tranquillité appliquée aux lancements programmés et notifications personnelles, y compris plages traversant minuit. |
| 167 | Alertes de fraîcheur des données | **Implémenté** | Onglet Santé des collectes : fraîcheur attendue, collectes manquantes et échecs répétés par source, avec reprise après succès. |
| 168 | Réconciliation des tournées après actualisation | **Implémenté** | Rapprochement d’une tournée avec le relevé courant, écarts visibles et reconstruction explicite des étapes déverrouillées. |
| 169 | Enrichissement ciblé des détails | **Implémenté** | File priorisée de lectures de détails depuis sélection ou requête, limitée de 1 à 100 tickets, annulation et états/date de lecture par ticket. |
| 170 | Reprise après veille ou coupure réseau | **Implémenté** | Retour réseau et sortie de veille relancent l’examen des travaux dus ; reprise de pagination bornée et contrôlée, sans lancement concurrent ni confiance aveugle dans un ancien contexte. |

## 18. Configuration, partage et rapports

**Repères dans le dépôt :** [Aperçu et fusion](../src/GlpiPilot/Core/ConfigurationReview.cs) · [Transfert partageable](../src/GlpiPilot/Core/ConfigurationTransferService.cs) · [Versions des réglages](../src/GlpiPilot/Core/AppSettings.cs) · [Intégration et démonstration](../src/GlpiPilot/MainWindow.ControlCenter.cs) · [Bibliothèque de requêtes](../src/GlpiPilot/Core/QuerySettings.cs) · [Comparaison détaillée](../src/GlpiPilot/Core/ConfigurationReview.Details.cs) · [Rapport](../src/GlpiPilot/Core/ReportService.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 171 | Aperçu avant import de configuration | **Implémenté** | Comparaison détaillée des sections et éléments ajoutés, retirés, modifiés ou réordonnés avant application. |
| 172 | Import sélectif | **Implémenté** | Sélection des sections de configuration à importer, avec maintien des préférences personnelles exclues du partage. |
| 173 | Fusion avec gestion des conflits | **Implémenté** | Arbitrage par conflit : conserver le courant, prendre l’import ou garder deux variantes ; nouveaux identifiants déterministes et références des widgets/règles réécrites ensemble. |
| 174 | Historique des configurations | **Implémenté** | Trente versions de réglages et restauration avec comparaison détaillée champ par champ avant confirmation. |
| 175 | Profils de travail complets | **Implémenté** | Profil de source réunissant acquisition, conventions, correspondances et, sur choix explicite, requêtes et dashboards ; identité et dossiers personnels exclus du partage. |
| 176 | Assistant de première utilisation | **Implémenté** | Parcours intégré de première utilisation : source, correspondances, identité personnelle et démonstration ; progression mémorisée. |
| 177 | Mode démonstration réaliste | **Implémenté** | Démonstration de 180 tickets fictifs, exclue de l’archivage et du déclenchement d’alertes. |
| 178 | Pack de configuration d’équipe | **Implémenté** | Configuration d’équipe versionnée avec documentation, sans cookies, profil SSO, identité, notes ou programmation personnelle. |
| 179 | Packs de requêtes par métier | **Implémenté** | Packs JSON de requêtes par métier, aperçu, validation et choix des conflits avant import ; définitions non exécutées automatiquement. |
| 180 | Rapport de relevé complet | **Implémenté** | Rapport HTML autonome assemblant indicateurs, graphiques, critères, qualité, provenance et tickets avec bornes signalées ; export PDF natif et PNG complémentaire. |

## 19. Assistance avancée et extensions facultatives

**Repères dans le dépôt :** [Rapprochements et solutions](../src/GlpiPilot/Core/TicketIntelligenceService.cs) · [Vue des rapprochements](../src/GlpiPilot/UI/TicketIntelligenceView.cs) · [Passations](../src/GlpiPilot/Core/TicketReadingService.cs) · [Calendrier ICS](../src/GlpiPilot/Core/ScheduledTourService.cs) · [Assistant](../src/GlpiPilot/Core/LanguageAssistantService.cs) · [Formulaires](../src/GlpiPilot/Core/BrowserTicketActions.cs) · [Récurrence](../src/GlpiPilot/Core/TicketRecurrenceService.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 181 | Recherche de tickets similaires | **Implémenté** | Rapprochements locaux par titre, termes, ville et catégorie, avec preuves et références aux tickets retenus. |
| 182 | Repérage des incidents récurrents | **Implémenté** | Récurrences recherchées sur plusieurs relevés, dédoublonnées par identité et analysées par équipement lorsqu’un champ fiable existe ; fréquence et couverture explicites. |
| 183 | Vue des relations entre dossiers | **Implémenté** | Vue structurée des relations réellement lues, distinguant objets liés et rapprochements locaux ; navigation validée par type/origine. |
| 184 | Solutions déjà rencontrées | **Implémenté** | Solutions réellement présentes dans une colonne ou une lecture antérieure, avec ticket source, statut résolu/clos et date de lecture. |
| 185 | Création de requêtes en langage naturel | **Implémenté** | Assistant local déterministe et fournisseur facultatif compatible chat/completions : texte converti en brouillon DSL borné, validé puis prévisualisé avant ajout ; aucune exécution implicite. |
| 186 | Synthèses assistées et traçables | **Implémenté** | Synthèse locale extractive ou assistée sur contexte choisi, citations exactes vérifiées et rejet des références hors contexte ; endpoint et clé restent locaux, appel externe déclenché explicitement. |
| 187 | Brouillons de suivis structurés | **Implémenté** | Brouillons locaux structurés de suivi, tâche ou passation, alimentés par les seules données sélectionnées ; édition et aperçu avant copie ou publication. |
| 188 | Ajout assisté d’un suivi ou d’une tâche | **Implémenté** | Ajout de suivi/tâche via formulaire GLPI configuré : cible et champs vérifiés, aperçu, une confirmation, reçu durable et relecture ; résultat ambigu conservé pour éviter une répétition aveugle. |
| 189 | Export des rendez-vous en calendrier | **Implémenté** | Export ICS des étapes datées, identités stables, fuseau, échappement et contrôles des horaires ; import agenda laissé à l’utilisateur. |
| 190 | Météo terrain facultative | **Implémenté** | Client Open-Meteo facultatif avec endpoint configuré, consentement avant envoi des coordonnées, date et provenance ; météo absente ou périmée reste inconnue. |

## 20. Robustesse, performances et distribution Windows

**Repères dans le dépôt :** [Version et chemins](../src/GlpiPilot/Core/AppPaths.cs) · [Instance unique](../src/GlpiPilot/Core/SingleInstanceCoordinator.cs) · [Démarrage et sauvegardes](../src/GlpiPilot/Core/StartupBackupService.cs) · [Diagnostics](../src/GlpiPilot/Core/StartupDiagnostics.cs) · [Construction des packs](../build.ps1) · [Chaîne Windows](../.github/workflows/windows.yml) · [Promotion des artefacts](../build-tools/publish-release.sh) · [Mise à jour](../src/GlpiPilot/Core/UpdateService.Transaction.cs) · [Signature](../build-tools/Sign-Release.ps1) · [Harnais de retour arrière](../tests/WindowsUpdateChecks/Program.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 191 | Version unique dans toute l’application | **Implémenté** | Version de l’assemblage utilisée au démarrage, dans les diagnostics et dans les métadonnées des packs. |
| 192 | Diagnostics directement exploitables | **Implémenté** | Aperçu intégré du diagnostic nettoyé avant copie/partage, avec provenance et codes d’opération ; valeurs des tickets et données de session exclues des journaux techniques. |
| 193 | Diagnostic de démarrage | **Implémenté** | Diagnostics des dépendances, écritures et erreurs de démarrage avec conseils de récupération ; sauvegarde préalable des préférences. |
| 194 | Instance unique du logiciel | **Implémenté** | Instance unique dans la session Windows, signal limité au compte courant et restauration de la fenêtre existante. |
| 195 | Stockage local indexé | **Implémenté** | Catalogue persistant des archives et index d’identifiants par relevé, vérifiables et reconstructibles à partir des archives sans base externe. |
| 196 | Travaux de fond priorisés | **Implémenté** | Ordonnanceur partagé donnant priorité aux vues visibles, annulation des calculs remplacés et rejet des résultats périmés ; reprise d’un dashboard caché annulé lorsqu’il redevient visible. |
| 197 | Validation sur des cas représentatifs | **Partiel** | Fixtures représentatives, contrôles Windows réels et scénarios de mise à jour/retour arrière inclus dans la chaîne. Restent à valider sur le poste utilisateur : portail/SSO privé, extensions, veille et moniteurs physiques. |
| 198 | Signature et provenance des versions | **Option externe** | Provenance, empreintes et étape Authenticode avec certificat de signature et horodatage vérifiés implémentées. Aucun certificat de confiance fourni : la distribution reste non signée tant qu’il n’est pas configuré. |
| 199 | Mise à jour avec retour arrière | **Implémenté** | Assistant de mise à jour à la demande : source officielle, SHA/commit/inventaire vérifiés, copie préparée, sauvegarde indépendante et retour arrière après échec de démarrage ; scénarios exécutés par le harnais Windows. |
| 200 | Contrôle automatique des packs PROD et DEV | **Implémenté** | Chaîne vérifiant contenu PROD/DEV, même EXE, empreintes, commit source et démarrage/zoom du pack avant promotion en Release. |

## Validation et prérequis restants

Les 197 lignes implémentées décrivent une fonction reliée au produit dans le périmètre indiqué. Les tests de chaque version, leur exécution Windows et les empreintes des packs figurent dans le [rapport de vérification](Rapport-verification.txt), les [notes 2.1.0](RELEASE-2.1.0.md) et le workflow du commit livré.

- **197 — Validation du poste et du portail réels :** les tests synthétiques et le bureau Windows du runner ne remplacent pas les règles SSO, extensions, droits et périphériques de l’installation de l’utilisateur.
- **198 — Certificat de signature :** le script de signature vérifie clé privée, usage, empreinte, signature et horodatage. Un certificat de confiance et son URL d’horodatage doivent être fournis à la chaîne avant de pouvoir annoncer une distribution signée.

Aucune ligne n’est réputée validée sur le GLPI privé du seul fait de la réussite des tests. Les inconnues, limites d’observation et actions nécessitant une configuration explicite restent documentées dans les guides.
