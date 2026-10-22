# Travail personnel, lecture des tickets et organisation des techniciens

Cette partie de GLPI Pilot travaille sur les données collectées par le navigateur et sur des préférences enregistrées sur le poste. Les propositions de répartition, notes, rappels et brouillons restent locaux. La page **Actions assistées** permet séparément une affectation ou l’ajout d’un suivi ou d’une tâche dans GLPI : chaque envoi exige un formulaire natif reconnu, un aperçu à relire et une confirmation explicite. Une simulation ou un brouillon ne déclenche aucun envoi.

## Lire plusieurs tickets

L’espace **Tickets** conserve jusqu’à douze onglets internes. Le numéro de ticket reste associé à son portail et à son installation GLPI ; le même numéro sur un autre portail désigne un autre dossier. Fermer un onglet laisse disponibles ses notes, rappels et éventuelle copie hors connexion.

Les pages de la fiche comprennent :

- **Fiche GLPI** : menu des rubriques découvertes et des sections configurées, avec traitement, suivis, tâches, solutions, validations et documents lorsque leur contenu est accessible. Les outils de lecture proposent les filtres, la chronologie, le tableau, les favoris et l’ordre des rubriques.
- **Synthèse** : date de lecture, couverture réellement lue, volumes d’événements, dernier échange daté, prochaine tâche explicitement planifiée, validations en attente et champs disponibles.
- **Mon travail** : notes personnelles, liste de vérification, épingle, file de travail, rappel, report et conservation hors connexion.
- **Chercher partout** : recherche insensible aux accents dans toutes les sections déjà collectées, avec retour à la rubrique d’origine. La présentation est limitée à 2 000 résultats.
- **Passation** : texte préparé à partir des données lues et des actions personnelles, à relire et copier.
- **Rapprochements** : comparaison locale avec d’autres tickets du relevé, consultation des solutions disponibles et analyse des récurrences sur des archives compatibles.
- **Planification et relations** : tâches planifiées, validations, mises en attente, dernier échange, relations métier et index des documents.
- **Brouillon de suivi** : modèles de suivi, faits saisis et sélection explicite du contexte à inclure, puis texte modifiable avant copie ou préparation d’un ajout assisté.
- **Actions assistées** : inspection du formulaire ouvert dans GLPI, aperçu de l’action, confirmation d’un envoi unique et résultat de sa relecture.

Le raccourci **Lectures ciblées** ouvre la file de tickets à lire avec ordre, priorité, limite par lancement, annulation et résultats datés. Les listes et tableaux sont paginés. Une cellule longue dispose de **Lire le texte complet** : le texte collecté s’ouvre intégralement et reste du texte, même s’il contient des balises ou du code.

La chronologie place un repère **DERNIÈRE LECTURE** entre les événements datés. Les empreintes conservées indiquent les rubriques modifiées et surlignent les lignes ou événements nouveaux ou modifiés. La comparaison est bornée à 10 000 lignes par fiche ; une comparaison incomplète reste signalée. Ce mécanisme compare deux observations et ne constitue pas un historique exhaustif des modifications GLPI.

Les rubriques sont déplaçables vers le haut ou vers le bas depuis les outils de lecture. Leur ordre personnalisé, les favoris et la rubrique d’ouverture sont mémorisés. Une rubrique absente d’une nouvelle lecture est signalée « non relue ». Si la lecture différée d’un onglet échoue, ses anciennes observations gardent leur date et ne sont pas présentées comme une nouvelle lecture réussie.

La recherche et les index de lecture portent sur les textes effectivement recueillis. Les pièces jointes ne sont ni téléchargées automatiquement ni analysées par ces index ; leur téléchargement volontaire est décrit ci-dessous. Les onglets non lus et les limitations de lecture restent indiqués.

## Tâches, échanges, attentes et validations

Les tableaux de **Planification et relations** distinguent les données observées des informations manquantes. Une colonne **Lu le** indique la date de l’observation utilisée ; « — » signifie que le résultat n’est pas déterminé.

