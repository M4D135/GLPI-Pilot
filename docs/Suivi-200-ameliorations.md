# Suivi des 200 améliorations de GLPI Pilot

État du code 2.0.0 examiné le **22 octobre 2026**. Ce registre reprend les 200 propositions transmises pour poursuivre le projet ; leurs numéros restent stables. Les intitulés sont conservés pour faciliter le rapprochement avec la demande.

**Ce registre décrit la couverture fonctionnelle du code.** Il ne remplace pas les résultats de compilation, de rendu Windows ou d’essai sur le portail réel. La publication d’un pack et ses contrôles doivent être vérifiés dans le [rapport de vérification](Rapport-verification.txt) et le workflow du commit livré.

## Comment lire les états

| État | Signification | Nombre |
| --- | --- | ---: |
| **Implémenté** | Fonction implémentée et reliée à son interface ou à la chaîne de distribution, dans le périmètre précisé. | **87** |
| **Partiel** | Une partie fonctionne ; une exigence de la proposition initiale reste absente ou plus limitée. | **92** |
| **À poursuivre** | Fonction demandée non implémentée ; les briques voisines ne suffisent pas à la considérer livrée. | **13** |
| **Option externe** | Extension non livrée exigeant notamment un référentiel externe, un moteur/service, un certificat ou un adaptateur avec droits spécifiques. | **8** |
| **Total** | Une entrée par proposition, sans double comptage. | **200** |

Une option externe reste du travail à réaliser ; ce statut ne signifie ni service déjà raccordé, ni autorisation d’envoyer les données. Les notes indiquent le prochain manque concret pour les éléments partiels. Les liens de chaque chantier pointent vers les preuves de code les plus pertinentes ; ils ne prétendent pas que chaque fonction existe dans tous ces fichiers.

## 1. Connexion, SSO et collecte

