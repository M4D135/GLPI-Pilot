# 1 Ce qui change en 3.13

La version 3.13 rend la collecte des tickets reprenable et différentielle, permet de transférer une bibliothèque préparée et ajoute le pilotage assisté des tickets. La 3.13.1 corrige l'affichage des résumés de partage et harmonise le repli du menu avec Domenia, Pesto et FILPRO.

## Version 3.13.6

- **Import de configuration.** Une configuration complète exportée par une version 3.12 ou 3.13 est de nouveau importable (chapitre 5).
- **Pack allégé PROD-LIGHT.** Identique au pack PROD sans le runtime WebView2 embarqué ; il démarre sur le runtime WebView2 Evergreen déjà installé sur Windows et le signale s'il manque. Un poste installé avec un pack complet se met à jour avec des packs complets, un poste allégé avec des packs allégés ; l'assistant de mise à jour et le dossier de diffusion réseau proposent l'édition du poste (chapitre 3).

## Version 3.13.5

- **Thème uniforme.** La barre du haut porte quatre icônes de thème : clair, sombre, contraste élevé et suivre Windows. Le même choix existe dans la palette Ctrl K et dans « Réglages », où il remplace la case « Suivre le thème Windows » (chapitre 5).
- **Démonstration dans Réglages.** La démonstration s'ouvre, se réinitialise et se quitte depuis la carte « Démonstration » de « Réglages », avec une confirmation (chapitre 7).

## Version 3.13.4

- **Fiches ouvertes sur l'onglet Ticket.** La collecte demande chaque fiche directement sur son onglet « Ticket » et le lit sans clic. Au banc GLPI 9.5, une collecte de 30 tickets passe de 13 min 30 s à 11 min 24 s.
- **Fiches déjà conservées.** Une fiche lue par la 3.13.2 ou la 3.13.3 est conservée telle quelle tant que la date de dernière modification affichée par GLPI ne change pas. Passez d'abord le poste collecteur en 3.13.4, puis ne collectez plus depuis un poste resté en version antérieure.

## Version 3.13.3

- **Collecte plus rapide.** La lecture d'un ticket n'ouvre plus les onglets sans intérêt pour la bibliothèque (chapitre 8). Au banc GLPI 9.5, une collecte de 30 tickets passe de 29 à 13 minutes.
- **Onglet rouvert par GLPI.** GLPI rouvre un ticket sur le dernier onglet consulté. La lecture finit sur l'onglet « Ticket », et une fiche rouverte sur un onglet vide n'attend plus le délai maximal.
- **Fiches déjà conservées.** Une fiche lue par une version antérieure est conservée telle quelle tant que la date de dernière modification affichée par GLPI ne change pas, même par « Relire tous les contenus » ; une collecte suspendue en 3.13.2 se reprend. Passez d'abord le poste collecteur en 3.13.3, synchronisez la bibliothèque partagée avant sa première collecte, jusqu'à ne plus rien recevoir, puis ne collectez plus depuis un poste resté en 3.13.2 : sinon, des fiches apparaîtraient dans « Versions à comparer ».

## Version 3.13.2

- **GLPI 9.5.** La collecte des tickets lit aussi les pages natives de GLPI 9.5 ; jusqu'ici elle restait sur la liste puis s'arrêtait sans ticket (chapitre 6).
- **Raison affichée.** Si la liste GLPI n'est pas reconnue, la fenêtre de collecte en donne la raison ; pendant la lecture de l'index, elle montre la page et les tickets déjà indexés.
- **Techniciens.** Le guide technicien ne leur fait plus lancer la collecte complète : elle reste au poste collecteur (chapitre 6).

## Version 3.13.1

- **Partage entre postes.** Quand la synchronisation automatique est désactivée, le panneau n'affiche plus de ligne de programmation vide. Le bloc « Données communes » omet aussi ses messages facultatifs absents. Les textes recueillis dans les tickets restent intacts (chapitre 9).
- **Menu de gauche.** La dernière ligne du menu déplié est « Replier le menu », avec l'indice Ctrl B au survol. Menu replié, une icône seule centrée le déplie et le nom de chaque espace s'affiche au survol. Le choix est enregistré avec les préférences d'affichage.
- **Publication vérifiée.** La qualification complète produit les packs sans les publier. La publication est un déclenchement distinct qui exige le run complet réussi sur le même commit et les empreintes SHA-256 des deux originaux inspectés (chapitre 14).

## Version 3.13.0

- **Collecte reprenable.** « Collecte et reprise » commande le moteur de collecte : mise à jour différentielle des contenus, pause, arrêt et reprise, y compris après plusieurs interruptions (chapitre 6).
- **Bibliothèque transférable.** Un paquet .glpilibrary.zip distribue une bibliothèque de départ sans recommencer la collecte (chapitre 8).
- **Pilotage assisté des tickets.** Les actions GLPI passent par un formulaire reconnu, un aperçu et une confirmation unique ; statut, priorité et résolution ouvrent la fiche GLPI (chapitre 12).
- **Ergonomie terrain.** Matériel, tournées et fiches terrain sont regroupés, sans gestion de stock (chapitre 10).

> [!ATTENTION] Avant toute mise à jour, sauvegardez les bases et les réglages, puis alignez sur la même version tous les postes qui collectent ou échangent des paquets dans un même espace partagé (chapitre 14).

# 2 Démarrage rapide

Mise en service d'un poste ou d'une équipe, dans l'ordre. Chaque étape renvoie au chapitre détaillé.

