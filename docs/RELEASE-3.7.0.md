# GLPI Pilot 3.7.0 — Réunions et données hors connexion

## Choisir ce qui sera présenté

Pilotage propose une sélection des indicateurs, graphiques et tableaux présents dans la composition. Cocher les éléments utiles, régler leur ordre, puis ouvrir la présentation. Une sélection vide ne peut pas démarrer. Les filtres et la provenance restent visibles ; les chiffres décrivent le relevé choisi, avec sa date. Le mode réunion retire les commandes d’édition et se quitte avec Échap. La composition enregistrée n’est pas modifiée.

Les exports de présentation reprennent la sélection et son ordre. Les tableaux sont complets au-delà de la page affichée. Les éléments non sélectionnés ne sont pas ajoutés comme un bilan caché. L’export HTML est autonome. Les exports de listes de tickets conservent leurs propres filtres et colonnes.

## Retrouver le dernier relevé sans connexion

Chaque import réel CSV/Excel et collecte réussie mémorise localement les lignes, leur provenance, l’interprétation et les éléments de localisation utiles. Le démarrage retrouve cette copie sans dépendre du fichier original ni de GLPI. Accueil et Données indiquent explicitement le cache, sa date et ses limites.

L’enregistrement remplace le cache de manière atomique et conserve une génération de secours vérifiée. Une importation échouée ne détruit pas le relevé précédent. Une copie endommagée peut être remplacée par la génération précédente, avec avertissement visible. La démonstration ne lit ni ne remplace les données réelles du cache.

Cette consultation ne lance pas automatiquement de collecte. Les opérations de détail qui demandent le navigateur restent indisponibles jusqu’à une nouvelle collecte explicite. Un fichier ne contient toujours que sa liste exportée : aucun détail absent n’est inventé. Données permet d’effacer la copie hors ligne après confirmation, sans supprimer fichiers originaux, bibliothèque, notes, calendrier ni archives.

## Animations Domenia

Les scènes météo conservent leurs animations dans la présentation, même si la carte d’origine était hors écran. Leurs dégradés et masques SVG sont propres à chaque copie. Les chiffres et les boutons gardent leur position. Lorsque Windows suspend les effets, le réglage explicite permet d’activer les animations partout et de mémoriser ce choix ; le mode Réduites reste disponible et respecté. Les documents PDF restent fixes pour l’impression.

## Distribution et validation

PROD et DEV conservent les deux lanceurs Standard et Admin, avec les ressources communes dans `wad`. La mise à jour respecte l’absence du lanceur Admin. Les sources et tests sont inclus dans DEV. Le workflow de chaque commit qualifie les moteurs, l’interface, PostgreSQL, la reconstruction DEV et le démarrage/update/retour arrière réels sous Windows avant publication.

Depuis une version antérieure à 3.6, extraire le nouveau pack dans un nouveau dossier local pour le premier passage au contrat à deux exécutables. Le profil utilisateur reste conservé. Voir [Diffusion et administration](Diffusion-et-administration.txt).
