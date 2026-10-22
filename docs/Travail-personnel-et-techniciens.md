# Travail personnel, lecture des tickets et organisation des techniciens

Cette partie de GLPI Pilot travaille sur les données collectées par le navigateur et sur des préférences enregistrées sur le poste. Les propositions de répartition et les notes personnelles sont conservées localement.

## Lire plusieurs tickets

L’espace **Détail du ticket** conserve jusqu’à douze onglets internes. Le numéro de ticket reste associé à son portail et à son installation GLPI ; le même numéro sur un autre portail désigne un autre dossier. Fermer un onglet laisse disponibles ses notes, rappels et éventuelle copie hors connexion.

Les pages de la fiche comprennent :

- **Fiche GLPI** : menu des rubriques découvertes et des sections configurées, avec traitement, suivis, tâches, solutions, validations et documents lorsque leur contenu est accessible.
- **Synthèse du traitement** : date de lecture, couverture réellement lue, volumes d’événements, derniers événements datés et champs d’attente, validation ou échéance disponibles.
- **Mon travail** : notes personnelles, liste de vérification, épingle, file de travail, rappel, report et conservation hors connexion.
- **Chercher partout** : recherche insensible aux accents dans toutes les sections déjà collectées, avec retour à la rubrique d’origine. La présentation est limitée à 2 000 résultats.
- **Passation** : texte préparé à partir des données lues et des actions personnelles, à relire et copier.
- **Lecture** : filtres par type d’événement, auteur et période, regroupements repliables par jour, rubriques favorites et rubrique d’ouverture.
- **Rapprochements** : comparaison locale avec d’autres tickets du relevé et consultation des textes de solutions disponibles.

Une date d’événement de la chronologie n’est pas utilisée comme date de planification d’une tâche. Une rubrique absente d’une nouvelle lecture est signalée « non relue ». Les empreintes de contenu indiquent les rubriques modifiées depuis la dernière lecture mémorisée ; ce mécanisme ne constitue pas un historique exhaustif des modifications GLPI.

La recherche et les index de lecture portent sur les textes effectivement recueillis. Le contenu interne des fichiers joints n’est pas téléchargé ni analysé. Les onglets non lus et les limitations de lecture restent indiqués.

## Ma journée

Dans **Techniciens**, choisissez votre identité parmi celles du relevé avec **C’est moi**, puis ouvrez **Ma journée**. Le bouton **Dans ma journée** de l’aperçu ajoute également une sélection de plusieurs tickets à la file personnelle ; Ctrl et Maj permettent la sélection multiple.

La page propose les périmètres Aujourd’hui, Mes tickets du relevé, File personnelle, Épinglés, Rappels à traiter, Reportés et Tous les conservés. L’ordre de la file est modifiable. Un ticket absent du relevé courant conserve la mention d’un état non vérifié.

Les notes et listes disposent d’une action explicite **Enregistrer mon travail**. Les modèles de vérification peuvent être créés, modifiés et réutilisés. Un rafraîchissement des rappels conserve un brouillon de notes encore non enregistré lorsque le dossier sélectionné n’a pas changé.

Un rappel personnel et une date de reprise n’affectent pas le statut ni l’échéance du ticket dans GLPI. Les rappels sont examinés chaque minute pendant que l’application est ouverte. Une notification Windows affiche seulement le nombre de rappels ; son clic ouvre Ma journée. Cette notification est désactivable dans **Préférences personnelles**. Les réglages de Windows peuvent masquer la notification ; la liste de rappels reste consultable dans l’application.

Les préférences de travail sont enregistrées dans `%LOCALAPPDATA%\GLPI-Pilot\workplace.json`. Le document est borné à 500 tickets conservés, 8 000 caractères de notes par ticket, 40 actions par liste, 30 modèles et 12 onglets ouverts. Il est validé avant remplacement atomique du fichier. Un document illisible est préservé avant tout nouvel enregistrement.

## Copies hors connexion

La conservation d’une fiche est volontaire, depuis **Mon travail**, après lecture de cette fiche. Elle comprend uniquement ses sections déjà collectées et leur date de lecture. Une copie ancienne porte son horodatage ; **Actualiser** relit le portail actuel.

Les copies sont séparées par origine, installation et identifiant de ticket. Le cache est limité à 8 Mio par fiche et 64 Mio au total. Décochez la conservation hors connexion pour retirer une copie. Les tickets épinglés, notes et rappels restent indépendants de cette copie.

Les onglets ouverts, la file, les favoris, les modèles et le dernier espace technicien sont mémorisés. Le défilement exact et tous les filtres temporaires de lecture ne sont pas restaurés.

## Compétences, secteurs et organisation

La page **Équipe** conserve, pour chaque technicien identifié dans le relevé : compétences, territoires principaux, territoires de remplacement, capacité quotidienne en minutes, durée estimée par ticket, intervalle d’indisponibilité et note de disponibilité. Ces déclarations complètent les favoris, objectifs locaux et notes existants.

