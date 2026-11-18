# Contrat SQL de la bibliothèque

Cette suite console n’ouvre aucun navigateur et ne contacte aucun fournisseur IA. Elle utilise de vrais fichiers SQLite temporaires et des tickets entièrement fictifs.

```sh
dotnet run --project tests/TicketLibraryKnowledgeChecks/TicketLibraryKnowledgeChecks.csproj -c Release
```

Les **neuf scénarios portables** vérifient les colonnes SQL, les grandes identités conservées comme chaînes, les valeurs vides et absentes, les champs d’index, les cellules, les éléments observés, les mentions privées, la couverture, les citations, la confirmation, les versions divergentes, l’accès en lecture seule, l’isolation des installations et la migration FTS reprenable sans mutation des documents/révisions/outbox.

Avec un serveur PostgreSQL de test isolé, la variable `GLPI_PILOT_TEST_POSTGRES` contient la chaîne de connexion ; elle n’est jamais affichée. Le serveur doit autoriser la création et la suppression d’un schéma temporaire.

```sh
dotnet run --project tests/TicketLibraryKnowledgeChecks/TicketLibraryKnowledgeChecks.csproj -c Release -- --postgres
```

Les **trois scénarios PostgreSQL supplémentaires** comparent les projections et les citations des deux moteurs après une vraie synchronisation, utilisent le corpus central sans provisionnement de schéma par le client, puis vérifient la conservation des versions concurrentes. Le mode explicite échoue si aucun serveur n’est configuré. Le job PostgreSQL 17 de la CI exige les **12/12** scénarios, dont trois portant le préfixe `PostgreSQL:`. La suite `AdministrationChecks --postgres` vérifie séparément le provisionnement des vues et les droits `SELECT` du compte technique restreint.

Le contrat consommable est décrit dans [Bibliotheque-contrat-chatbot.md](../../docs/Bibliotheque-contrat-chatbot.md). Les tests n’affirment aucune validation du GLPI professionnel, de ses droits effectifs ou de ses pièces jointes.