1. **Télécharger le pack.** Prenez le ZIP PROD de la version et vérifiez son empreinte avec SHA256SUMS.txt (chapitre 4).
2. **Extraire.** Extrayez tout le ZIP dans un dossier local et gardez wad à côté des EXE (chapitre 4).
3. **Choisir l'édition.** Distribuez GLPI-Pilot.exe aux agents ; gardez GLPI-Pilot-Admin.exe pour l'administration (chapitre 3).
4. **Régler la source.** Dans « Données », renseignez l'adresse complète de la recherche GLPI et le navigateur, puis lancez une première collecte (chapitre 6).
5. **Régler la bibliothèque.** Choisissez le « Fuseau des dates GLPI » et l'ordre des dates (chapitre 8).
6. **Choisir le partage.** Sur ce poste uniquement, dossier réseau partagé ou PostgreSQL (chapitre 9).
7. **Adapter le pilotage.** Seuils, équipes, sites et règles d'alerte (chapitres 10 et 11).
8. **Préparer la configuration d'équipe.** « Définir comme configuration par défaut » écrit le fichier de déploiement à diffuser avec le pack (chapitre 5).
9. **Désigner le collecteur.** Un seul poste collecteur par périmètre ; activez la programmation seulement sur ce poste (chapitre 6).
10. **Former les techniciens.** Remettez le guide technicien ; la démonstration sert à la formation (chapitre 7).

# 3 Périmètre et architecture

GLPI Pilot est une application Windows x64 qui lit les tickets du portail GLPI par le navigateur, les conserve sur le poste et les organise en tickets, interventions, préparation terrain et pilotage. Aucun accès API ni accès direct à la base de GLPI n'est nécessaire.

## Composants

- **Hôte Windows.** Une fenêtre C# .NET 10 (WinForms) qui affiche l'interface dans WebView2. Le pack embarque le runtime .NET et un runtime WebView2 fixe : rien à installer en plus.
- **Moteur C#.** Collecte, bibliothèque, partage, calendrier, préparation terrain et pilotage. L'interface ne fait que l'appeler.
- **Bibliothèque SQLite.** Une base locale par poste, avec recherche plein texte.
- **Navigateur de collecte.** Microsoft Edge ou Google Chrome, utilisé pour la session GLPI et son SSO.
- **Partage facultatif.** Un dossier réseau ou un serveur PostgreSQL 17 pour les données communes de l'équipe.

## Éditions et packs

| Élément | Contenu |
|---|---|
| GLPI-Pilot.exe | Édition Standard, pour les agents. Elle refuse les commandes d'administration. |
| GLPI-Pilot-Admin.exe | Édition Admin : sauvegardes et remises à zéro SQLite, préparation d'une base PostgreSQL dédiée. |
| wad/ | Ressources de l'application, documentation, licences et outils de support (wad/support). |
| Pack PROD | Les deux EXE et wad/, sans outils de développement. |
| Pack PROD-LIGHT | Le pack PROD sans le runtime WebView2 embarqué ; il utilise le runtime WebView2 Evergreen déjà installé sur Windows et le signale s'il manque. Un poste allégé se met à jour avec des packs allégés. |
| Pack DEV | Les mêmes EXE et wad/, avec les sources et les tests dans wad/sources/. |

> [!ATTENTION] Retirez GLPI-Pilot-Admin.exe du dossier distribué aux agents. La mise à jour ne réintroduit pas ce lanceur.

## Données sur le poste

| Emplacement | Contenu |
|---|---|
| %LOCALAPPDATA%\GLPI-Pilot | Profil du navigateur, préférences, archives, notes et tournées. |
| %LOCALAPPDATA%\GLPI-Pilot\Library | Bibliothèque SQLite glpi-pilot-library.db, ou le dossier local choisi. |
| %LOCALAPPDATA%\GLPI-Pilot\Backups | Sauvegardes vérifiées des préférences avant une nouvelle version. |
| %LOCALAPPDATA%\GLPI-Pilot\SharedImports | Fichiers métier remplacés par un échange, sauvegardés avant application. |
| Logs (données locales) | Journaux, dont demarrage-dernier.log. |

Les données de travail ne dépendent pas du dossier de l'EXE : remplacer le pack ne les touche pas.

## Ce que GLPI Pilot ne fait pas

- Il n'écrit dans GLPI qu'au travers d'une action assistée confirmée (chapitre 12).
- Il ne gère pas de stock : ni disponibilité, ni entrée, ni sortie, ni réservation, ni commande.
- Il ne crée pas d'intervention automatiquement et n'applique pas les propositions de répartition à GLPI.
- Il ne télécharge pas le contenu des pièces jointes : seules leurs références et métadonnées sont conservées.
- Il n'envoie pas automatiquement de contenu à un modèle distant.
- Il ne fonctionne pas comme un service Windows : la collecte programmée et la synchronisation exigent que l'application soit ouverte.

# 4 Installation et premier démarrage

L'installation consiste à extraire le pack. Au premier démarrage, GLPI Pilot sauvegarde les préférences existantes et peut proposer la configuration fournie avec le pack.

## Installer

1. **Vérifier l'archive.** Comparez l'empreinte SHA-256 du ZIP à SHA256SUMS.txt de la même publication.
2. **Extraire.** Extrayez tout le ZIP dans un dossier local, par exemple un nouveau dossier par version. Ne mélangez pas les fichiers avec un ancien wad.
3. **Lancer.** Ouvrez GLPI-Pilot.exe (ou GLPI-Pilot-Admin.exe).
4. **Vérifier le navigateur.** Edge ou Chrome doit être présent pour la connexion au portail GLPI.

> [!NOTE] Les EXE ne sont pas signés. Les empreintes SHA-256 établissent l'intégrité du pack, pas l'identité d'un éditeur. Windows peut afficher un avertissement au premier lancement.

