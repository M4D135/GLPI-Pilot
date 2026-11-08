# GLPI Pilot 3.12.0 · Préparation terrain

## Matériel par ticket, intervention et tournée

La préparation terrain relie les besoins matériels aux tickets incidents/demandes, aux interventions manuelles et aux visites de tournée. Elle fonctionne sans tournée et sans gestion de stock : pas de solde disponible, mouvement d’inventaire, achat ou réservation.

**Interventions → Préparer le matériel** permet de choisir une source, créer la liste à emporter, suivre les quantités prévues et préparées, renseigner le préparateur et les consignes, ou indiquer explicitement qu’aucun matériel n’est nécessaire. Les tickets et les cartes de visite proposent des raccourcis contextuels.

Le catalogue de catégories et références est personnalisable. La création d’une référence est possible depuis la préparation. L’archivage empêche les nouveaux usages sans réécrire le nom, la catégorie, la référence ou l’unité enregistrés sur les anciennes fiches.

## Pilotage, fraîcheur et partage

**Pilotage → Matériel prévu** ajoute les filtres de site, technicien, équipe, tournée, période et état. Les quantités sont regroupées par référence et unité ; chaque préparation reste comptée une seule fois. Les besoins propres aux tickets et les besoins complémentaires des visites restent distincts, avec un avertissement en cas de coexistence.

Les lectures rapprochent les fiches du planning et du relevé connus du poste. Un déplacement conserve le matériel et la clé de visite, y compris pour une série répétée scindée. Une annulation ou une clôture reconnue retire les besoins du départ. Une source absente ou en conflit est signalée et exclue des totaux vérifiés, sans inventer une suppression de ticket.

La sauvegarde est versionnée dans la base SQLite existante et raccordée au partage facultatif par dossier ou PostgreSQL. Les modifications concurrentes se comparent et se résolvent explicitement. Les versions antérieures restent archivées ; les résolutions sont propagées sans rouvrir un conflit déjà examiné lors d’un ancien échange rejoué.

## Fiches terrain et kits

Imprimer une sélection de fiches ou une tournée entière, avec tous ses arrêts et les préparations enregistrées des tickets liés. Les visites sans matériel renseigné restent visibles et ne créent pas de faux enregistrements. Les aperçus comprennent la liste de départ, les détails disponibles, le matériel, les consignes et les espaces de compte rendu. L’export HTML utilise le sélecteur natif de Windows.

Une archive dont la source n’est plus vérifiable exige un choix explicite ; elle est identifiée comme telle et ses quantités ne gonflent pas la liste de départ. Les sélections périmées, les conflits et les dépassements de capacité sont refusés sans impression partielle silencieuse.

Des kits peuvent être suggérés à partir des préparations locales récentes. Le motif et les quantités sont affichés, l’ajout doit être confirmé, et les quantités préparées repartent à zéro. Aucun modèle distant n’est contacté et aucune compatibilité n’est supposée.

## Déploiement

**Mettre à jour tous les participants d’un espace partagé vers 3.12.0 ou une version ultérieure avant de publier ces nouvelles préparations.** Les clients 3.11.x ne connaissent pas les nouveaux types de documents. Sauvegarder les bases et conserver les réglages privés avant le déploiement ; ne pas mélanger les versions dans cet espace.

Les packs PROD et DEV contiennent `GLPI-Pilot.exe`, `GLPI-Pilot-Admin.exe` et `wad` à la racine. Le pack DEV comprend les sources et tests dépliés. Le runtime .NET et le runtime fixe WebView2 restent embarqués. Extraire le pack complet, sans séparer les EXE de `wad`.

Les préparations, le catalogue utilisateur, les tickets, le JSON privé de déploiement et les fichiers de sites ne sont pas publiés dans les packs génériques. Les règles de configuration par défaut dans `wad` et les fonctionnalités antérieures de planning, météo, présentation, cartes et bibliothèque sont conservées.

## Qualification et limites

La publication de cette version exige les suites du moteur et de SQLite, les contrôles des changements de source et des tournées, les échanges réels sur un dossier de test et PostgreSQL éphémère, les formulaires Chromium, les parcours avec le vrai pont C# et la base SQLite, puis les contrôles des deux EXE réellement extraits sous Windows avec leur runtime WebView2 et le sélecteur de fichiers natif.

Les packs sont contrôlés par manifestes et SHA-256, puis le pack DEV est reconstruit hors du dépôt avec une restauration isolée. Les démarrages, la mise à jour et le retour arrière sont testés sur des copies temporaires. Les résultats concernent le commit indiqué par `wad/version.json` et par la livraison, pas un autre commit voisin.

Les compteurs couvrent les préparations enregistrées et les sources connues du poste. Ils ne remplacent pas une collecte GLPI ni une synchronisation réseau récente. Le portail GLPI/SSO, les permissions SMB et les imprimantes de l’organisation ne sont pas accessibles aux tests automatisés ; leur fonctionnement reste à vérifier dans l’environnement cible. Le guide `wad/docs/Preparation-terrain.md` décrit les parcours, la compatibilité et les limites de capacité.
