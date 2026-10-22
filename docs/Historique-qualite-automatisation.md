# Historique, qualité et automatisations — version 2.1

Ces fonctions travaillent à partir des données réellement recueillies. Une ligne absente d’un relevé ne prouve pas sa résolution ; une donnée inconnue n’est jamais transformée en zéro. Les fonctions d’écriture GLPI disposent d’un parcours distinct avec aperçu et confirmation.

## Archives et comparaisons

L’archivage conserve cellules, provenance, règles de calcul, requêtes et référentiel géographique. Les nouvelles archives conservent aussi les décisions de localisation effectivement utilisées et l’inventaire daté des effectifs/postes. Les anciennes archives restent lisibles ; une preuve absente rend indisponible seulement le filtre ou l’indicateur qui en dépend.

La conservation se règle par âge, nombre et budget disque. Les réglages initiaux sont de 180 jours, 500 archives et 1 024 Mio. Les archives protégées et les derniers relevés utiles de chaque source sont conservés, même si cela fait dépasser l’objectif de rétention. Le catalogue persistant et les index d’identifiants sont reconstructibles depuis les archives.

Une archive s’ouvre à sa date et avec ses règles. Les changements actuels de dictionnaire, de seuil ou de correction locale ne réécrivent pas son contenu. Ouvrir la fiche GLPI d’un ticket lance une consultation actuelle, distincte de cette preuve historique.

La comparaison métier exige des relevés complets, de même recherche et contexte observable, avec schémas, règles et identités compatibles. Elle distingue apparitions, absences et changements de champs. Résolutions, réouvertures et transferts exigent le même identifiant observé des deux côtés. La file de lecture ciblée permet, sur demande, de vérifier l’état actuel des absents.

## Séries et analyses historiques

Les séries matérialisent les observations, les incompatibilités et les trous de collecte par rapport à la cadence attendue. Une rupture n’est pas comblée par une valeur interpolée. Les mini-historiques des indicateurs recalculent l’identité personnelle et les définitions des filtres dans chaque archive comparable : ils ne réutilisent ni les numéros de lignes ni la sélection du relevé actuel.

Le recalcul est borné à 120 archives, deux millions de lignes de calcul et 250 000 valeurs par opération. Les limites et preuves manquantes créent des ruptures explicites. Pour les taux par site, les effectifs/postes doivent être valables à la date observée et leur source reste consultable.

Les analyses proposent cohortes à J+1/J+7/J+30, mouvements de périmètre, transferts, calendrier des événements observés, décomposition des variations et anomalies robustes. Le temps dans un statut comporte des bornes d’incertitude entre deux observations. Les scénarios linéaires de stock affichent leurs hypothèses ; ils ne sont pas des prévisions garanties.

## Qualité et correspondances

L’espace **Qualité** sépare complétude de l’extraction, présence des champs et interprétation. Il présente valeurs renseignées/vides/illisibles, diversité bornée, exemples et lignes en cause. Les contrôles portent notamment sur identifiants répétés, dates futures ou incohérentes et statuts inconnus.

Le schéma est mémorisé par source : ajouts, suppressions, doublons et changements d’ordre déclenchent une information ; les renommages restent des propositions. L’assistant présente valeur brute et interprétée avant application. Une archive est en lecture seule ; les correspondances d’un import local s’appliquent au seul résultat concerné. Un résultat d’un autre portail ne modifie pas le profil actuellement actif.

Chaque source peut définir culture de dates, convention numérique, fuseau, séparateurs multivaleurs et dictionnaires de normalisation. Les cellules brutes sont conservées et accessibles par **F2**.

Un CSV/XLSX local garde une provenance locale non certifiée GLPI. Le lecteur XLSX utilise la première feuille visible et les valeurs déjà enregistrées ; il n’exécute ni formules ni liens externes. Les colonnes atypiques restent à vérifier dans l’aperçu.

## Alertes et santé des collectes

Une règle porte sur une mesure ou une requête, avec seuil, variation absolue ou variation en pourcentage. Les variations exigent une référence comparable ; une référence absente ou un dénominateur nul reste inconnu. Les évaluations utilisent le relevé courant global. Démonstrations, archives rouvertes et vues personnelles ne déclenchent pas ces alertes.