> [!NOTE] Depuis une version antérieure à 3.6, faites le premier passage par extraction dans un nouveau dossier ; le profil utilisateur est conservé.

## Premier démarrage

1. **Sauvegarde automatique.** Avant le premier démarrage réussi d'une nouvelle version, les fichiers de préférences connus sont copiés dans Backups et vérifiés par empreinte SHA-256.
2. **Configuration du pack.** Si wad/GLPI-Pilot.deployment.json est présent, un profil neuf l'applique avant le premier affichage, et le journal de démarrage note « La configuration fournie a été appliquée ». Un profil existant propose « Examiner la configuration du pack » avant tout remplacement (chapitre 5).
3. **Une seule fenêtre.** Un second lancement affiche « GLPI Pilot est déjà ouvert. Retrouvez sa fenêtre dans la barre des tâches ; son démarrage peut encore être en cours. »

![Premier démarrage d’un profil neuf dans le vrai EXE : la configuration du pack est déjà appliquée (thème clair, zoom 115 %), sans question.](fig:admin-premier-demarrage)

| Vous voyez | Que faire |
|---|---|
| Aucune fenêtre n'apparaît | Lancez wad/support/DIAGNOSTIC_DEMARRAGE.cmd, puis lisez Logs/demarrage-dernier.log (chapitre 15). |
| Windows bloque les fichiers extraits | wad/support/DEBLOQUER_TOUT.cmd débloque les fichiers du pack. |
| La sauvegarde atteint sa limite | Le journal l'explique : déplacez les anciennes archives de Backups. Elles ne sont jamais supprimées automatiquement. |

> [!NOTE] La sauvegarde est bornée à 16 Mio par fichier, 12 archives et 256 Mio. Le profil et les sessions du navigateur ne sont pas copiés.

# 5 Configuration complète d'équipe

Une configuration complète transporte les options publiques d'un poste vers les autres : sources, référentiel de sites, seuils, tableaux, préférences métier. Les données de tickets, les secrets et les préférences personnelles n'en font jamais partie.

## Préparer la configuration par défaut du pack

Quand : vous préparez le dossier PROD à diffuser à l'équipe.

1. **Configurer un poste de référence.** Réglez sources, sites, seuils, tableaux et options dans ce dossier PROD.
2. **Ouvrir la configuration.** Dans « Réglages », ouvrez « Configuration d’équipe », section « Configuration complète et déploiement ».
3. **Enregistrer comme défaut.** Cliquez sur « Définir comme configuration par défaut », puis « Enregistrer comme défaut ». Le fichier wad/GLPI-Pilot.deployment.json est écrit dans cette installation.
4. **Diffuser.** Copiez le dossier complet, wad compris, par votre canal interne.

> [!RESULTAT] « Configuration par défaut enregistrée » s'affiche ; l'emplacement d'enregistrement est indiqué.

> [!ATTENTION] Ce fichier et les catalogues de sites sont privés : ne les publiez pas avec les packs génériques.

## Examiner et appliquer une configuration

1. **Ouvrir.** « Examiner un fichier complet » pour un fichier reçu, ou « Examiner la configuration du pack ».
2. **Comparer.** Choisissez « Toutes les sections » ou une section, et « Uniquement les différences ». Chaque option montre « Avant » et « Après ».
3. **Décider.** « Garder mes réglages » conserve le profil. Pour remplacer, cochez « Je confirme le remplacement de toutes les options du profil par les valeurs affichées. », puis cliquez sur « Sauvegarder et remplacer ».

![Revue d’une configuration complète : chaque option comparée avant et après, par section.](fig:admin-configuration-revue)

> [!RESULTAT] « Configuration appliquée » s'affiche. Le profil précédent est sauvegardé avant le remplacement.

| Vous voyez | Que faire |
|---|---|
| « L’aperçu complet avant/après est indisponible. Aucun import ne peut être confirmé. » | Le fichier est incomplet ou invalide. Exportez-le de nouveau avec « Exporter la configuration complète ». |
| « Configuration non appliquée » | Rien n'a été modifié. Relisez le motif affiché. |

## Ce qui reste propre à chaque poste

- Mots de passe, profils de connexion GLPI et SSO, paramètres PostgreSQL.
- Identité, notes privées, rappels personnels, épingles, positions de lecture.
- Préférences d'affichage et organisation du menu (« Organiser le menu »).

## Thème de l'interface

Quatre icônes de la barre du haut choisissent le thème : « Thème clair », « Thème sombre », « Contraste élevé » et « Thème : suivre Windows ». L'icône allumée indique le choix enregistré ; le même choix existe dans « Réglages », carte « Confort et présentation ». Le contraste élevé affiche le texte en blanc sur fond noir, avec des accents cyan et jaune ; « Suivre Windows » l'applique aussi quand Windows utilise un thème de contraste. La case « Contrastes renforcés » reste disponible pour les thèmes clair et sombre.

Une configuration complète transmet le thème clair ou sombre et « Suivre Windows ». Le contraste élevé reste propre à chaque poste : il n'est ni exporté ni remplacé par une configuration.

# 6 Sources GLPI et collecte

La collecte lit les tickets autorisés dans la session GLPI de l'utilisateur, par son navigateur. Elle est différentielle : un passage ne relit que les contenus nouveaux, modifiés, incomplets ou trop anciens.

## Régler la source

1. **Ouvrir.** Dans « Données », la carte « GLPI en direct » affiche la recherche, le navigateur et la collecte.
2. **Renseigner.** « Adresse de la recherche GLPI » : l'adresse complète de la recherche, filtres compris. « Navigateur » : « Microsoft Edge » ou « Google Chrome ».
3. **Se connecter.** Cliquez sur « Se connecter ». Le navigateur dédié reste réduit et s'affiche si le SSO demande une action visible.
4. **Collecter.** Cliquez sur « Lancer la collecte ».

