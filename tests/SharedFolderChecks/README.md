# Contrôles du dossier partagé facultatif

    dotnet run --project tests/SharedFolderChecks/SharedFolderChecks.csproj --configuration Release

Les 22 scénarios utilisent un dossier temporaire et des caches SQLite distincts, placés hors du dossier partagé. Tous les index de test, y compris ceux des processus enfants, reçoivent explicitement leur dossier temporaire local. Deux scénarios démarrent un véritable processus supplémentaire afin de vérifier les échanges entre sessions et le verrou interprocessus.

Couverture :

- Identité stable du registre et conservation des chaînes originales.
- Écritures concurrentes, révisions monotones, absence d’écrasement.
- Verrou FileShare.None, attente bornée et annulable, sans suppression ni reprise forcée.
- Reprise après redémarrage ou perte de reçu ; refus d’un identifiant de requête réutilisé.
- Lots de calendrier entiers, comparaison de versions et conservation des propositions rejetées.
- Fichier temporaire inachevé ignoré ; corruption, trou et disparition de fin de journal détectés.
- Reprise d’un point de contrôle resté en retard après une transaction déjà validée.
- Dossier indisponible : aucune recréation silencieuse, file locale conservée, reprise après retour du dossier.
- Isolation du mode simulation.
- Corpus de tickets complet, reçus durables et conflits fondés sur la version source GLPI.
- Aucun fichier SQLite dans le registre partagé.
- Index local supprimé, illisible, incomplet ou dont un contenu a été altéré : reconstruction depuis les transactions communes validées.
- Interruption après publication du JSON partagé mais avant commit de l’index local : reprise unique, sans perte de reçu ni double écriture.
- Nouvelle identité du partage au même emplacement : exclusion des anciennes données de l’index.
- Relecture de journaux produits par l’ancien protocole en mémoire, avec reçus identiques et calendrier atomique.
- Historique de 80 versions de document et 80 versions de fiche de 140 000 caractères chacune : contenus et reçus complets sur disque, pagination conservée et hausse de mémoire managée retenue inférieure à 24 Mio après collecte. Ce contrôle mesure la rétention des transports, pas la consommation totale du logiciel.

Le transport conserve des transactions JSON fermées dans GLPI-Pilot-Shared/team et GLPI-Pilot-Shared/tickets, une identité commune, une chaîne d’empreintes et un point de contrôle. Une transaction est écrite dans un fichier temporaire, vidée, puis publiée par renommage dans le même dossier. Les caches et leurs journaux SQLite restent locaux.

Les constructeurs acceptent un deuxième paramètre facultatif `cacheFolder`. Le runtime utilise `SharedIndexes` dans le dossier local de bibliothèque ; sans paramètre, l’index va dans `SharedFolderIndexes` du profil local de l’application. Une empreinte du chemin de partage et le domaine distinguent les index. Un chemin réseau, une liaison de fichiers ou un index situé dans le dossier partagé est refusé.

L’index contient les documents courants, les anciens reçus, le flux paginé et les métadonnées des transactions. Il ne fait pas autorité : un index absent ou corrompu est reconstruit automatiquement depuis le journal validé. Les fichiers SQLite illisibles sont conservés localement sous un nom `.invalid-*` avant reconstruction. Les transactions partagées altérées ou manquantes arrêtent toujours l’échange. Le calcul d’une mutation, ses changements et son reçu sont préparés dans une transaction locale, puis le journal partagé est publié avant le commit local. Une interruption dans cet intervalle est récupérée par replay.

Les transports ne gardent aucun corpus ni historique de contenus dans leurs champs mémoire. Les lectures visent des pages de 32 Mio ; une contribution indivisible avec sa proposition concurrente, ou un lot entier de calendrier, peut atteindre 64 Mio. La sérialisation, les reçus et les transactions sont bornés pendant l’écriture. Le cache de pages SQLite est limité à 8 Mio par connexion ; les métadonnées portent sur au plus 100 000 transactions par domaine. La première reconstruction peut nécessiter de relire tout le journal et consomme de l’espace disque local. Les synchronisations suivantes ne rejouent que les nouvelles transactions et vérifient les métadonnées du journal existant.

Ces tests vérifient le protocole et les accès concurrents sur le système de fichiers de la machine exécutant la suite. Ils ne remplacent pas la qualification d’un serveur SMB particulier ni de ses autorisations Windows.
