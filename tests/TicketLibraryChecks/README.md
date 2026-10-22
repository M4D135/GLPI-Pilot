# TicketLibraryChecks

Vérifications C# sur de vrais fichiers SQLite temporaires. Aucune donnée utilisateur, aucun profil GLPI et aucun portail réel ne sont utilisés.

```powershell
dotnet run --project tests/TicketLibraryChecks -c Release
```

Le programme contrôle la persistance après réouverture, la séparation des installations, le texte natif (y compris HTML littéral et C1), les citations, la politique de cache à la minute, les conflits à date identique, les dates absentes, les versions anciennes, les lectures partielles, la révision locale conditionnelle, l’annulation, le classement BM25 avant pagination et l’export du corpus. Il refuse les chemins SQLite réseau ou redirigés et un schéma futur.

## PostgreSQL réel

Le mode explicite suivant exige un serveur de test et échoue si la variable manque :

```powershell
# Injecter la connexion du service de test dans GLPI_PILOT_TEST_POSTGRES.
dotnet run --project tests/TicketLibraryChecks -c Release -- --postgres
```

Un schéma `gp_library_checks_<guid>` est créé et supprimé par le test. Les bases locales A/B restent dans le dossier temporaire possédé par le programme. Les scénarios vérifient réellement le push/pull entre postes, les copies anciennes et concurrentes, les dates inconnues, une réponse perdue suivie d’une reprise idempotente, un accusé qui ne supprime pas une mutation plus récente, la publication simultanée sous verrou jusqu’au commit, la reprise d’un curseur et l’annulation. La connexion n’est jamais affichée.

Les nombres exacts et les résultats Windows/PostgreSQL sont ceux des journaux du commit livré ; un succès SQLite local ne prouve pas le fonctionnement du serveur partagé.

## Contrat du corpus exporté

`TicketLibraryStore.ExportAsync` produit du NDJSON UTF-8 dans un instantané de lecture cohérent :

- `recordType: "manifest"`, `protocol: "glpi-pilot-ticket-library"`, `format: 1` décrit le format.
- `recordType: "ticket"`, `format: 1`, `document` conserve le document complet et les chaînes originales de GLPI.
- `recordType: "chunk"`, `format: 1`, `chunkKey`, `ticketKey`, `text`, `citation` contient un fragment de 3 000 caractères au maximum. La citation comprend le ticket, l’URL, la section, la ligne, la date de lecture, `contentHash`, `current` et `isSolution`.

Une solution exploitable doit être explicitement observée dans une chronologie, provenir d’un ticket résolu/clos, et porter `current: true` sur une lecture complète. Un fragment périmé reste exporté avec son état ; il ne devient pas une solution actuelle. Les liens de pièces jointes peuvent être collectés, leur contenu binaire n’est pas annoncé comme lu. Le format ne promet pas de réponse générée ni de cause commune à partir d’une simple similarité lexicale.

## Protocole de stockage

Les constantes publiques `TicketLibraryStore.SchemaSql` et `TicketLibraryPostgresStore.SchemaSql` définissent les tables versionnées. Les tables locales `ticket_library_*` peuvent cohabiter dans le même fichier avec d’autres modules ; aucune connexion ni transaction n’est gardée pendant une navigation GLPI. Le schéma partagé `glpi_pilot_library_*` conserve les tickets, les révisions, les conflits, les réponses idempotentes et le journal de changements.

Un outil qui écrit dans le serveur doit respecter tout le protocole de publication : validation du document et de l’identité, comparaison de la version GLPI, conservation des conflits, UUID immuable, verrou transactionnel `TicketLibraryPostgresStore.PublishLock`, allocation de la révision puis écriture atomique du ticket/journal/réponse avant le commit. Écrire directement une ligne de ticket sans journal et sans ces garde-fous ne constitue pas une synchronisation compatible.

L’heure du poste ne départage jamais deux contenus différents. Les minutes et secondes sont des intervalles ; des intervalles qui se recouvrent ne sont pas ordonnés arbitrairement. Le cache exige un jeton fiable inchangé, un fuseau connu, une lecture complète encore actuelle, une signature de lecteur identique, une lecture effectuée après la fin de l’intervalle et la marge de sécurité, et un âge maximum. Une relecture forcée reste possible.