![Espace « Données » : GLPI en premier, avec la recherche, le navigateur et la collecte.](fig:admin-donnees-glpi)

## Collecter plusieurs recherches

« Collecter plusieurs recherches » enchaîne les recherches mémorisées dans « Mes recherches ». Un bilan récapitule les réussites, les résultats à vérifier et les échecs de chaque recherche. Les tickets de plusieurs recherches ne sont pas fusionnés.

![Collecte groupée : le bilan par recherche.](fig:admin-recherches-groupees)

## Collecte et reprise

Dans « Données », « Collecte et reprise » ouvre la fenêtre « Collecte des tickets ».

- « Mettre à jour » lit l'index des tickets autorisés de l'installation courante, puis les contenus à relire.
- « Pause » et « Arrêter » annulent les nouvelles lectures et conservent les contenus déjà validés ainsi que le journal de reprise. Ils suspendent aussi la programmation jusqu'à une reprise explicite.
- « Reprendre » réutilise le même journal, vérifie le contexte et revalide l'index. Une fiche déjà enregistrée mais non confirmée peut être confirmée par sa preuve exacte, sans nouveau téléchargement.
- « Reprendre les erreurs » relance uniquement les tickets en erreur.
- « Journal et fiches à reprendre » détaille les résultats ; le journal retient jusqu'à 20 bilans.

![« Collecte des tickets » dans le vrai EXE Admin : l’état, la commande principale et les sections repliées.](fig:admin-collecte-native)

## Ce qui déclenche une relecture

- Une date de modification absente, ambiguë ou sans fuseau interprétable.
- Un contenu incomplet : pagination inconnue, onglet non reconnu, limite de lignes, changement de contexte.
- Une copie plus ancienne que l'âge maximal de la bibliothèque, sept jours par défaut.

Une absence de l'index ne devient jamais une suppression : le ticket est indiqué absent, son contenu local est conservé. Un résultat partiel n'efface pas une ancienne copie complète.

## Rythme et programmation

Le rythme se règle dans « Rythme et programmation », puis « Enregistrer le rythme ».

| Réglage | Valeur initiale | Plage |
|---|---|---|
| Collecte automatique | Désactivée | Activée sur le seul poste collecteur |
| Intervalle entre passages | 120 minutes | 5 à 10 080 minutes |
| Délai entre navigations | 3 000 ms, une lecture à la fois | 1 000 à 60 000 ms |
| Lot de navigations | 25 | 1 à 500 |
| Pause entre lots | 30 secondes | 0 à 3 600 secondes |
| Budget par passage | 60 minutes | 1 à 1 440 minutes |
| Arrêt après erreurs consécutives | 3 | 1 à 10 |
| Plage horaire | Toute la journée | Fuseau explicite, plage possible au-delà de minuit |

> [!NOTE] La programmation ne se déclenche pas sur un poste hors ligne, verrouillé, en démonstration, sur un import ou sur un relevé historique. Une pause, un arrêt ou une erreur restent prioritaires.

## Versions de GLPI

GLPI Pilot lit les pages de recherche et les fiches natives de GLPI 10 et de GLPI 9.5. Si la liste des tickets n'est pas reconnue, la collecte s'arrête sans rien modifier ni dans GLPI ni dans la bibliothèque, et la fenêtre « Collecte des tickets » affiche « Liste GLPI non reconnue : » suivi de la raison. Pendant la lecture de l'index, elle affiche la page et le nombre de tickets déjà indexés.

## Un collecteur, plusieurs lecteurs

Désignez un seul poste collecteur par périmètre. La collecte des tickets relit le contenu de chaque ticket, onglet par onglet, au rythme réglé : sur un parc important, un passage complet prend des heures. Les techniciens ne la lancent pas : ils mettent à jour leur liste avec « Lancer la collecte » ou un ticket avec « Actualiser la fiche », deux lectures courtes, et reçoivent le contenu des tickets par le partage entre postes (chapitre 9). Dans un même espace partagé, un verrou exclut deux collecteurs simultanés pour une même installation GLPI. La perte du verrou bloque la navigation suivante : aucune collecte locale non coordonnée ne prend le relais.

> [!ATTENTION] Une défaillance de stockage arrête la collecte : GLPI Pilot ne continue pas à lire des données qu'il ne peut pas conserver.

| Vous voyez | Que faire |
|---|---|
| L'état reste interrompu après un arrêt inattendu | Décidez manuellement : « Reprendre » ou un nouveau passage. |
| « Réglages de collecte invalides. Aucun rythme illimité ou parallèle n’est autorisé. » | Une valeur sort de sa plage. Corrigez-la dans « Rythme et programmation ». |
| « Le fuseau de la plage de collecte n’est pas reconnu. » | Choisissez un fuseau Windows reconnu. |
| Le SSO attend une action | Le navigateur s'affiche : terminez la connexion, puis reprenez. |

# 7 Fichiers CSV/Excel, cache local et démonstration

Trois sources secondaires complètent la collecte GLPI : un extrait CSV ou Excel, le dernier relevé conservé et une démonstration isolée. Chacune affiche sa provenance et sa date.

## Fichier CSV ou Excel

1. **Importer.** Dans « Données », « Un extrait CSV / Excel », cliquez sur « Importer CSV / Excel ».
2. **Choisir la feuille.** Pour un classeur, choisissez la feuille et la ligne d'en-têtes ; un profil de classeur compatible peut être proposé.
3. **Travailler.** Le bandeau « Source CSV / Excel » rappelle la provenance. « Revenir à la source précédente » ou « Fermer cette source » en sortent.