| Vue | Informations utilisées | Limite d’interprétation |
| --- | --- | --- |
| Tâches planifiées | Responsable, échéance ou fin prévue, début prévu, état et achèvement explicitement affichés | Une tâche n’est comptée en retard que si son échéance est passée à la date de lecture et que son état incomplet est connu. La date d’une entrée de chronologie n’est jamais transformée en échéance. |
| Dernier échange | Dernier suivi daté recueilli, auteur et rôle explicitement présent dans la page | Le nom seul ne prouve pas un rôle de demandeur ou de technicien. Une réponse non observée ne prouve pas une attente contractuelle. |
| Mises en attente | Motif et bornes de l’intervalle, ou début et état « en cours » explicites | La durée est calculée uniquement sur cet intervalle ; l’âge du ticket n’est pas une durée d’attente. Une attente en cours est arrêtée à sa date d’observation. |
| Validations | État lu, responsable, date de demande explicite et ancienneté à la lecture | Un intitulé de rubrique ou une date quelconque ne suffit pas à établir qu’une validation est en attente. |

L’extraction utilise les champs et attributs visibles de la page, ainsi que des tableaux dont les en-têtes correspondent explicitement aux informations recherchées. Un portail ou un module qui ne fournit pas ces repères produit des valeurs inconnues, sans estimation implicite. Les dates sans fuseau sont interprétées dans le fuseau local du poste ; ces observations ne reconstruisent pas un calendrier SLA.

## Relations et documents

**Relations** réunit les liens observés vers les tickets, problèmes, changements, équipements, logiciels, utilisateurs et groupes reconnus. Un ticket lié s’ouvre dans le lecteur ; les autres objets s’ouvrent dans une page GLPI dédiée. Les liens doivent correspondre à la même origine et à la même installation GLPI, avec une identité numérique explicite et une destination de consultation autorisée. La recherche de départ est conservée.

**Documents observés**, dans **Planification et relations**, présente les pièces dont un lien de téléchargement vérifiable a été recueilli. Cliquez sur **Télécharger…** sur la ligne voulue, puis choisissez son emplacement. Le transfert utilise la session authentifiée du navigateur piloté. Ouvrir la fiche ou son index ne télécharge rien. Les redirections sont contrôlées ; une page HTML de connexion n’est pas enregistrée comme document. Le téléchargement est borné à 50 Mio et les fichiers partiels sont retirés en cas d’échec ou d’annulation. Le contenu du document n’est pas indexé par cette fonction.

## Ma journée

Dans **Équipe**, choisissez votre identité parmi celles du relevé avec **C’est moi** pour ouvrir **Ma journée**. Depuis le relevé, une sélection de plusieurs tickets peut aussi être ajoutée à la file personnelle. Les actions personnelles restent rattachées à leurs dossiers, même si une collecte remplace ensuite le relevé affiché.

La page propose les périmètres Aujourd’hui, Mes tickets du relevé, File personnelle, Épinglés, Rappels à traiter, Reportés et Tous les conservés. L’ordre de la file est modifiable. Un ticket absent du relevé courant conserve la mention d’un état non vérifié.

Les onglets **Briefing de session** et **Tournées du jour** complètent cette liste. Le briefing assemble les priorités et échéances observées, les nouvelles présences dans le périmètre personnel et les changements d’affectation, de statut ou d’échéance depuis la session précédente. Les tournées enregistrées pour la date du jour affichent leur état, leurs horaires et leurs numéros de ticket. Les tournées du technicien choisi et les tournées sans technicien désigné restent visibles. Un raccourci ouvre leur planification. Les onglets **Modèles de listes** et **Mes rappels** donnent accès aux modèles et aux préférences de notification.

Le briefing compare uniquement des relevés de recherche et de contexte GLPI vérifiés, avec la même identité personnelle, les mêmes colonnes et les mêmes règles d’interprétation. Il indique ses dates et sa couverture. Une première observation ou un changement de source donne un point de départ sans comparaison ; une nouvelle présence dans la liste ne devient pas une date d’affectation supposée. Consulter une archive ne remplace pas le repère de la session courante. Les tickets absents restent d’état inconnu hors du relevé.

Les notes et listes disposent d’une action explicite **Enregistrer mon travail**. Les modèles de vérification peuvent être créés, modifiés et réutilisés. Un rafraîchissement des rappels conserve un brouillon de notes encore non enregistré lorsque le dossier sélectionné n’a pas changé.

