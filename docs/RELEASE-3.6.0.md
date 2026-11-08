# GLPI Pilot 3.6.0 — Sources de données, administration et diffusion réseau

Un export CSV ou Excel devient une source de travail réelle, utilisable en complément ou en secours de la collecte GLPI par navigateur. La démonstration possède désormais une base fictive préremplie indépendante.

## Travailler à partir d’un fichier

**Importer CSV / Excel** ouvre les lignes du fichier dans l’espace habituel. Le sélecteur Excel conserve le choix de feuille, des en-têtes et du profil de pilotage facultatif. Les valeurs enregistrées sont lues sans exécuter de macro, formule ou liaison externe.

La liste, les filtres, les exports, le pilotage, la carte, les tournées et les interventions restent utilisables. Ouvrir un ticket affiche les valeurs de sa ligne. Les fiches détaillées, suivis, tâches, solutions, documents, actions GLPI et enrichissements nécessitent la collecte par navigateur : leur indisponibilité est expliquée et vérifiée aussi dans le moteur. Aucun contenu de ticket n’est inventé depuis un export.

Le contexte est identifié par **Source CSV / Excel**, son nom et sa qualité de lecture. Les calculs concernent le fichier, sans prétendre certifier la couverture GLPI. La récolte automatique reste suspendue tant que cette source est active.

Le planning et les tournées sont enregistrés dans l’espace habituel. Les références d’un même fichier, de sa feuille et de ses en-têtes restent stables lors d’un nouvel import ; un autre fichier possède une identité distincte. Dans **Données**, **Revenir à la source précédente** restaure l’observation précédant les imports successifs. Sans observation précédente, **Fermer cette source** libère le relevé.

Annuler la sélection ou rencontrer un fichier modifié avant validation conserve la source courante. Les écritures déjà engagées se terminent dans leur contexte avant la bascule. Importer depuis la démonstration active une source réelle avec les réglages habituels.

## Démonstration préremplie

**Ouvrir la démonstration** prépare une base locale temporaire avec :

- 60 tickets entièrement fictifs, leurs descriptions, suivis, tâches et solutions pour les tickets terminés ; six types d’incidents et demandes, six états et plusieurs priorités ;
- six techniciens, quatre sites et leurs horaires de travail ;
- six interventions dont deux terminées, plusieurs à venir et une série hebdomadaire de quatre occurrences, plus une formation ;
- quatre tournées, dont deux effectuées, une prête et une en préparation ;
- huit dossiers personnels avec notes et checklists, trois épingles, cinq éléments dans la file et deux rappels.

Les dates suivent la période d’ouverture de la démo. La bibliothèque contient les 60 fiches dès le départ. Les actions, collectes, échanges et mises à jour de démonstration utilisent les adaptateurs locaux existants. Quitter restaure l’espace habituel ; aucun essai n’y est recopié. Un nouvel import réel quitte la démonstration.

## Mises à jour réseau et patchs de sites

Une source **Dossier réseau partagé** s’ajoute à GitHub et au ZIP local. Elle recherche le dernier PROD Windows x64 déclaré dans `SHA256SUMS.txt`, copie et contrôle le pack localement, puis réutilise l’installation avec sauvegarde et retour arrière. Aucun programme n’est exécuté sur le partage.

Le référentiel se diffuse séparément avec `GLPI-Pilot-Sites-v1.csv` et `GLPI-Pilot-Sites.sha256`. L’Admin exporte ce patch ; Standard et Admin disposent du comparatif paginé avant/après. L’application conserve les Id, les éléments absents et les corrections individuelles. Elle refuse les aperçus périmés et sauvegarde le référentiel avant confirmation d’une modification. Le format et la restauration sont documentés dans [Diffusion et administration](Diffusion-et-administration.txt).

## Deux lanceurs, un pack commun

PROD et DEV contiennent `GLPI-Pilot.exe`, `GLPI-Pilot-Admin.exe` et `wad/`. Les deux binaires sont identiques entre éditions. Les pouvoirs d’administration sont fixés à la compilation et contrôlés dans le moteur. Retirer l’Admin pour diffuser aux agents ; la mise à jour préserve ce retrait. Un Admin présent est remplacé et restauré avec le reste de l’installation.

L’Admin peut remettre à zéro les tickets SQLite ou toute la base locale après aperçu et sauvegarde cohérente. Les fichiers personnels restent conservés ; la synchronisation automatique est suspendue. Il peut également préparer une base PostgreSQL absente sur un serveur existant avec des identifiants administrateur temporaires. Le compte technique utilisé par les agents conserve des droits limités ; la synchronisation normale ne crée plus de schéma.

## Qualification

Les contrôles locaux et la CI couvrent les sources fichier, la démo, le partage de diffusion, les patchs et les droits des lanceurs. La qualification finale porte sur les deux archives exactes produites par le workflow Windows et PostgreSQL du commit livré. Les partages SMB et le SSO de l’entreprise restent des recettes dans l’environnement réel. Les résultats effectivement obtenus sont indiqués dans le rapport de livraison.

**Premier passage depuis une version antérieure à 3.6 :** l’ancien vérificateur refuse la nouvelle racine à deux EXE. Fermer l’ancienne version et extraire ce pack complet dans un nouveau dossier local ; le profil reste conservé. Les mises à jour suivantes utilisent le nouvel assistant.