![Provenance d’une source CSV / Excel : fichier, lignes, colonnes et lecture complète.](fig:admin-csv-provenance)

![Profil de classeur : la feuille, la ligne d’en-têtes et les colonnes reconnues.](fig:admin-profil-classeur)

> [!NOTE] Un fichier importé n'est pas certifié comme extraction exhaustive du portail. Un fichier corrompu ou ambigu n'entraîne aucun repli silencieux.

## Dernier relevé conservé

Le dernier relevé réussi reste consultable hors connexion, avec sa date. Il ne dit rien de l'état de la session SSO.

![Relevé conservé en cache local, avec sa date et sa provenance.](fig:admin-cache-local)

## Démonstration pour la formation

La démonstration s'ouvre dans « Réglages », carte « Démonstration » ; le choix « Démonstration » de la source affichée y conduit aussi. « Ouvrir la démonstration » charge 60 tickets fictifs dans un espace isolé : toutes les fonctions sont utilisables, sans toucher au profil réel. Pendant la démonstration, la même carte propose « Quitter la démonstration », qui restaure l'espace habituel comme le bandeau « Quitter la simulation », et « Réinitialiser les données fictives ». Chaque action demande une confirmation. Les réglages modifiés pendant la démonstration ne sont pas conservés.

![Espace « Données » pendant la démonstration : la source simulée est clairement signalée.](fig:admin-donnees-demonstration)

> [!ATTENTION] La démonstration et les imports ne deviennent jamais des sources d'écriture vers GLPI.

# 8 Bibliothèque de tickets

La bibliothèque conserve sur chaque poste les tickets recueillis, leurs contenus consultables, les interventions et les contributions de l'équipe. Elle sert à la recherche, aux précédents résolus et au travail hors connexion.

## Réglages de lecture

| Réglage | Effet |
|---|---|
| « Dossier de la bibliothèque » | Emplacement local de la base. Le changer copie la base vers un nouveau fichier ; l'ancienne est conservée. |
| « Fuseau des dates GLPI » | Fuseau d'affichage de GLPI. Sans ce choix, une date sans décalage ne permet pas de réutiliser un contenu sans le relire. |
| Format des dates GLPI | Ordre jour/mois/année. Une date comme 05/06 reste ambiguë tant qu'il n'est pas choisi. |
| Recontrôle des contenus | Âge maximal d'une copie : sept jours par défaut. Une fiche lue par une version antérieure n'est relue que si la date de dernière modification affichée par GLPI change. |

> [!ATTENTION] Gardez le dossier de la bibliothèque sur un disque local. Un chemin UNC, un lecteur réseau ou une liaison de fichiers est refusé pour la base SQLite.

## Ce qui est conservé

- Les onglets de consultation reconnus et leurs paginations : description, champs, suivis, tâches, solutions, validations, éléments, documents, historique, liens. Les onglets Statistiques, Coûts, Projets, Tâches de projet, Base de connaissances, Problèmes et Changements ne sont pas ouverts, ni la vue « Tous » qui répète les autres.
- Les pièces jointes en références et métadonnées, sans leur contenu.
- Jamais les boutons d'ajout, de modification ou de suppression de GLPI : la lecture ne déclenche aucune action métier.

## Recherche et précédents

- La recherche locale porte sur les titres et les contenus conservés ; Ctrl K y donne accès depuis tous les écrans.
- Les précédents résolus d'un ticket restent dans la même installation GLPI ; le moteur examine au plus 100 candidats et propose huit précédents au plus, avec leurs sources.
- « Exporter le corpus RAG » écrit un fichier JSONL avec les documents et leurs passages sourcés. Aucun index vectoriel ni envoi à un modèle distant.

![Contributions de l’équipe dans la bibliothèque.](fig:admin-equipe)

## Transférer une bibliothèque préparée

Quand : un nouveau poste doit démarrer avec une bibliothèque déjà collectée.

1. **Exporter.** Dans « Collecte des tickets », section « Transférer une bibliothèque préparée », cliquez sur « Exporter la bibliothèque préparée ». Un paquet .glpilibrary.zip est créé.
2. **Transmettre.** Par un canal interne protégé, à des destinataires habilités au même périmètre.
3. **Importer.** Sur le poste destinataire, sélectionnez la même installation GLPI, puis « Importer une bibliothèque préparée ». L'aperçu vérifie l'identité, le format et les empreintes avant la confirmation.

> [!RESULTAT] L'import est transactionnel et idempotent ; les versions concurrentes sont conservées. Aucune collecte n'est lancée à l'import.

> [!ATTENTION] Le paquet n'est pas chiffré et peut contenir des suivis privés. Ne le joignez jamais à un ticket, un dépôt, une publication ou un pack public. Il n'exporte ni cookies, ni mots de passe, ni identité de synchronisation, ni notes personnelles.

# 9 Partage entre postes

Le partage est facultatif. Il échange les données communes de l'équipe (planning, tournées, sites, règles, tableaux, contributions) par un dossier réseau ou un serveur PostgreSQL. Les bases SQLite restent locales à chaque poste.

## Choisir le mode

Dans « Réglages », « Partage entre postes » ouvre « Configurer le partage entre postes ».

| Mode | Usage |
|---|---|
| « Sur ce poste uniquement » | Aucun échange. Toutes les fonctions restent disponibles sur le poste. |
| « Dossier réseau partagé » | Un dossier commun reçoit des transactions fermées ; aucune base SQLite n'y est ouverte. |
| « Serveur PostgreSQL » | Une base centrale dédiée à l'équipe. |