Un rappel personnel et une date de reprise n’affectent pas le statut ni l’échéance du ticket dans GLPI. Les rappels sont examinés périodiquement pendant que l’application est ouverte et disponible. Une notification Windows affiche seulement le nombre de rappels ; son clic ouvre Ma journée. Cette notification est désactivable dans **Mes rappels**. Les plages de silence et le verrouillage de la session Windows suspendent les notifications sans consommer les rappels en attente. L’application n’acquitte l’affichage qu’après la réponse positive de l’hôte Windows. Les réglages de Windows peuvent masquer la notification ; la liste de rappels reste consultable dans l’application.

Les préférences de travail sont enregistrées dans `%LOCALAPPDATA%\GLPI-Pilot\workplace.json`. Le document est borné à 500 tickets conservés, 8 000 caractères de notes par ticket, 40 actions par liste, 30 modèles et 12 onglets ouverts. Il est validé avant remplacement atomique du fichier. Un document illisible est préservé avant tout nouvel enregistrement.

## Copies hors connexion

La conservation d’une fiche est volontaire, depuis **Mon travail**, après lecture de cette fiche. Elle comprend uniquement ses sections déjà collectées et leur date de lecture. Une copie ancienne porte son horodatage ; **Actualiser** relit le portail actuel.

Les copies sont séparées par origine, installation et identifiant de ticket. Le cache est limité à 8 Mio par fiche et 64 Mio au total. Décochez la conservation hors connexion pour retirer une copie. Les tickets épinglés, notes et rappels restent indépendants de cette copie.

Les onglets ouverts, la file, les favoris, les modèles et le dernier espace technicien sont mémorisés. Chaque fiche conserve aussi la rubrique, la page interne et le mode de lecture, les filtres de type, auteur et dates, les recherches locales, la ligne sélectionnée et le défilement du lecteur. Ma journée conserve sa recherche, son périmètre et sa sélection. Une ligne disparue ou un contenu réorganisé peut déplacer le point visible ; la restauration ne crée pas de données absentes.

## Enrichir une sélection ou vérifier des absences

Dans l’aperçu, **Enrichir la sélection** prépare les lignes sélectionnées ; **Enrichir la requête** prépare le périmètre affiché. La file contient au maximum 1 000 références exploitables et signale les exclusions ou la présentation tronquée. Vous pouvez donner la priorité à un ticket puis choisir de 1 à 100 lectures pour un lancement.

La lecture est séquentielle dans la page dédiée du navigateur et peut être annulée. Un lancement suivant reprend les candidats encore sans lecture réussie ; **Réinitialiser pour relire la file** permet une nouvelle lecture volontaire. La file est conservée dans `ticket-enrichment.json` et sa restauration ne la lance jamais automatiquement.

Chaque résultat précise sa date de lecture, le statut effectivement recueilli, le nombre de rubriques et les erreurs éventuelles. Les candidats non lus restent inconnus. Le bouton de l’historique **Vérifier les absents dans GLPI** utilise ce même parcours pour des références fiables de la source comparée. Le statut récent reste une observation distincte : une ancienne comparaison conserve la mention « absent du relevé » et ses données ne sont pas réécrites.

## Compétences, secteurs et organisation

La page **Équipe** conserve, pour chaque technicien identifié dans le relevé : compétences, territoires principaux, territoires de remplacement, capacité quotidienne en minutes, durée estimée par ticket, intervalle d’indisponibilité et note de disponibilité. Cliquez sur le nom d’un technicien pour ouvrir ses déclarations. Des tableaux avec champs de jour, heure, date et minutes permettent d’ajouter ses plages hebdomadaires, ses exceptions par demi-journée et ses modèles de durée par catégorie. Ces déclarations sont enregistrées dans `technician-preferences.json` et complètent les favoris, objectifs locaux et notes existants.

Chaque tableau propose **Ajouter une ligne**, puis **Enregistrer les déclarations** applique l’ensemble des champs validés. Exemples :

| Déclaration | Exemple | Signification |
| --- | --- | --- |
| Présence récurrente | Lundi ; début 08:00 ; fin 12:00 ; capacité 180 | Présence du lundi matin, avec 180 minutes de capacité déclarée. |
| Exception de capacité | Date 2026-10-26 ; matin 0 ; après-midi 90 | Capacité nulle le matin, 90 minutes l’après-midi à cette date. Une demi-journée vide conserve sa valeur habituelle ou son état inconnu. |
| Modèle de catégorie | Réseau ; minimum 20 ; centrale 40 ; maximum 80 | Catégorie Réseau : borne basse 20, estimation centrale 40, borne haute 80 minutes. |

