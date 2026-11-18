# GLPI Pilot 4.0.7

## Préparation des colonnes selon l’activité GLPI

La préparation relit les éléments natifs qui ont été remplacés au lieu d’attendre pendant dix secondes leurs anciens identifiants. Le cadre personnel et le tableau de tickets restent vérifiés dans leur contexte d’origine.

Les requêtes de la fenêtre personnelle sont suivies depuis leur émission jusqu’à la fin de leur réponse. Une requête GLPI encore active ne devient pas une erreur parce que le délai de liste est dépassé. L’interface indique l’étape et permet d’annuler. Le délai configuré sert à reconnaître une absence de progression sans requête en cours ; les requêtes d’autres fenêtres, les ressources et les recherches sans rapport n’entretiennent pas cette attente.

Les refus HTTP, les interruptions réseau, les changements de contexte et les ajouts non confirmés produisent des messages distincts. Une commande déjà envoyée n’est pas répétée. Après une interruption avec une opération native encore en cours, l’onglet concerné est isolé avant une nouvelle collecte. Le tableau, les critères et les ajouts sont relus avant l’export.

## Composants du poste

Les champs du fichier ZIP et de son empreinte SHA-256 sont initialement vides et portent leurs libellés. Le bouton d’installation devient disponible lorsqu’un chemin est renseigné. L’empreinte SHA-256 reste facultative ; une empreinte saisie est contrôlée avant l’envoi. Le formulaire n’envoie plus la chaîne `[object Object]` comme chemin de fichier.

Le diagnostic distingue le pilote Playwright fourni dans le pack des composants supplémentaires. Le runtime WebView2 fixe et le navigateur Firefox pilotable restent des options selon l’équipement du poste ; leur absence n’est pas présentée comme l’absence du pilote livré avec GLPI Pilot.

## Parcours Données et apparence

L’ajout et la gestion du référentiel de lieux depuis Données restent dans cet écran. La configuration de l’adresse GLPI peut se poursuivre sans navigation automatique vers la carte. Le bouton « Se connecter » séparé a été retiré de Données : lancer la collecte ouvre GLPI et prend en charge l’attente d’authentification.

Les nouveaux profils utilisent « Suivre Windows » comme thème initial. Les préférences d’apparence explicitement enregistrées sont conservées.

## Vérification et mise à jour

Les régressions couvrent les remplacements de cadres et de tableaux, les réponses lentes, les erreurs natives, l’activité étrangère et l’annulation. Les parcours WebView vérifient également les composants, le référentiel de lieux et l’apparence. La qualification Windows complète et l’inspection de l’archive originale précèdent la publication du même ZIP. Les scénarios utilisent des données fictives ; ils ne constituent pas une validation du portail privé de l’organisation.

Fermer GLPI Pilot puis remplacer les deux exécutables et le dossier `wad/` par ceux du pack 4.0.7. Conserver les données et réglages existants.