![« Configurer le partage entre postes » : la copie locale à gauche, le mode de partage et la synchronisation à droite.](fig:admin-partage-connexion)

## Dossier réseau partagé

1. **Préparer le dossier.** Un dossier existant, par exemple \\serveur\equipe\GLPI-Pilot, avec lecture et écriture pour les utilisateurs concernés.
2. **Configurer chaque poste.** Choisissez « Dossier réseau partagé » et le même dossier ; préférez le chemin UNC à une lettre de lecteur.
3. **Tester.** « Tester la connexion » vérifie l'ouverture et l'écriture et prépare le sous-dossier GLPI-Pilot-Shared.
4. **Enregistrer.** Cliquez sur « Enregistrer le partage », puis « Synchroniser ».

![Partage par dossier : le registre commun et l’état de synchronisation.](fig:admin-partage-dossier)

> [!ATTENTION] Ne modifiez ni ne déplacez un fichier interne du registre. Un contenu incomplet bloque l'échange en conservant la file locale.

## Serveur PostgreSQL

Prérequis : un serveur PostgreSQL 17 maintenu, une base dédiée, un nom DNS conforme à son certificat TLS, un rôle applicatif limité à cette base et autorisé à créer ses tables dans son schéma, des sauvegardes, et un pare-feu limité aux postes de l'équipe.

1. **Restreindre l'accès.** Dans pg_hba.conf, une règle hostssl pour cette base, ce rôle et le réseau réel de l'équipe, avec scram-sha-256, placée avant toute règle plus large.
2. **Préparer la base.** Depuis l'édition Admin, « Préparer la base distante » crée la structure.
3. **Configurer chaque poste.** « Serveur PostgreSQL », même hôte, port, base et utilisateur (par défaut : port 5432, base glpi_pilot). Gardez « TLS · certificat et nom vérifiés », le mode par défaut.
4. **Tester et enregistrer.** « Tester la connexion », puis « Enregistrer le partage ».

> [!NOTE] Le mot de passe peut rester en mémoire pour la session. « Mémoriser ce mot de passe sur ce poste » le protège avec le compte Windows. Il n'est jamais renvoyé à l'interface ni inclus dans une configuration partagée.

> [!ATTENTION] Les droits PostgreSQL et du dossier partagé ne reproduisent pas ceux de GLPI : toute personne autorisée lit les données communes. Réservez l'espace à une équipe autorisée à tout consulter.

## Synchronisation

| Réglage | Valeur initiale | Effet |
|---|---|---|
| « Synchroniser automatiquement » | Activée | Échanges périodiques pendant que l'application est ouverte, avec un premier passage au démarrage. |
| « Intervalle de synchronisation (minutes) » | 5 | De 1 à 1 440 minutes. |
| « Synchroniser » | Manuel | Échange immédiat, même sans automatisme. |

En cas de coupure, les modifications sont préparées dans la file locale et reprises au retour du réseau. Un échange partiel n'est jamais présenté comme une réussite complète.

## Données communes et conflits

Le bloc « Données communes » indique le nombre de documents communs conservés et les propositions concurrentes. Quand deux postes modifient la même donnée, « Comparer et choisir » ouvre « Comparer les données communes » : la version actuelle et la proposition reçue, champ par champ.

![« Comparer les données communes » : version actuelle et proposition reçue, différence par différence.](fig:admin-partage-conflit)

![« Comparer les versions de la contribution » en fenêtre étroite : les deux versions d’une contribution d’équipe, champ par champ.](fig:admin-comparaison-versions)

1. **Comparer.** Filtrez avec « Filtrer un champ ou une valeur ».
2. **Choisir.** « Conserver la version actuelle » ou « Appliquer la proposition reçue ».
3. **Garder une trace.** « Exporter les versions complètes » enregistre les deux versions avant décision.

> [!NOTE] Les fichiers métier remplacés sont sauvegardés dans SharedImports avant application.

## Sauvegardes du partage

- Dossier partagé : arrêtez les échanges et sauvegardez GLPI-Pilot-Shared en entier.
- PostgreSQL : pg_dump en format d'archive, avec un test de restauration sur une base séparée.
- Base locale : fermez GLPI Pilot, puis copiez le dossier de bibliothèque complet, fichiers auxiliaires compris.

# 10 Interventions, tournées et préparation terrain

Le planning commun, la carte et les tournées se trouvent dans « Interventions ». La préparation terrain prévoit le matériel et imprime les fiches, sans gérer de stock.

## Planning et carte

- « Planning commun » montre les interventions de l'équipe ; « Mes interventions » celles de l'identité choisie.
- « À planifier » liste les tickets actifs sans intervention prévue.
- La carte place les tickets selon le référentiel de sites. « Gérer le référentiel » importe et règle les sites, communes et alias.
- « Corriger une localisation » affecte le bon site à un ou plusieurs tickets ; la correction est conservée et partagée avec les données communes. Elle ne modifie pas le ticket dans GLPI.

## Référentiel de sites et carte

La carte place chaque ticket sur un site du référentiel, reconnu dans ses champs par les noms, alias et communes enregistrés. Un ticket rattaché seulement à sa commune compte dans « Localisation approximative » ; un ticket sans lieu reconnu compte dans « Sans coordonnées ».