**Repères dans le dépôt :** [Moteur automatique](../src/GlpiPilot/Core/AutomaticExtractionService.cs) · [SSO et navigation](../src/GlpiPilot/Core/BrowserNavigation.cs) · [Prévol et profil](../src/GlpiPilot/Core/ExtractionVerificationService.cs) · [Profils de source](../src/GlpiPilot/Core/AppSettings.cs) · [Campagnes](../src/GlpiPilot/MainWindow.ControlCenter.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 1 | Plusieurs profils de source | **Partiel** | Profils nommés avec URL, navigateur et méthode ; les repères de tableau et le dossier de sortie restent communs, pas propres à chaque source. |
| 2 | Campagnes en un clic | **Partiel** | Les sources cochées s’enchaînent avec un total final et des archives distinctes ; un échec interrompt la campagne et il manque un bilan autonome par source. |
| 3 | Vérification avant démarrage | **Partiel** | Prévol de l’adresse, des limites, de l’écriture et verrou de profil ; pas de contrôle explicite de l’espace disque disponible. |
| 4 | Suivi précis du SSO | **Partiel** | États observés, attente SSO et progression sont journalisés ; pas de chronométrage séparé de chaque étape du parcours d’authentification. |
| 5 | Reprise humaine simple | **Implémenté** | Commande Voir le navigateur, suivi des onglets et reprise de l’attente jusqu’au retour à la recherche. |
| 6 | Reconnexion en cours de collecte | **À poursuivre** | Une expiration en cours de lecture provoque un arrêt expliqué ; pas de reconnexion puis reprise automatique au point interrompu. |
| 7 | Délais adaptés aux étapes | **Partiel** | Délais SSO et téléchargement configurables ; attentes de listes et de détails encore en partie fixes ou dérivées du délai général. |
| 8 | Contrôle des filtres effectifs | **Implémenté** | Empreintes de recherche et de contexte, contrôles d’adresse et des critères, interruption lors d’une variation observée. |
| 9 | Isolation des portails | **Partiel** | Profil réservé à l’application et séparé par Edge/Chrome ; pas de profil distinct par instance GLPI. |
| 10 | Méthode d’extraction expliquée | **Implémenté** | Le parcours indique la méthode utilisée et les raisons d’un repli : export natif, affichage complet ou pagination. |

## 2. Extraction complète et récupération après incident

**Repères dans le dépôt :** [Pagination](../src/GlpiPilot/Core/BrowserService.cs) · [Export natif](../src/GlpiPilot/Core/BrowserNativeExport.cs) · [Export atomique](../src/GlpiPilot/Core/ExportService.cs) · [Import XLSX](../src/GlpiPilot/Core/XlsxImportService.cs) · [Contrôles de fondation](../tests/FoundationChecks/Program.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 11 | Reprise d’une pagination interrompue | **À poursuivre** | Aucun point de reprise de pagination conservé. Une relance recommence depuis la première page. |
| 12 | Pagination automatique de dernier recours | **Implémenté** | Repli lorsque l’export natif est réellement indisponible, avec plafonds, détection de pages répétées et arrêt motivé. |
| 13 | Conditions de collecte visibles | **Partiel** | Début, fin, méthode et périmètre sont conservés ; le tri effectivement observé n’a pas de champ explicite dans le reçu. |
| 14 | Vérification par échantillon | **Partiel** | Jusqu’à 25 identifiants visibles sont rapprochés du CSV ; les autres valeurs de l’échantillon ne sont pas comparées. |
| 15 | Protection du dernier relevé valide | **Implémenté** | Les exports sont finalisés atomiquement ; la rétention préserve aussi le dernier relevé complet, vérifié et comparable malgré des essais partiels ultérieurs. |
| 16 | Résultat clairement qualifié | **Implémenté** | Complétude, résultat vide confirmé, résultat partiel et erreur sont distingués dans les résultats, avertissements et journaux. |
| 17 | Annulation pendant l’écriture | **Implémenté** | Les écritures CSV/XLSX et historiques prennent en compte l’annulation ; les fichiers temporaires restent distincts des destinations finalisées. |
| 18 | Progression fondée sur le travail réel | **Implémenté** | La progression utilise les pages et lignes réellement reçues et le total disponible ; elle n’annonce pas de délai restant artificiel. |
| 19 | Identifiants sans collision | **Partiel** | Identité des tickets isolée par origine et installation GLPI ; la généralisation à plusieurs types d’objets n’existe pas. |
| 20 | Import local Excel | **Implémenté** | Import XLSX de la première feuille visible, valeurs enregistrées, limites et erreurs contrôlées, provenance locale explicitement non certifiée GLPI. |

## 3. Qualité et compréhension des données

**Repères dans le dépôt :** [Qualité](../src/GlpiPilot/Core/DataQualityService.cs) · [Vue Qualité](../src/GlpiPilot/UI/DataQualityView.cs) · [Interprétation des colonnes](../src/GlpiPilot/Core/DashboardService.cs) · [Réglages des indicateurs](../src/GlpiPilot/Core/DashboardSettings.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 21 | Détection des changements de colonnes | **Partiel** | La comparaison montre colonnes ajoutées/retirées et bloque un schéma incompatible ; pas d’assistant de détection de renommages ou d’alerte automatique au changement. |
| 22 | Assistant de correspondance des champs | **Partiel** | Reconnaissance des rôles et correspondances modifiables disponibles ; pas de nouvel assistant progressif avec proposition et aperçu par cellule. |
| 23 | Profil de qualité par colonne | **Implémenté** | Rapport par colonne : présence, vide, valeurs illisibles, diversité bornée, exemple et accès aux lignes problématiques. |
| 24 | Formats propres à chaque source | **Partiel** | Culture de dates et correspondances globales mémorisées ; conventions de nombres et fuseaux ne sont pas configurables séparément pour chaque source. |
| 25 | Valeur d’origine accessible | **Partiel** | Cellules brutes conservées et valeurs dérivées identifiées ; pas de panneau général comparant pour chaque cellule sa valeur brute et interprétée. |
| 26 | Gestion configurable des champs multiples | **Partiel** | Lecture des affectations multiples et modes de comptage disponibles ; pas de configuration générale des séparateurs et règles de découpage par source. |
| 27 | Dictionnaires de normalisation | **Partiel** | Normalisation intégrée des statuts/priorités et dictionnaire de lieux ; pas de dictionnaires éditables généraux pour catégories, statuts et personnes. |
| 28 | Règles de cohérence | **Implémenté** | Contrôles des dates futures, dates avant création, clôture avant résolution et valeurs non interprétables ; source conservée. |
| 29 | Analyse des doublons | **Implémenté** | Les identifiants répétés sont distingués entre lignes identiques et lignes divergentes, avec référence à la ligne précédente. |
| 30 | Trois niveaux de confiance | **Implémenté** | L’espace Qualité sépare complétude, couverture des champs et fiabilité d’interprétation. |

## 4. Historique des relevés

**Repères dans le dépôt :** [Archives et comparaisons](../src/GlpiPilot/Core/SnapshotStore.cs) · [Historique dans l’interface](../src/GlpiPilot/UI/HistoryWorkspaceView.cs) · [Courbes globales](../src/GlpiPilot/MainWindow.Dashboard.cs) · [Contrôles de fondation](../tests/FoundationChecks/Program.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 31 | Bibliothèque de relevés datés | **Implémenté** | Archives compressées datées avec provenance, règles, indicateurs, empreinte et catalogue consultable. |
| 32 | Comparaison entre deux relevés | **Implémenté** | Comparaison de deux relevés avec apparitions, absences et changements champ par champ, compteurs complets et affichage borné. |
| 33 | Contrôle de comparabilité | **Implémenté** | Contrôle de source, contexte, complétude, schéma, règles, identité et ordre chronologique avant comparaison métier. |
| 34 | Absence correctement interprétée | **Partiel** | L’absence reste « absent du relevé » et n’est jamais une résolution ; aucune recherche automatique du statut actuel des absents n’est lancée. |
| 35 | Courbe réelle du stock observé | **Partiel** | Courbes globales de stock et ruptures sur archives incompatibles ; les périodes sans aucune collecte ne sont pas matérialisées comme des points manquants. |
| 36 | Entrées et sorties du périmètre | **Implémenté** | Les comparaisons comptent apparitions et absences du périmètre, en les distinguant des créations et résolutions supposées. |
| 37 | Réouvertures observées | **Implémenté** | Réouverture comptée uniquement lorsqu’un même identifiant passe d’un statut résolu/clos à un statut actif entre deux relevés. |
| 38 | Historique des affectations observées | **Implémenté** | Changements de technicien observés entre deux relevés, avec leurs dates de constat ; aucune heure de transition n’est inventée. |
| 39 | Règles attachées aux relevés | **Implémenté** | Correspondances, paramètres de calcul et dictionnaire de villes sont attachés aux archives avec une empreinte des règles. |
| 40 | Politique de conservation | **Implémenté** | Rétention par âge, nombre et budget disque ; protection explicite et conservation des derniers relevés utiles, pouvant dépasser le budget. |

## 5. Interface générale et confort Windows

**Repères dans le dépôt :** [Préférences et fenêtre](../src/GlpiPilot/MainWindow.ControlCenter.cs) · [Réglages de présentation](../src/GlpiPilot/UI/ControlCenterView.cs) · [Navigation et palette](../src/GlpiPilot/MainWindow.Workspace.cs) · [Thèmes](../src/GlpiPilot/App.xaml).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 41 | Densité indépendante du zoom | **Implémenté** | Trois densités de lignes, indépendante du zoom mémorisé de l’interface. |
| 42 | Mode concentration | **Partiel** | Le mode concentré masque le journal et le contexte secondaire ; il ne masque pas toute la navigation. |
| 43 | Disposition adaptée aux petits écrans | **Implémenté** | Navigation adaptée à la largeur, panneaux intégrés et contrôles de disposition à plusieurs tailles/zooms ; validation réelle à consulter dans le workflow. |
| 44 | Thème automatique Windows | **Implémenté** | Option de suivi du thème Windows avec maintien du choix manuel clair/sombre. |
| 45 | Couleur d’accent personnalisable | **Implémenté** | Cinq accents configurables appliqués aux ressources de l’interface, avec valeurs adaptées aux thèmes clair et sombre. |
| 46 | Accessibilité des couleurs | **Partiel** | Contraste renforcé, textes et symboles présents ; pas de palettes dédiées au daltonisme ni de certification d’accessibilité. |
| 47 | Animations réglables | **Partiel** | Réduction locale des animations et transitions de popup ; pas de suivi explicite de la préférence Windows de réduction des mouvements. |
| 48 | Gestion des écrans multiples | **Partiel** | Position et taille mémorisées et bornées au bureau virtuel ; pas de sélection d’un moniteur réellement disponible ni de gestion complète des espaces entre écrans. |
| 49 | Navigation personnalisable | **À poursuivre** | La navigation reste définie par l’application ; pas d’épinglage, réorganisation ou masquage personnel des rubriques. |
| 50 | Palette de commandes intégrée | **Implémenté** | Palette de commandes Ctrl+K intégrée dans la fenêtre principale, avec fermeture et retour du focus. |

## 6. Tableaux, colonnes et navigation

**Repères dans le dépôt :** [Tableau du relevé](../src/GlpiPilot/MainWindow.Preview.cs) · [Colonnes](../src/GlpiPilot/UI/PreviewColumnsDialog.cs) · [Vues nommées](../src/GlpiPilot/Core/NamedPreviewStore.cs) · [Filtres et tris](../src/GlpiPilot/Core/PreviewQueryService.cs) · [Lecture des cellules](../src/GlpiPilot/MainWindow.xaml).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 51 | Vues de colonnes nommées | **Implémenté** | Vues nommées enregistrant colonnes, dimensions, ordre, recherche, filtres et tri pour une structure d’en-têtes donnée. |
| 52 | Sélecteur de colonnes intégré | **Partiel** | Sélecteur intégré, recherche par nom, affichage et déplacement de colonnes ; regroupement par familles non implémenté. |
| 53 | Colonnes figées | **Implémenté** | Nombre de colonnes figées configurable pour le tableau du relevé. |
| 54 | Filtres directement dans les colonnes | **Partiel** | Filtres typés par colonne dans l’éditeur de vues ; pas de contrôles de filtrage intégrés directement à chaque en-tête du tableau. |
| 55 | Tri sur plusieurs critères | **Implémenté** | Jusqu’à quatre tris appliqués à toutes les lignes, avec ajout par Maj + clic et réglage dans la vue. |
| 56 | Panier de sélection persistant | **Partiel** | Panier explicite pour les tournées et file personnelle persistants ; la sélection courante du tableau n’est pas un panier universel conservé entre tous les filtres. |
| 57 | Groupes de colonnes repliables | **À poursuivre** | Pas de groupes de colonnes repliables par identité, affectation, dates ou localisation. |
| 58 | Lecture confortable des cellules longues | **Partiel** | Lecture du texte complet et copie de cellules dans le détail du ticket ; pas de lecteur équivalent universel pour toutes les cellules du relevé. |
| 59 | Présentation mémorisée par vue | **Partiel** | Préférences liées au schéma et vues nommées ; pas de persistance complète des tailles et colonnes séparée pour chaque tableau global/personnel/tournée. |
| 60 | Navigation complète au clavier | **Partiel** | Raccourcis, navigation WPF et ouverture par Entrée disponibles ; restauration garantie de la ligne et de la position après tout parcours non implémentée. |

## 7. Construction et personnalisation des dashboards

**Repères dans le dépôt :** [Périmètres et édition groupée](../src/GlpiPilot/UI/DashboardView.Scopes.cs) · [Éditeur et catalogue](../src/GlpiPilot/UI/DashboardView.Editor.cs) · [Disposition](../src/GlpiPilot/Core/DashboardLayoutSettings.cs) · [Intégration et export](../src/GlpiPilot/MainWindow.Dashboard.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 61 | Barre de filtres commune | **Implémenté** | Filtres communs de distribution, population d’indicateur et période appliqués aux widgets compatibles du dashboard. |
| 62 | Filtres croisés entre widgets | **Implémenté** | Clic de filtrage sur catégories et populations, conditions visibles et remise à zéro du périmètre. |
| 63 | Filtres propres à chaque widget | **Implémenté** | Conditions propres à chaque widget ajoutées au périmètre commun ; filtre devenu indisponible signalé explicitement. |
| 64 | Comparaison de deux périmètres | **Implémenté** | Comparaison A/B de deux périmètres sur le même relevé, avec protection du périmètre personnel. |
| 65 | Sections de dashboard | **Partiel** | Sections nommées repliables et mémorisées ; déplacement d’une section entière comme un bloc non implémenté. |
| 66 | Modification de plusieurs widgets | **Partiel** | Sélection multiple pour section et style ; dimensions et alignements groupés restent à compléter. |
| 67 | Favoris de widgets persistants | **Partiel** | Favoris du catalogue persistants ; liste des derniers widgets utilisés absente. |
| 68 | Catalogue adapté aux données présentes | **Implémenté** | Catalogue filtrable sur la disponibilité réelle et explications des mesures/champs manquants. |
| 69 | Contextes de consultation enregistrés | **Implémenté** | Contextes de filtres nommés mémorisés indépendamment du placement des widgets. |
| 70 | Export visuel du dashboard | **Partiel** | Export PNG du dashboard et bilan CSV/XLSX ; pas de PDF natif ni de cartouche garanti réunissant filtres, date et complétude dans l’image. |

## 8. Nouveaux widgets visuels

**Repères dans le dépôt :** [Rendus analytiques](../src/GlpiPilot/UI/DashboardView.Analytics.cs) · [Séries et calculs](../src/GlpiPilot/Core/DashboardService.cs) · [Catalogue](../src/GlpiPilot/UI/DashboardView.Editor.cs) · [Contrôles des indicateurs](../tests/DashboardChecks/Program.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 71 | Matrice techniciens × villes | **Implémenté** | Matrice techniciens × villes avec mode de comptage explicite et accès aux tickets de chaque cellule. |
| 72 | Barres empilées | **Partiel** | Barres empilées normalisées à 100 % et volumes lisibles ; pas de bascule entre deux rendus empilés volume/pourcentage. |
| 73 | Histogramme d’ancienneté | **Implémenté** | Histogrammes d’ancienneté et autres délais avec bornes configurables dans le widget. |
| 74 | Boîtes à moustaches | **Implémenté** | Boîtes avec min, Q1, médiane, Q3, P90, max et effectif par segment ; accès aux contributeurs. |
| 75 | Diagramme de Pareto | **Partiel** | Pareto avec cumul des catégories ; le repère est fixé à 80 %, sans seuil cumulatif réglable. |
| 76 | Nuage de points des tickets | **Implémenté** | Nuage âge × temps avant échéance avec accès à chaque ticket et limites d’affichage indiquées. |
| 77 | Mini-courbes dans les indicateurs | **Partiel** | Quatre widgets historiques globaux disponibles ; pas de mini-courbe attachée à chaque indicateur ni de comparaison de période généralisée. |
| 78 | Calendrier d’activité observée | **Partiel** | Répartitions des dates présentes dans le relevé ; pas de calendrier d’activité historique avec couverture des événements observés. |
| 79 | Diagramme des transferts | **À poursuivre** | Pas de diagramme de flux des transferts entre techniciens ou groupes. |
| 80 | Carte reliée aux autres widgets | **À poursuivre** | Coordonnées de villes/sites disponibles, mais aucune carte interactive reliée aux widgets n’est implémentée. |

## 9. Indicateurs opérationnels et « météo » du service

**Repères dans le dépôt :** [Indicateurs et météo](../src/GlpiPilot/Core/DashboardService.cs) · [Paramètres météo](../src/GlpiPilot/Core/DashboardSettings.cs) · [Synthèse du traitement](../src/GlpiPilot/Core/TicketReadingService.cs) · [Lecture des sections](../src/GlpiPilot/Core/BrowserTicketDetails.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 81 | Dictionnaire des indicateurs | **Implémenté** | Indicateurs accompagnés de méthode, unité, échantillon, population et limites dans l’interface et les exports de bilan. |
| 82 | Météo entièrement paramétrable | **Implémenté** | Poids, seuils et couverture minimale configurables ; règles conservées et empreinte versionnée dans l’historique. |
| 83 | Météos spécialisées | **Partiel** | Profils synthèse, échéances, qualité et lieux ; météo distincte de la charge observée non implémentée. |
| 84 | Cohérence urgence–impact–priorité | **Implémenté** | Matrice locale impact/urgence/priorité explicite, avec repérage des écarts ; aucune matrice d’organisation supposée. |
| 85 | Échéances par tranche | **Partiel** | Indicateurs dépassé, aujourd’hui, demain, semaine, bientôt et sans échéance ; pas de partition unique entièrement configurable en tranches exclusives. |
| 86 | Tâches arrivées à échéance | **À poursuivre** | Les tâches lues sont consultables, mais aucune extraction fiable de leur échéance/achèvement ne produit le compteur de tâches en retard demandé. |
| 87 | Dernier échange visible | **Partiel** | Dernier suivi daté visible dans la synthèse ; origine demandeur/technicien et attente de réponse ne sont pas établies. |
| 88 | Analyse des mises en attente | **Partiel** | Statut En attente, ancienneté du ticket et champs lus consultables ; motifs structurés et durées réelles de mise en attente non calculés. |
| 89 | Validations en attente | **Partiel** | Entrées et champs de validation lus dans la fiche ; pas de file des validations en attente par responsable et ancienneté. |
| 90 | Satisfaction contextualisée | **À poursuivre** | Aucun indicateur de satisfaction avec nombre de réponses et couverture n’est intégré. |

## 10. Analyses historiques et tendances

**Repères dans le dépôt :** [Comparaison historique](../src/GlpiPilot/Core/SnapshotStore.cs) · [Séries historiques](../src/GlpiPilot/MainWindow.Dashboard.cs) · [Analyses et comptages](../src/GlpiPilot/Core/DashboardService.cs) · [Agrégations](../src/GlpiPilot/Core/QueryService.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 91 | Cohortes de tickets | **À poursuivre** | Pas de suivi longitudinal de cohortes à J+1/J+7/J+30. |
| 92 | Délais par segment | **Implémenté** | Médianes/quantiles par segment dans les widgets et requêtes, avec effectif valide et définition calendaire des délais. |
| 93 | Équilibre des mouvements observés | **Partiel** | Apparitions, absences et résolutions observées disponibles dans la comparaison ; pas de série dédiée d’équilibre des mouvements. |
| 94 | Temps passé dans les statuts | **À poursuivre** | Aucun calcul de temps par statut ni d’intervalle d’incertitude entre observations. |
| 95 | Scénarios d’évolution du stock | **À poursuivre** | Pas de scénarios prévisionnels de stock ni de fourchettes statistiques. |
| 96 | Détection des variations inhabituelles | **À poursuivre** | Pas de détection statistique des variations inhabituelles sur une référence historique comparable. |
| 97 | Explication des variations d’indicateurs | **Partiel** | Différences par ticket et incompatibilités de règles/périmètre consultables ; pas de décomposition chiffrée d’une variation d’indicateur. |
| 98 | Indicateurs rapportés à la taille des sites | **Option externe** | Il manque un référentiel daté des effectifs/postes par site et son intégration pour calculer des taux pour cent unités. |
| 99 | Comptage explicite des affectations multiples | **Implémenté** | Comptages explicités, avec participations, parts égales et catégories d’affectation complète ; leurs limites d’addition sont affichées. |
| 100 | Fraîcheur et continuité des analyses | **Partiel** | Fraîcheur du relevé et ruptures d’archives incompatibles visibles ; périodes sans observation et couverture historique détaillée restent à représenter. |

## 11. Atelier de requêtes avancé

**Repères dans le dépôt :** [Modèle des requêtes](../src/GlpiPilot/Core/QuerySettings.cs) · [Exécution et explication](../src/GlpiPilot/Core/QueryService.cs) · [Éditeur avancé](../src/GlpiPilot/UI/QueryWorkshopView.Advanced.cs) · [Contrôles des requêtes](../tests/QueryChecks/Program.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 101 | Groupes ET/OU imbriqués | **Implémenté** | Expressions numérotées ET/OU avec parenthèses, validation et profondeur bornée dans l’atelier. |
| 102 | Négation de groupes | **Implémenté** | NON disponible dans les expressions ; les valeurs inconnues restent inconnues sous négation. |
| 103 | Comparaison entre champs | **Implémenté** | Bornes issues d’autres champs et comparaisons de valeurs numériques ou de dates compatibles. |
| 104 | Opérateurs enrichis | **Implémenté** | Listes IN/NOT IN, intervalles inclusifs, exclusion et opérateurs de dates/ancienneté dans le moteur typé. |
| 105 | Requêtes à paramètres | **Implémenté** | Paramètres texte/nombre/date avec défaut mémorisé et valeurs temporaires d’exécution dans l’aperçu. |
| 106 | Calculs numériques par groupe | **Implémenté** | Somme, moyenne, minimum et maximum numériques par groupe, distincts du nombre de lignes. |
| 107 | Regroupements à plusieurs niveaux | **Partiel** | Trois niveaux et périodes de regroupement disponibles ; pas de présentation hiérarchique généralisée avec sous-totaux intermédiaires. |
| 108 | Statistiques avancées personnalisables | **Implémenté** | Médiane et percentile configurable, échantillon numérique et valeurs manquantes distingués. |
| 109 | Champs calculés avec types contrôlés | **Implémenté** | Catalogue borné de calculs texte/date/nombre, dépendances contrôlées et gestion des valeurs vides ou non calculables. |
| 110 | Explication du résultat d’une requête | **Implémenté** | Explication par identifiant unique ou ligne source avec conditions vraies, fausses ou indéterminées. |

## 12. Détail du ticket et partie traitement

**Repères dans le dépôt :** [Espace ticket](../src/GlpiPilot/UI/TicketWorkplaceView.cs) · [Lecture et recherche](../src/GlpiPilot/Core/TicketReadingService.cs) · [Persistance de lecture](../src/GlpiPilot/Core/WorkplaceStore.cs) · [Intégration des tickets](../src/GlpiPilot/MainWindow.WorkplaceTickets.cs) · [Lecteur](../src/GlpiPilot/UI/TicketReaderView.xaml.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 111 | Plusieurs tickets ouverts dans le logiciel | **Implémenté** | Jusqu’à douze onglets internes de tickets, identités isolées par installation et restauration des onglets. |
| 112 | Tickets épinglés | **Implémenté** | Épingles persistantes accessibles depuis l’espace personnel, indépendantes des filtres du relevé. |
| 113 | Recherche dans tout le ticket | **Implémenté** | Recherche dans les sections effectivement collectées, résultats bornés et retour à la section d’origine. |
| 114 | Sections préférées | **Partiel** | Rubriques favorites et rubrique d’ouverture mémorisées ; ordre personnalisé complet des rubriques absent. |
| 115 | Filtres de chronologie | **Implémenté** | Filtres de chronologie par type, auteur et période sur les événements lus. |
| 116 | Chronologie plus lisible | **Partiel** | Regroupement repliable par journée et date de lecture conservée ; pas de repère de dernière lecture inséré précisément dans la chronologie. |
| 117 | Nouveautés depuis la dernière consultation | **Partiel** | Rubriques nouvelles, modifiées ou non relues détectées par empreintes ; pas de surlignage des événements/cellules ajoutés à l’intérieur d’une rubrique. |
| 118 | Synthèse factuelle du traitement | **Partiel** | Dernières entrées datées et champs disponibles synthétisés ; prochaine tâche planifiée et validations réellement en attente non déduites. |
| 119 | Relations du ticket | **Partiel** | Lecture des rubriques accessibles du ticket ; pas de vue structurée réunissant et naviguant toutes ses relations métier. |
| 120 | Index des documents | **Partiel** | Rubriques Documents consultables si recueillies ; pas d’index dédié des pièces avec téléchargement à la demande. |

## 13. Tableau de bord personnel et organisation quotidienne

**Repères dans le dépôt :** [Ma journée](../src/GlpiPilot/UI/PersonalDayView.cs) · [Éditeur personnel](../src/GlpiPilot/UI/PersonalTicketEditorView.cs) · [Travail et cache](../src/GlpiPilot/Core/WorkplaceStore.cs) · [Rappels](../src/GlpiPilot/UI/DesktopReminderNotifier.cs) · [Intégration personnelle](../src/GlpiPilot/MainWindow.WorkplaceDay.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 121 | Page « Ma journée » | **Partiel** | Ma journée rassemble tickets, file, échéances et rappels ; la tournée prévue n’est pas réunie dans cette même page. |
| 122 | Ordre de travail personnel | **Implémenté** | File locale ordonnable indépendamment des priorités GLPI. |
| 123 | Report temporaire d’un ticket | **Implémenté** | Report personnel jusqu’à une date, motif et réapparition dans les périmètres de travail. |
| 124 | Notes personnelles par ticket | **Implémenté** | Notes locales par ticket, recherche dans l’espace personnel et persistance des modifications. |
| 125 | Listes de vérification réutilisables | **Implémenté** | Checklists personnelles avec modèles créables, modifiables et réutilisables. |
| 126 | Rappels Windows | **Implémenté** | Rappels pendant l’ouverture de l’application, notification Windows discrète et accès à Ma journée ; affichage soumis aux réglages Windows. |
| 127 | Briefing de début de journée | **Partiel** | Urgences, fraîcheur et changements de lecture sont accessibles ; pas de briefing automatique assemblant les changements depuis la dernière session. |
| 128 | Fiche de passation | **Implémenté** | Passation préparée avec contexte lu, notes, actions cochées/restantes, rappels et prochaine responsabilité à préciser. |
| 129 | Consultation hors connexion choisie | **Implémenté** | Cache volontaire des fiches et sections déjà lues, daté et borné, distinct des données actuelles du portail. |
| 130 | Reprise du poste de travail | **Partiel** | Onglets, file, favoris et espace technicien restaurés ; positions exactes de lecture et tous les filtres temporaires ne le sont pas. |

## 14. Gestion des techniciens et répartition du travail

**Repères dans le dépôt :** [Préférences techniciens](../src/GlpiPilot/Core/TechnicianPreferences.cs) · [Recommandations et scénarios](../src/GlpiPilot/Core/TechnicianPlanningService.cs) · [Organisation](../src/GlpiPilot/UI/TechnicianPlanningView.cs) · [Contrôles métier](../tests/WorkplaceChecks/Program.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 131 | Référentiel de compétences | **Implémenté** | Compétences déclarées par technicien et prise en compte de compétences demandées dans les propositions. |
| 132 | Secteurs principaux et remplaçants | **Implémenté** | Territoires principaux et de remplacement déclarés, utilisés et expliqués dans les recommandations. |
| 133 | Disponibilités déclarées | **Partiel** | Intervalle d’indisponibilité et note de disponibilité ; pas de planning récurrent complet des plages de présence. |
| 134 | Capacité de travail prévisionnelle | **Partiel** | Capacité quotidienne déclarée ; pas de calendrier de capacités variant par journée ou demi-journée. |
| 135 | Charge estimée explicable | **Partiel** | Minutes saisies par proposition et estimation uniforme déclarée par technicien ; modèles par catégorie et fourchettes d’incertitude absents. |
| 136 | Matrice de couverture | **Partiel** | Couverture territoriale pour une date et relais ; matrice complète sites × spécialités sur plusieurs périodes absente. |
| 137 | Simulation de répartition | **Implémenté** | Scénarios locaux de réaffectation avec comparaison des charges avant/après et exclusion des références non retrouvées. |
| 138 | Suggestions d’affectation expliquées | **Implémenté** | Recommandations exposant compétences, secteur, indisponibilités déclarées, capacité, estimation et données manquantes. |
| 139 | Préparation des absences | **Implémenté** | Préparation de passation pour absence avec tickets concernés et propositions de remplaçants déclarés. |
| 140 | Réaffectation GLPI assistée | **Option externe** | Aucune écriture GLPI. Il faut un adaptateur d’affectation vérifié sur le portail, les droits associés, aperçu et relecture de contrôle. |

## 15. Villes, alias, sites et localisation

**Repères dans le dépôt :** [Référentiel](../src/GlpiPilot/Core/CitySettings.cs) · [Détection et corrections](../src/GlpiPilot/Core/CityDetectionService.cs) · [Import CSV](../src/GlpiPilot/Core/CityReferenceService.cs) · [Simulateur et validation](../src/GlpiPilot/UI/CitySettingsView.Advanced.cs) · [Contrôles des lieux](../tests/CityDetectionChecks/Program.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 141 | Hiérarchie géographique complète | **Partiel** | Région/département/secteur, ville et site disponibles ; niveau bâtiment et hiérarchie territoriale générale non implémentés. |
| 142 | Alias sensibles au contexte | **Partiel** | Alias avec termes de contexte requis/interdits et colonne choisie ; règles de position précise ou de format de titre encore absentes. |
| 143 | Règles d’exclusion | **Implémenté** | Exclusions au niveau de la ville et de la règle, avec refus explicite des contextes absents ou ambigus. |
| 144 | Priorité entre sources de localisation | **Partiel** | Correction locale et détection site/titre intégrées ; ordre configurable de toutes les sources, dont champ de localisation GLPI, absent. |
| 145 | Correction locale par ticket | **Partiel** | Correction locale par installation et ID unique, date et cellules source conservées ; motif libre de correction non enregistré. |
| 146 | Explication de la ville détectée | **Partiel** | Preuves textuelles des termes, alias et règles disponibles ; pas de surlignage visuel du passage dans le titre. |
| 147 | Simulation des changements d’alias | **Partiel** | Simulation d’un titre et de contextes saisis, contrôle des alias et aperçu CSV ; pas de simulation globale des changements sur tous les tickets avant application. |
| 148 | Import du référentiel géographique | **Implémenté** | Import CSV des villes/sites, validation des relations et conflits, comparatif avant fusion au brouillon. |
| 149 | Coordonnées au niveau du site | **Implémenté** | Coordonnées facultatives au site ou à la ville ; source du lieu et caractère indicatif des distances distingués dans la préparation. |
| 150 | File de validation géographique | **Partiel** | File des lieux non reconnus/ambigus et corrections ; conversion assistée d’une correction en règle générale non implémentée. |

## 16. Tournées et interventions sur site

**Repères dans le dépôt :** [Planning et persistance](../src/GlpiPilot/Core/ScheduledTourService.cs) · [Tournées datées](../src/GlpiPilot/UI/ScheduledTourView.cs) · [Périmètres et rapprochement](../src/GlpiPilot/Core/TourPlanningService.cs) · [Contrôles des tournées](../tests/TourPlanningChecks/Program.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 151 | Tournées nommées et datées | **Partiel** | Tournées nommées, datées, affectées et mémorisées ; pas de cycle explicite de préparation avec états brouillon/prête/effectuée. |
| 152 | Étapes constituées de tickets choisis | **Implémenté** | Panier de tickets choisis puis regroupement par site/ville, avec identifiants exacts conservés par étape. |
| 153 | Départ et arrivée configurables | **Implémenté** | Départ et retour configurables depuis le référentiel ou des coordonnées saisies ; aucun géocodage d’adresse. |
| 154 | Créneaux de visite | **Partiel** | Fenêtres horaires par étape et contrôle des dépassements ; pas de calendrier récurrent d’ouverture des sites. |
| 155 | Durée d’intervention par étape | **Implémenté** | Durées d’intervention, trajets saisis, attentes et retour distingués dans le planning. |
| 156 | Calcul routier réel facultatif | **Option externe** | Aucun moteur routier raccordé ; choix et intégration d’un fournisseur nécessaires. Les distances actuelles sont à vol d’oiseau. |
| 157 | Comparaison de plusieurs scénarios | **Partiel** | Scénarios copiables avec comparatif des volumes, minutes et fenêtres dépassées ; pas d’arbitrage intégré des échéances et kilomètres routiers. |
| 158 | Répartition sur plusieurs journées ou techniciens | **Partiel** | Répartition entre techniciens selon les durées déclarées ; pas d’optimisation multi-jours ni de prise en compte automatique des compétences/absences/capacités. |
| 159 | Visites verrouillées | **Implémenté** | Étapes verrouillées conservées lors des réorganisations ; reconstruction refusée si elle invaliderait leur position ou panier. |
| 160 | Fiche de tournée terrain | **Partiel** | Briefing HTML imprimable avec planning, tickets, lieux et consignes ; contacts et matériel ne sont pas automatiquement récoltés dans une fiche terrain structurée. |

## 17. Automatisations et alertes

**Repères dans le dépôt :** [Programmation et campagnes](../src/GlpiPilot/MainWindow.ControlCenter.cs) · [Évaluation des alertes](../src/GlpiPilot/Core/AlertService.cs) · [Journal](../src/GlpiPilot/Core/OperationJournal.cs) · [Vue Alertes](../src/GlpiPilot/UI/AlertsView.cs) · [Lecture ciblée actuelle](../src/GlpiPilot/MainWindow.Tickets.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 161 | Collectes programmées en session Windows ouverte | **Partiel** | Collectes à intervalle réglable tant que l’application reste ouverte et la session disponible ; pas de calendrier d’horaires fixes multiples. |
| 162 | Recettes d’automatisation complètes | **Partiel** | Chaîne fixe extraction, contrôle, enrichissement, archive et exports ; pas de constructeur de recettes avec étapes/conditions personnalisées. |
| 163 | Alertes basées sur les requêtes | **Partiel** | Alertes sur seuil d’un indicateur ou résultat de requête ; variations significatives entre relevés non évaluées. |
| 164 | Regroupement des alertes répétées | **Partiel** | Incidents regroupés par règle/source et occurrences comptées ; pas de mise en évidence des tickets nouvellement contributeurs entre occurrences. |
| 165 | Cycle de vie des alertes | **Partiel** | Prise en compte, report et résolution automatique sur une mesure valide ; résolution manuelle locale avec motif/historique dédié absente. |
| 166 | Plages de tranquillité | **Partiel** | Plages silencieuses pour les lancements automatiques ; elles ne constituent pas une politique commune à toutes les notifications et rappels Windows. |
| 167 | Alertes de fraîcheur des données | **Partiel** | Fraîcheur signalée et échecs conservés au journal ; alertes dédiées aux collectes manquantes et aux échecs répétés absentes. |
| 168 | Réconciliation des tournées après actualisation | **Implémenté** | Rapprochement d’une tournée avec le relevé courant, écarts visibles et reconstruction explicite des étapes déverrouillées. |
| 169 | Enrichissement ciblé des détails | **Partiel** | Lecture à la demande du ticket et de ses rubriques ; pas de file d’enrichissement par sélection/requête avec priorité et limite globale. |
| 170 | Reprise après veille ou coupure réseau | **Partiel** | Verrouillage de session pris en compte et opérations interrompues identifiées ; pas de reprise dédiée après veille ou retour réseau. |

## 18. Configuration, partage et rapports

**Repères dans le dépôt :** [Aperçu et fusion](../src/GlpiPilot/Core/ConfigurationReview.cs) · [Transfert partageable](../src/GlpiPilot/Core/ConfigurationTransferService.cs) · [Versions des réglages](../src/GlpiPilot/Core/AppSettings.cs) · [Intégration et démonstration](../src/GlpiPilot/MainWindow.ControlCenter.cs) · [Bibliothèque de requêtes](../src/GlpiPilot/Core/QuerySettings.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 171 | Aperçu avant import de configuration | **Partiel** | Aperçu comparant des sections et leurs volumes ; pas de liste exhaustive des éléments ajoutés, modifiés et supprimés. |
| 172 | Import sélectif | **Implémenté** | Sélection des sections de configuration à importer, avec maintien des préférences personnelles exclues du partage. |
| 173 | Fusion avec gestion des conflits | **Partiel** | Fusion conservant les identifiants déjà présents et ajoutant les nouveaux ; pas d’arbitrage des conflits ni de conservation automatique de deux variantes. |
| 174 | Historique des configurations | **Partiel** | Trente versions de réglages, récupération et restauration avec aperçu ; comparaison détaillée champ par champ non implémentée. |
| 175 | Profils de travail complets | **Partiel** | Sources, vues et contextes enregistrés séparément ; aucun profil global réunissant automatiquement source, requêtes et dashboards. |
| 176 | Assistant de première utilisation | **Partiel** | Écran de départ et indications de réglage présents ; pas d’assistant guidé enchaînant source, correspondances et identité personnelle. |
| 177 | Mode démonstration réaliste | **Implémenté** | Démonstration de 180 tickets fictifs, exclue de l’archivage et du déclenchement d’alertes. |
| 178 | Pack de configuration d’équipe | **Implémenté** | Configuration d’équipe versionnée avec documentation, sans cookies, profil SSO, identité, notes ou programmation personnelle. |
| 179 | Packs de requêtes par métier | **Partiel** | Bibliothèque de 80 modèles avec conditions de disponibilité ; pas de packs importables distincts par métier. |
| 180 | Rapport de relevé complet | **Partiel** | Exports de bilan CSV/XLSX, données et PNG disponibles ; pas de rapport unique assemblant graphiques, critères, qualité et tickets. |

## 19. Assistance avancée et extensions facultatives

**Repères dans le dépôt :** [Rapprochements et solutions](../src/GlpiPilot/Core/TicketIntelligenceService.cs) · [Vue des rapprochements](../src/GlpiPilot/UI/TicketIntelligenceView.cs) · [Passations](../src/GlpiPilot/Core/TicketReadingService.cs) · [Calendrier ICS](../src/GlpiPilot/Core/ScheduledTourService.cs).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 181 | Recherche de tickets similaires | **Implémenté** | Rapprochements locaux par titre, termes, ville et catégorie, avec preuves et références aux tickets retenus. |
| 182 | Repérage des incidents récurrents | **Partiel** | Signal de récurrence possible dans le relevé courant ; fréquence sur plusieurs relevés et suivi par équipement non calculés. |
| 183 | Vue des relations entre dossiers | **Partiel** | Rubriques du dossier et rapprochements locaux consultables ; pas d’explorateur structuré des liens tickets/problèmes/changements/équipements. |
| 184 | Solutions déjà rencontrées | **Implémenté** | Solutions réellement présentes dans une colonne ou une lecture antérieure, avec ticket source, statut résolu/clos et date de lecture. |
| 185 | Création de requêtes en langage naturel | **Option externe** | Aucun moteur d’IA raccordé pour traduire le langage naturel en conditions ; le constructeur actuel reste déterministe. |
| 186 | Synthèses assistées et traçables | **Option externe** | Aucun moteur d’IA ni pipeline de synthèse avec citations ; choix explicite du moteur et du périmètre à concevoir. |
| 187 | Brouillons de suivis structurés | **À poursuivre** | Passations et notes locales existent, mais pas de modèles dédiés de brouillons de suivis GLPI alimentés par une sélection de données. |
| 188 | Ajout assisté d’un suivi ou d’une tâche | **Option externe** | Aucune publication de suivi/tâche ; adaptateur validé, droits, prévisualisation et relecture nécessaires avant cette extension. |
| 189 | Export des rendez-vous en calendrier | **Implémenté** | Export ICS des étapes datées, identités stables, fuseau, échappement et contrôles des horaires ; import agenda laissé à l’utilisateur. |
| 190 | Météo terrain facultative | **Option externe** | Aucun fournisseur de prévisions météo raccordé ; les météos actuelles sont des indices métier, sans données climatiques. |

## 20. Robustesse, performances et distribution Windows

**Repères dans le dépôt :** [Version et chemins](../src/GlpiPilot/Core/AppPaths.cs) · [Instance unique](../src/GlpiPilot/Core/SingleInstanceCoordinator.cs) · [Démarrage et sauvegardes](../src/GlpiPilot/Core/StartupBackupService.cs) · [Diagnostics](../src/GlpiPilot/Core/StartupDiagnostics.cs) · [Construction des packs](../build.ps1) · [Chaîne Windows](../.github/workflows/windows.yml) · [Promotion des artefacts](../build-tools/publish-release.sh).

| N° | Proposition initiale | État | Preuve disponible ou limite à traiter |
| ---: | --- | --- | --- |
| 191 | Version unique dans toute l’application | **Implémenté** | Version de l’assemblage utilisée au démarrage, dans les diagnostics et dans les métadonnées des packs. |
| 192 | Diagnostics directement exploitables | **Partiel** | Codes stables d’opération, journal et diagnostic nettoyé copiable ; aperçu interactif d’un rapport complet avant partage absent. |
| 193 | Diagnostic de démarrage | **Implémenté** | Diagnostics des dépendances, écritures et erreurs de démarrage avec conseils de récupération ; sauvegarde préalable des préférences. |
| 194 | Instance unique du logiciel | **Implémenté** | Instance unique dans la session Windows, signal limité au compte courant et restauration de la fenêtre existante. |
| 195 | Stockage local indexé | **Partiel** | Dictionnaires et index en mémoire pour certains moteurs ; stockage persistant indexé des relevés et historique non implémenté. |
| 196 | Travaux de fond priorisés | **Partiel** | Travaux en arrière-plan, annulation et rejet de résultats périmés ; pas d’ordonnanceur général donnant priorité aux seuls éléments visibles. |
| 197 | Validation sur des cas représentatifs | **Partiel** | Scénarios synthétiques de volumes, encodages, colonnes, chemins et tailles Windows ; veille, moniteurs physiques et portail réel ne sont pas validés intégralement. |
| 198 | Signature et provenance des versions | **Option externe** | Empreintes, commit source et inventaire/licences livrés ; signature authentifiée de l’EXE absente, nécessitant un certificat et une chaîne de signature. |
| 199 | Mise à jour avec retour arrière | **Partiel** | Sauvegardes de réglages et remplacement manuel du pack documentés ; pas de mise à jour automatique vérifiée avec retour arrière des binaires. |
| 200 | Contrôle automatique des packs PROD et DEV | **Implémenté** | Chaîne vérifiant contenu PROD/DEV, même EXE, empreintes, commit source et démarrage/zoom du pack avant promotion en Release. |

## Ordre de reprise conseillé

1. **Fiabilité de collecte et séparation des sources** : 1–4, 6–9, 11, 13–14, 21–27. Terminer les profils complets, la preuve de collecte et la reconnexion avant d’ajouter davantage d’automatismes.
2. **Confort de consultation** : 42, 46–49, 52, 54, 56–60, 65–67, 70, 114, 116–120, 130. Achever les fonctions déjà proches de la demande, notamment la sélection persistante et la reprise de contexte.
3. **Historique exploitable au quotidien** : 34–35, 77–79, 91, 93–97, 100, 127, 163–167. Les comparaisons sont présentes ; les flux, explications, cohortes et alertes de variation restent distincts.
4. **Préparation du terrain** : 121, 133–136, 141–147, 150–151, 154, 157–160, 168–170. Relier journée, contraintes de disponibilité, lieux et tournées sans supposer les données manquantes.
5. **Extensions et distribution** : 80, 98, 140, 156, 175, 180, 183, 185–188, 190, 195–199. Conserver les prérequis explicités pour les services externes, écritures GLPI, signature et mise à jour.

La liste conserve volontairement les exigences non réalisées. Une nouvelle version doit mettre à jour les lignes concernées et leurs preuves avant de changer leur état.