Les plages qui se chevauchent et les fourchettes incohérentes sont refusées. Une indisponibilité explicite donne une capacité nulle. Une capacité manquante reste inconnue : une capacité quotidienne seule n’est pas arbitrairement divisée en deux demi-journées. Dans un calendrier récurrent renseigné, un jour sans plage déclarée a une capacité habituelle nulle. Une exception peut remplacer ce budget de capacité ; elle n’invente pas de nouveaux horaires de présence pour placer des interventions.

La page **Répartition**, également accessible par le bouton **Organisation** d’Équipe, prépare un scénario daté. Elle comprend une liste de tickets actifs ou dont le statut reste inconnu, les propositions de techniciens, un tableau de couverture territoriale, une comparaison de charge estimée avant/après et une préparation de passation pour une absence. **Couverture sur 7 jours** croise territoires et compétences déclarés avec les matinées et après-midis. Les mêmes minutes peuvent apparaître pour plusieurs compétences ou territoires d’une personne : elles constituent une capacité partagée, et ne s’additionnent pas entre cellules de la matrice.

Chaque proposition explique ses critères : compétences requises, territoire principal ou de remplacement, indisponibilité déclarée et capacité disponible à la date choisie. L’absence de déclaration d’indisponibilité n’atteste pas une présence réelle. La charge de départ utilise les modèles correspondant aux catégories identifiées, ou l’estimation générale déclarée lorsqu’aucun modèle de catégorie ne correspond. Les bornes de charge restent inconnues si une contribution n’a pas de fourchette. **Durée du modèle de catégorie** reprend la valeur centrale pour une proposition ; sa fourchette doit encore être vérifiée. Les minutes prévues sont explicites ; les estimations de charge ne sont pas des mesures de temps de travail.

Les scénarios sont enregistrés localement dans `dispatch-scenario.json` et exportables en CSV avec la mention de leur nature de proposition. La simulation retire l’estimation de l’affectation remplacée avant d’ajouter les minutes proposées ; les propositions concernant un ticket absent du relevé sont signalées et exclues de l’impact chiffré. Les tickets à statut inconnu ne sont pas soustraits d’une charge active qui ne les comptait pas.

Un scénario n’applique pas ses propositions à GLPI. Pour une affectation réelle, ouvrez le ticket puis utilisez séparément le parcours **Actions assistées** décrit ci-dessous.

## Brouillons et actions GLPI assistées

**Brouillon de suivi** propose quatre modèles : compte rendu d’intervention, diagnostic et prochaine action, demande de complément et passation technique. Renseignez le constat, l’action et la suite attendue, puis sélectionnez les éléments factuels de la synthèse à inclure. La composition produit un texte modifiable. Le texte du brouillon est enregistré automatiquement sur le poste ; les champs de composition inachevés sont également conservés pendant les changements de fiche de la session. Copier un brouillon ou le reprendre dans les actions assistées ne le publie pas.

Changer de fiche ou de page attend l’enregistrement du dernier texte. Fermer l’application annule d’abord les traitements en cours, puis attend cette sauvegarde ; une sauvegarde qui attendait derrière une collecte annulée est resoumise avec le dernier texte. Si l’enregistrement échoue, la page reste ouverte, le texte reste modifiable et l’erreur est affichée. Une nouvelle tentative est possible après correction de l’emplacement.

Pour une affectation ou l’ajout d’un suivi ou d’une tâche :