- « Gérer le référentiel », depuis la carte, ou « Réglages », « Sites et communes » (« Importer le référentiel et régler la détection ») : sites, communes, alias et coordonnées.
- « Explorer les sites » ouvre « Sites du périmètre » : stock actif, « Préconisation » (« À suivre », « Intervention à planifier », « Aucun ticket »), position, et « Modifier le site ». Les sites sans ticket restent listés.
- « Options du terrain » : « Panier et ordre des communes », « Services de route et de météo » et « Effectifs des sites · facultatif ». Les services externes de route et de météo restent inactifs tant qu'aucun fournisseur n'est configuré et autorisé.
- Le référentiel fait partie de la configuration d'équipe : « Définir comme configuration par défaut » y inclut les communes, sites, bâtiments et alias enregistrés (chapitre 5).

![Correction d’un site : le ticket mal localisé et le site retenu.](fig:admin-correction-site)

> [!NOTE] Les affectations et visites prévues ne sont pas une position GPS des personnes.

## Préparation terrain

- Une préparation porte sur un ticket (« Préparer un ticket ») ou une visite (« Préparer une visite »).
- Chaque ligne a une quantité prévue et une quantité préparée ; les états vont de « À renseigner » à « Prête », avec « Sans matériel nécessaire ».
- Le « Catalogue » des références est personnalisable par l'équipe.
- Les fiches s'impriment depuis « Aperçu et impression » ou s'exportent avec « Exporter les fiches ».

![Fiche terrain produite par le vrai EXE : l’intervention et son matériel.](fig:admin-fiche-terrain-native)

> [!ATTENTION] Préparer une fiche ne change ni l'affectation, ni le statut, ni la description du ticket GLPI.

Dans un espace partagé, deux modifications concurrentes d'une préparation sont conservées et proposées à la comparaison, comme les autres données communes.

# 11 Pilotage, seuils et présentation

Le Pilotage calcule la météo du service à partir du relevé affiché. Les seuils, la vue et les présentations sont des réglages d'équipe transférables.

## Seuils

Les seuils initiaux sont 100/200 pour le stock, 50/100 pour les incidents ou demandes et 10/20 par équipe ou catégorie. La vigilance et la tension commencent strictement au-dessus de leur seuil.

1. **Ouvrir.** Sur une carte météo, cliquez sur « Seuils pour cette sélection ».
2. **Choisir l'unité.** Valeur absolue, ou pourcentage avec une « Base du pourcentage » explicite.
3. **Choisir la portée.** « Portée des seuils » : ce widget pour toutes les sélections, ou la sélection courante.
4. **Enregistrer.** « Seuil de vigilance », « Seuil de tension », puis « Enregistrer les seuils ».

![« Seuils pour cette sélection » : unité, portée, base du pourcentage et seuils.](fig:admin-seuils)

> [!NOTE] « Réglages », « Seuils, équipes et règles météo » donne les valeurs de départ et les correspondances d'équipes.

## Vue et présentation

- « Personnaliser la vue » place et dimensionne les widgets ; les positions, tailles, filtres et seuils sont enregistrés.
- « Présenter » affiche le tableau en plein écran ; l'éditeur de présentation compose les diapositives.

![Vue de Pilotage en personnalisation : positions et tailles des widgets.](fig:admin-vue-personnalisee)

![Éditeur de présentation : les diapositives et leurs widgets.](fig:admin-presentation)

# 12 Actions GLPI assistées et sécurité

GLPI Pilot n'écrit dans GLPI que par une action assistée : un formulaire GLPI reconnu, un aperçu, puis une confirmation unique, avec les droits de la session de l'utilisateur.

## Règles

- « Ajouter un suivi », « Ajouter une tâche » et « Affecter » passent par des adaptateurs de formulaire reconnus.
- L'aperçu affiche l'avant, l'après et les autres champs transmis. La confirmation autorise un seul envoi.
- Échap, une saisie modifiée ou une expiration invalident l'aperçu.
- Un résultat indéterminé reste affiché et impose une vérification manuelle ; il n'est jamais renvoyé automatiquement.
- Statut, priorité et résolution ouvrent la fiche GLPI native : aucun adaptateur d'écriture automatique de ces champs.
- Ouvrir le centre d'actions ne déclenche ni collecte, ni inspection de formulaire, ni écriture.
- Les imports, archives et la démonstration ne sont jamais des sources d'écriture.

## Données de tickets

- Le contenu des tickets est une donnée, jamais une instruction : aucun texte recueilli ne déclenche d'action.
- Le journal général ne contient pas de contenu de cellule ; un diagnostic des écarts, qui en contient, s'exporte explicitement.
- Aucun service externe (itinéraire, météo, génération) n'est actif sans fournisseur configuré et autorisé.

# 13 Administration locale

L'édition Admin ajoute la maintenance de la base locale : aperçu avant effacement, sauvegarde vérifiée, préparation d'une base PostgreSQL.

![Édition Admin : « Administration locale » et ses deux réinitialisations, et « Préparer la base distante » pour PostgreSQL.](fig:admin-administration)

- « Réinitialiser les tickets locaux » et « Réinitialiser toute la base locale » affichent un aperçu, puis créent une sauvegarde vérifiée avant l'effacement. Les données distantes et les fichiers personnels sont conservés.
- « Préparer la base distante » crée la structure PostgreSQL dédiée (chapitre 9).

> [!ATTENTION] L'édition Standard refuse ces commandes. Ne distribuez pas l'édition Admin aux agents.

# 14 Mise à jour et retour arrière

La mise à jour remplace le pack sans toucher aux données de travail. L'assistant vérifie le pack, garde une sauvegarde complète et restaure l'ancienne version si le démarrage échoue.

## Avec l'assistant

