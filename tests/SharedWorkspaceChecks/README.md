# Contrôles des documents partagés et du calendrier

    dotnet run --project tests/SharedWorkspaceChecks/SharedWorkspaceChecks.csproj --configuration Release

La suite locale contient 25 scénarios utilisant de véritables fichiers SQLite et une réplique isolée en mémoire.

Pour exécuter aussi les scénarios PostgreSQL, fournir une connexion de test dans GLPI_PILOT_TEST_POSTGRES, puis :

    dotnet run --project tests/SharedWorkspaceChecks/SharedWorkspaceChecks.csproj --configuration Release -- --postgres

Chaque scénario PostgreSQL crée son propre schéma temporaire et le supprime à la fin. La suite complète comporte 44 scénarios. L’absence de connexion avec --postgres est un échec explicite.

Les contrôles historiques couvrent les documents, les suppressions, la pagination, la conservation des versions concurrentes, la reprise après interruption et le changement de central.

Les contrôles du calendrier ajoutent les transactions de plusieurs documents, les comparaisons de toutes les versions avant écriture, les groupes conservés dans la file d’envoi, les familles de séries lors d’un changement de central, les reçus idempotents, les pages de flux indivisibles, la récupération des propositions rejetées même lorsqu’elles correspondent à des tombstones, la résolution commune des versions et la conservation d’une saisie effectuée pendant une synchronisation.

Les contrôles des données métier vérifient une réception interrompue avant application aux fichiers locaux : la révision réellement connue reste la base de comparaison des éditions et suppressions suivantes. Les deux propositions restent récupérables, une première connexion sans baseline ne revendique pas une version reçue, et un ancien reçu ne supprime pas une nouvelle modification en attente.

Le scénario PostgreSQL supplémentaire provoque une exception sur le deuxième membre d’un lot pour vérifier le rollback des documents, du flux et du reçu dans la même transaction. La même requête doit ensuite pouvoir être reprise.