1. Ouvrez **Actions assistées**, puis **Afficher la fiche GLPI**. Dans cette fiche dédiée, ouvrez le formulaire natif correspondant à l’action voulue.
2. Choisissez **Inspecter le formulaire ouvert**. L’application reconnaît uniquement les formulaires POST autorisés, rattachés au ticket courant et à la même installation. Un formulaire ambigu, une cible étrangère, un champ inattendu ou la modification d’une entrée existante est refusé.
3. Choisissez une valeur d’affectation réellement proposée par le formulaire, ou préparez le texte à ajouter. **Mémoriser cet adaptateur** conserve les repères du formulaire pour cette installation ; cette configuration ne contient aucun script exécutable.
4. Cliquez sur **Prévisualiser l’action**. L’aperçu indique le ticket, l’opération, la valeur avant/après et les autres champs non secrets qui accompagneront l’envoi. L’aperçu ne remplit ni ne soumet le formulaire. Il expire après deux minutes ; changer la valeur l’invalide.
5. Après relecture, cliquez sur **Confirmer l’aperçu et envoyer une fois…**, puis confirmez dans la boîte de dialogue. Les droits restent ceux de la session GLPI. L’installation, le ticket, la structure et l’état du formulaire sont contrôlés à nouveau avant une soumission unique.

Une référence unique, visible dans l’aperçu, est ajoutée au texte des nouveaux suivis et tâches pour permettre leur identification lors de la relecture. L’application recharge la fiche et recherche une preuve de la valeur d’affectation ou de la nouvelle entrée. Le reçu local conserve l’intention avant l’envoi, puis son résultat.

Un envoi peut avoir réussi côté serveur alors que la réponse réseau ou la relecture échoue. Dans ce cas, le résultat reste **indéterminé** : il n’y a aucune relance automatique. Vérifiez directement la fiche et sa référence d’action avant de préparer un autre envoi. Le même aperçu ne peut pas être soumis deux fois. Les reçus sont conservés dans `workplace-action-receipts.json` ; une vérification manuelle déclarée lève le blocage d’un nouvel aperçu, sans transformer rétroactivement un résultat indéterminé en preuve de réussite.

Ce parcours prend en charge les champs natifs reconnus, y compris un choix Select2 ou un éditeur TinyMCE associé de façon vérifiable à son champ. Les formulaires d’une extension, les champs dynamiques non reconnus ou une structure différente exigent une nouvelle inspection ; ils peuvent rester disponibles uniquement dans GLPI. La prise en charge logicielle ne démontre pas à elle seule la compatibilité avec un portail particulier.

## Rapprochements et solutions collectées

Les rapprochements utilisent des règles locales explicites : titre normalisé, termes distinctifs communs, catégorie et ville. La normalisation conserve les chiffres présents dans les titres. Un score local exprime ces règles ; il ne représente pas une probabilité de doublon.

Une suggestion de récurrence exige un titre normalisé identique et une catégorie ou une ville identique. Des titres proches nécessitent au moins deux termes distinctifs communs et un recouvrement lexical d’au moins 50 %. Les cinquante meilleurs rapprochements sont présentés avec leurs critères.

Le ticket courant et chaque candidat doivent avoir un identifiant unique dans le relevé. Le portail et le sous-répertoire GLPI doivent correspondre. Les doublons d’identifiant ne sont pas fusionnés.

Les solutions affichées proviennent soit d’une colonne Solution non ambiguë, soit d’une chronologie déjà lue en mémoire. Le relevé doit présenter le ticket source comme résolu ou clos. Le texte, le ticket source et la date de lecture sont affichés. Un texte absent reste indisponible. Ces rapprochements et cet index de solutions s’exécutent localement.

L’analyse de récurrence sur plusieurs relevés est lancée explicitement depuis **Rapprochements**. Elle utilise le relevé courant et des archives compatibles, dans la limite de 20 relevés et 500 000 lignes examinées. Le même identifiant vu dans plusieurs relevés reste un seul dossier ; le nombre d’observations n’est pas présenté comme un nombre d’incidents.

Les résultats distinguent les dossiers différents observés, leurs premières et dernières observations, et les créations explicitement datées dans la période. Une colonne d’équipement non ambiguë peut être choisie pour rapprocher des dossiers du même équipement, même si leurs titres diffèrent. Les identifiants ambigus, conflits et archives exclues réduisent la couverture indiquée. La fréquence observée ne prouve ni une cause commune ni une nouvelle panne pour chaque apparition d’un même ticket. L’affichage est limité à 200 résultats.

## Correspondances d’un relevé et d’une source

**Données → Correspondances, formats et valeurs** applique des règles de lecture sans réécrire les cellules. Pour un relevé GLPI, la recherche et le contexte doivent être vérifiés et correspondre à la configuration active ; les colonnes d’une autre source ne peuvent pas être enregistrées implicitement dans ce profil. Une archive affiche son interprétation enregistrée en lecture seule.