1. **Ouvrir.** « Réglages », « Mettre à jour » ouvre « Mise à jour et retour arrière ».
2. **Choisir le pack.** « Vérifier GitHub » consulte la dernière publication ; ou « Choisir le ZIP… » pour un pack local, ou un dossier de diffusion réseau contenant le ZIP PROD et SHA256SUMS.txt.
3. **Vérifier.** « Télécharger et vérifier » ou « Vérifier le ZIP local » contrôle l'archive, son commit et chaque fichier. Renseignez si besoin le « SHA-256 attendu de l’archive » et le « Commit attendu ».
4. **Installer.** « Fermer GLPI Pilot et installer la version vérifiée ». La version en cours reste inchangée jusqu'à ce clic.

> [!RESULTAT] « Tous les fichiers du pack ont été contrôlés. L’installation reste à votre initiative. » précède l'installation. Après un échec, l'ancien pack est restauré ; si un fichier reste verrouillé, la sauvegarde est conservée et la récupération est signalée.

![« Mise à jour et retour arrière » : la publication officielle sur GitHub, ou un dossier de diffusion réseau.](fig:admin-mise-a-jour)

## Retour arrière

« Préparer un retour arrière » puis « Vérifier la sauvegarde » prépare le retour à la version conservée ; « Installer la version conservée » l'applique.

## Confiance

« Exiger une signature Authenticode de l’éditeur approuvé » et « SHA-256 du certificat d’éditeur approuvé » imposent une signature. Les packs actuels ne sont pas signés : n'activez cette exigence qu'avec un certificat fourni par une source de confiance.

## À faire avant chaque mise à jour

- Sauvegardez les bases locales et les réglages.
- Alignez sur la même version tous les postes qui collectent ou échangent des paquets dans un même espace partagé.
- Ne mélangez pas les fichiers avec un ancien wad en cas de remplacement manuel : extrayez dans un nouveau dossier.

> [!NOTE] Aucun contrôle de publication n'est lancé automatiquement au démarrage.

# 15 Journaux, diagnostic et limites connues

## Journal et diagnostic

- « Journal » (barre du bas) et « Réglages », « Journal et diagnostic » donnent les résultats des opérations et un rapport partageable.
- wad/support/DIAGNOSTIC_DEMARRAGE.cmd produit un diagnostic de démarrage ; wad/support/LIRE-LE-DIAGNOSTIC.txt l'explique.
- Logs/demarrage-dernier.log conserve les étapes du dernier démarrage, les types d'erreur et les codes, sans cellule, cookie ni jeton.
- Un incident du moteur d'affichage laisse un diagnostic visible dans la fenêtre plutôt qu'un écran vide.

| Vous voyez | Que faire |
|---|---|
| Aucune fenêtre au lancement | DIAGNOSTIC_DEMARRAGE.cmd, puis demarrage-dernier.log. |
| Un ticket reste à relire | « Journal et fiches à reprendre », puis « Reprendre les erreurs ». |
| Un échange de partage reste en attente | Vérifiez l'accès au dossier ou à la base, puis « Synchroniser ». |
| Un résultat d'action GLPI incertain | Ouvrez le ticket dans GLPI ; ne relancez pas l'action. |

## Limites connues

- Les EXE ne sont pas signés.
- Le portail GLPI et son SSO, les droits SMB et les imprimantes de l'organisation ne sont pas qualifiés par les tests automatiques, qui utilisent des profils isolés, des données fictives et des services de test.
- La collecte programmée et la synchronisation exigent que GLPI Pilot reste ouvert ; ce n'est pas un service Windows.
- Fermer le panneau de collecte ou changer d'écran pendant sa requête peut interrompre la collecte ; la reprise reste possible.
- Les dates sont calendaires : un stock ou une estimation ne mesure pas la productivité d'un technicien.

## Captures de ce guide

Les captures viennent de la vraie interface de GLPI Pilot 3.13.1 et de son moteur C#, avec des données fictives ou de démonstration ; certaines ont été prises dans les EXE Windows réels. Aucune donnée réelle n'y figure.

# 16 Mots à connaître

| Terme | En clair |
|---|---|
| Édition Standard | L'application des agents ; elle refuse les commandes d'administration. |
| Édition Admin | L'application de l'administrateur : maintenance de la base locale et préparation PostgreSQL. |
| Pack PROD / DEV | L'archive à distribuer ; le DEV ajoute les sources et les tests. |
| wad | Le dossier des ressources de l'application, à garder à côté des EXE. |
| Relevé | Le tableau de tickets chargé à un moment donné, qui alimente les chiffres. |
| Bibliothèque | La base SQLite locale des tickets recueillis et de leurs contenus. |
| Collecte différentielle | Un passage qui ne relit que les contenus nouveaux, modifiés ou douteux. |
| Reprise | La suite d'une collecte interrompue à partir de son journal. |
| Collecteur désigné | Le seul poste autorisé à collecter un périmètre dans un espace partagé. |
| Données communes | Planning, tournées, sites, règles et tableaux échangés entre postes. |
| Proposition concurrente | Une version d'une donnée commune modifiée ailleurs, à comparer avant de choisir. |
| Configuration complète | Le fichier qui transporte les options publiques d'un poste vers les autres. |
| .glpilibrary.zip | Le paquet non chiffré d'une bibliothèque préparée, à diffuser par canal protégé. |
| Action assistée | Une action GLPI préparée, relue puis confirmée une seule fois. |
| Adaptateur | La reconnaissance d'un formulaire GLPI qui permet une action assistée. |
| Démonstration | Un espace isolé de tickets fictifs pour la formation, sans effet sur le profil réel. |
| Météo | L'état d'un volume de tickets par rapport à ses seuils de vigilance et de tension. |
| Préparation terrain | Le matériel prévu et préparé pour une intervention, sans gestion de stock. |
| Référentiel de sites | Les sites, communes et alias connus, qui servent à placer les tickets sur la carte. |