L’onglet **Organisation** prépare un scénario daté. Il comprend une liste de tickets actifs ou dont le statut reste inconnu, les propositions de techniciens, un tableau de couverture territoriale, une comparaison de charge estimée avant/après et une préparation de passation pour une absence.

Chaque proposition explique ses critères : compétences requises, territoire principal ou de remplacement, indisponibilité déclarée et capacité disponible. L’absence de déclaration d’indisponibilité n’atteste pas une présence réelle. Les minutes prévues par ticket sont explicites ; les estimations de charge ne sont pas des mesures de temps de travail.

Les scénarios sont enregistrés localement dans `dispatch-scenario.json` et exportables en CSV avec la mention de leur nature de proposition. La simulation retire l’estimation de l’affectation remplacée avant d’ajouter les minutes proposées ; les propositions concernant un ticket absent du relevé sont signalées et exclues de l’impact chiffré. Les tickets à statut inconnu ne sont pas soustraits d’une charge active qui ne les comptait pas.

L’application ne modifie pas les affectations GLPI. Une écriture future exige des repères réellement vérifiés dans le portail, les droits correspondants, une prévisualisation explicite et une relecture de contrôle.

## Rapprochements et solutions collectées

Les rapprochements utilisent des règles locales explicites : titre normalisé, termes distinctifs communs, catégorie et ville. La normalisation conserve les chiffres présents dans les titres. Un score local exprime ces règles ; il ne représente pas une probabilité de doublon.

Une suggestion de récurrence exige un titre normalisé identique et une catégorie ou une ville identique. Des titres proches nécessitent au moins deux termes distinctifs communs et un recouvrement lexical d’au moins 50 %. Les cinquante meilleurs rapprochements sont présentés avec leurs critères.

Le ticket courant et chaque candidat doivent avoir un identifiant unique dans le relevé. Le portail et le sous-répertoire GLPI doivent correspondre. Les doublons d’identifiant ne sont pas fusionnés.

Les solutions affichées proviennent soit d’une colonne Solution non ambiguë, soit d’une chronologie déjà lue en mémoire. Le relevé doit présenter le ticket source comme résolu ou clos. Le texte, le ticket source et la date de lecture sont affichés. Un texte absent reste indisponible. Aucune requête vers un service d’IA ou d’analyse externe n’est effectuée.

## Démarrage Windows et conservation des préférences

Un second lancement signale l’instance déjà ouverte dans la même session Windows et lui demande de restaurer sa fenêtre. Le canal interprocessus est limité au compte courant et accepte uniquement cette activation. Si une ancienne version ne comprend pas le signal, un message indique de retrouver la fenêtre dans la barre des tâches.

Avant le premier démarrage réussi d’une nouvelle version, une sauvegarde locale des fichiers de préférences connus est créée et vérifiée par empreintes SHA-256 dans `%LOCALAPPDATA%\GLPI-Pilot\Backups`. Elle inclut les réglages, préférences techniciens, travail personnel, scénarios de répartition, ordre des tournées, vues du tableau, corrections de villes et état des alertes présents sur le poste. Le profil et les sessions du navigateur ne sont pas copiés.

Une sauvegarde identique déjà vérifiée est réutilisée après un lancement interrompu. La sauvegarde est bornée à 16 Mio par fichier, 12 archives et 256 Mio ; lorsque cette limite est atteinte, le journal explique de déplacer les anciennes archives. Les sauvegardes existantes ne sont pas supprimées automatiquement. Le repère de nouvelle version est enregistré seulement après le premier rendu effectif de la fenêtre et une préparation de sauvegarde réussie.

Le diagnostic de démarrage conserve les étapes, types d’erreur et codes HResult, avec des messages nettoyés. Il propose des actions adaptées aux permissions d’écriture, dépendances manquantes, mémoire insuffisante ou erreurs d’interface. Une sauvegarde préalable défaillante est signalée dans ce journal.

## Vérification

Les tests cœur du dossier `tests/WorkplaceChecks` couvrent la persistance, l’isolation des portails, les limites de stockage, la recherche, la chronologie GLPI, les différences observées, les estimations de charge, les scénarios et les rapprochements locaux. `tests/StartupResilienceChecks` contrôle les sauvegardes avant changement de version et les conseils de diagnostic.

La suite Windows construit et rend les contrôles réels avec des données synthétiques, sans utiliser les réglages ni le navigateur d’un utilisateur. Le test du pack démarre l’exécutable publié, vérifie le premier rendu, lance une seconde instance après réduction de la première fenêtre, puis vérifie la fermeture normale et la conservation des réglages synthétiques. L’apparition effective d’une notification dépend aussi des paramètres de notification du poste Windows.