Pour un CSV ou classeur local, **Appliquer au relevé local uniquement** conserve les règles pendant la session pour ce résultat. Aucun profil GLPI actif n’est modifié. Sélectionner un autre profil ne change pas ces règles locales. Changer les correspondances retire les localisations calculées avec les anciennes règles et conserve les cellules d’origine pour les calculs suivants. Les sélections de lecture des imports et des démonstrations sont isolées même lorsque deux fichiers comportent le même identifiant de ticket.

## Démarrage Windows et conservation des préférences

Un second lancement signale l’instance déjà ouverte dans la même session Windows et lui demande de restaurer sa fenêtre. Le canal interprocessus est limité au compte courant et accepte uniquement cette activation. Si une ancienne version ne comprend pas le signal, un message indique de retrouver la fenêtre dans la barre des tâches.

Avant le premier démarrage réussi d’une nouvelle version, une sauvegarde locale des fichiers de préférences connus est créée et vérifiée par empreintes SHA-256 dans `%LOCALAPPDATA%\GLPI-Pilot\Backups`. Elle inclut les réglages, préférences techniciens, travail personnel, scénarios de répartition, ordre des tournées, vues du tableau, corrections de villes et état des alertes présents sur le poste. Le profil et les sessions du navigateur ne sont pas copiés.

Une sauvegarde identique déjà vérifiée est réutilisée après un lancement interrompu. La sauvegarde est bornée à 16 Mio par fichier, 12 archives et 256 Mio ; lorsque cette limite est atteinte, le journal explique de déplacer les anciennes archives. Les sauvegardes existantes ne sont pas supprimées automatiquement. Le repère de nouvelle version est enregistré seulement après le premier rendu effectif de la fenêtre et une préparation de sauvegarde réussie.

Le diagnostic de démarrage conserve les étapes, types d’erreur et codes HResult, avec des messages nettoyés. Il propose des actions adaptées aux permissions d’écriture, dépendances manquantes, mémoire insuffisante ou erreurs d’interface. Une sauvegarde préalable défaillante est signalée dans ce journal.

## Vérification

Les tests cœur du dossier `tests/WorkplaceChecks` couvrent la persistance, l’isolation des portails, les limites de stockage, les filtres et positions de lecture, les observations structurées, le briefing, les calendriers et capacités, les estimations de charge, les scénarios, les récurrences et les files d’enrichissement. `tests/TechnicianWorkloadChecks` conserve les contrôles de calcul de charge. `tests/CompletionChecks` vérifie notamment la séparation des correspondances entre source active, import local et archive. `tests/StartupResilienceChecks` contrôle les sauvegardes avant changement de version et les conseils de diagnostic.

`tests/WorkplaceBridgeChecks` charge les pages WebView réelles et appelle les handlers C# réels sur des données synthétiques. Il contrôle les filtres restaurés, les observations, les notes, les brouillons, le texte long, les calendriers, les propositions, les modèles, les relations et les envois assistés à confirmation distincte. `tests/RuntimeSessionChecks` vérifie les révisions périmées, les profils, l’interprétation des imports, les sélections d’onglets, l’annulation et la sauvegarde du brouillon avant fermeture, y compris après un échec d’écriture.

`tests/TicketDetailChecks` exécute Chromium sur des pages GLPI synthétiques. Les contrôles couvrent la collecte d’observations, l’absence de téléchargement implicite, l’aperçu sans mutation, la soumission unique, la relecture, les refus d’origine ou d’identité, les champs inattendus, les aperçus périmés et les réponses ambiguës sans relance. Les téléchargements utilisent un serveur local synthétique avec vérification du contexte authentifié. Ces contrôles ne soumettent aucune action à un portail d’entreprise.

La suite Windows démarre l’hôte WinForms et WebView2 avec des données synthétiques, sans utiliser les réglages ni le navigateur d’un utilisateur. Le test du pack démarre l’exécutable publié, vérifie le premier rendu de l’interface locale, l’activation de l’instance existante, la fermeture normale et la conservation des réglages synthétiques. Une compilation croisée seule ne valide pas l’exécution de ces contrôles Windows. L’apparition effective d’une notification dépend aussi des paramètres de notification du poste.
