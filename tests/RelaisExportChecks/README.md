# Export natif pour Relais

Cette suite console utilise les classes réelles de GLPI-Pilot, des fichiers SQLite temporaires et des tickets entièrement synthétiques. Aucun navigateur, service IA, portail professionnel ou profil utilisateur n’est consulté.

```sh
dotnet run --project tests/RelaisExportChecks/RelaisExportChecks.csproj -c Release
```

Les **12 scénarios** couvrent le contrat natif, les motifs d’exclusion par ticket, la séparation des installations, les citations et marqueurs privés, les identifiants et révisions exacts, les valeurs nulles et Unicode, la source inchangée et sans migration, la liste blanche de tables, l’autonomie du fichier, un vrai WAL avec un auteur concurrent, les collisions de destination, les annulations, les limites et le refus des métadonnées URL sensibles.

Les tests de plafonds réduisent les seuils par une API interne qui interdit de relever les limites de production. Un scénario supplémentaire dans ce même décompte exerce réellement la limite de corps de 2 Mio à partir de cellules individuellement autorisées par la bibliothèque native.

Pour produire la fixture textuelle consommée par les tests de Relais :

```sh
dotnet run --project tests/RelaisExportChecks/RelaisExportChecks.csproj -c Release -p:Version=4.0.18 -- --fixture /chemin/fixtures/glpi-pilot-relais-v1
```

`schema.sql` est le DDL exact relu depuis le fichier exporté. `documents.json` contient les lignes des quatre tables, avec leurs types scalaires. `provenance.json` indique le générateur, le commit de base, les empreintes des sources et des sorties, le décompte et la nature synthétique des données. La base binaire reste temporaire. L’option `-p:Version` doit correspondre à la version de GLPI-Pilot qualifiée ; elle est conservée dans la provenance sans imposer une version fixe au projet de test.

Un mode distinct permet de qualifier un volume synthétique préparé à l’avance en appelant directement le véritable exporteur, sans exécuter de nouveau les douze scénarios :

```sh
dotnet run --project tests/RelaisExportChecks/RelaisExportChecks.csproj -c Release -p:Version=4.0.18 -- --export-source /chemin/source-synthetique.db https://fixture.example/glpi /chemin/nouveau-volume.relais
```

Ce mode conserve toutes les validations, limites et règles de publication de production. Il écrit uniquement des compteurs de progression et le bilan, jamais les corps des tickets. Il n’est pas un parcours de l’interface utilisateur.

Le contrat portable est décrit dans [Format-export-Relais.md](../../docs/Format-export-Relais.md).