Un incident est isolé par source et définition de règle. Les occurrences répétées sont regroupées et les nouveaux tickets contributeurs distingués. Une mesure inconnue, partielle ou insuffisamment couverte ne résout pas un incident. Le retour à la normale doit être prouvé ; une résolution manuelle exige un motif et conserve l’historique.

L’onglet **Santé des collectes** présente fraîcheur, collectes attendues manquantes et échecs répétés. Les plages de tranquillité s’appliquent aux lancements programmés et aux rappels personnels Windows. Aucun courriel ou message à un collègue n’est envoyé.

## Vues du relevé et reprise du travail

Les vues nommées mémorisent recherche, filtres typés, quatre niveaux de tri et présentation des colonnes. Les en-têtes permettent de filtrer directement ; les familles facilitent affichage/masquage groupés. Les filtres et tris s’appliquent avant la limite de 10 000 lignes de l’aperçu. L’export de la vue comprend toutes les lignes correspondantes.

Le panier persistant utilise les identités vérifiées des objets et reste indépendant des filtres. Il refuse identifiants ambigus, démonstrations et recherches multi-types sans type fiable. L’ordre/largeur des colonnes est mémorisé par tableau ; sélection et position de lecture sont isolées par source, y compris entre deux imports locaux distincts.

## Sources, campagnes et reprise de collecte

Chaque profil mémorise acquisition, repères, délais, conventions et dossier local. Sur choix explicite, il restaure aussi requêtes et dashboards. Le profil navigateur est séparé par installation GLPI et navigateur. Le prévol contrôle adresses, limites, écriture, verrou de profil et espace disque.

Une campagne parcourt les sources successivement et produit un bilan JSON/CSV par source. Une erreur n’efface pas les réussites et n’empêche pas l’examen des autres sources. Une annulation distingue sources commencées et sources non exécutées.

La pagination peut enregistrer un point de reprise borné. Après interruption, la reprise recontrôle contexte, schéma, total et pages témoins. Une reconnexion SSO ou réseau n’autorise pas à poursuivre avec un périmètre modifié. Le reçu conserve méthode, tri observé, horaires et durées de phases. L’échantillon de l’export natif rapproche les identifiants et les valeurs comparables ; un export corrompu ne devient pas un succès par repli silencieux.

## Programmation et recettes

La programmation est désactivée par défaut. Elle utilise un intervalle (minimum cinq minutes) ou plusieurs horaires fixes sur les jours choisis. Elle exige que GLPI Pilot reste ouvert et la session disponible. Un travail actif, un éditeur, le verrouillage ou une plage silencieuse retardent le lancement. Sortie de veille et retour réseau déclenchent un nouvel examen des échéances, sans tâche Windows permanente.

Une recette commence par une extraction inconditionnelle. Les étapes suivantes — contrôle, enrichissement, archivage et exports — peuvent être ordonnées et conditionnées aux résultats. Le moteur valide la recette avant lancement et journalise les étapes exécutées, ignorées ou échouées. La lecture ciblée de fiches conserve ses propres limites et dates de lecture.

L’ordonnanceur privilégie les vues visibles, annule les calculs remplacés et empêche un résultat périmé de s’afficher. Un dashboard caché dont le calcul a été annulé reprend lorsqu’il redevient visible.

## Configuration, partage et récupération

L’enregistrement est atomique et conserve trente versions valides. Un fichier illisible est préservé et une récupération depuis une version valide reste explicitement signalée. Import et restauration présentent un comparatif détaillé des ajouts, retraits, changements et réordonnancements.

L’import permet de sélectionner les sections et d’arbitrer chaque conflit : courant, importé ou deux variantes. Les identifiants et références liés sont réécrits ensemble lorsque deux variantes sont conservées. Les champs inconnus, limites invalides et références incohérentes sont refusés avant application.

Le partage transmet les définitions de travail choisies. Cookies, sessions SSO, identité personnelle, notes privées, dossiers locaux et clé du fournisseur d’assistance restent sur le poste. Les adresses et critères que contient le fichier partagé doivent être relus avant transmission.

Avant un changement de version, les paramètres reconnus sont sauvegardés avec leur empreinte, notamment tournées, panier, tableaux, effectifs, adaptateurs et politique de mise à jour. L’assistant de mise à jour vérifie provenance et contenu, sauvegarde les binaires et restaure la version précédente si le démarrage échoue. Voir [Guide 2.1](Guide-2.1.md) pour les commandes et la signature facultative.
