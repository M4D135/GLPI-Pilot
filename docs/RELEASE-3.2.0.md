# GLPI Pilot 3.2.0 — bibliothèque locale et partage d’équipe

Cette version ajoute une bibliothèque de tickets exploitable hors ligne, une recherche dans les contenus conservés et une synchronisation facultative entre postes via PostgreSQL. L’interface reste une WebView locale ; l’acquisition, la persistance et les règles de synchronisation sont implémentées en C#.

## Collecter les tickets et leurs contenus

La page **Bibliothèque** utilise la session GLPI ouverte pour parcourir le critère natif **Tous** des statuts, y compris les tickets résolus et clos. Le périmètre reste celui des entités accessibles et actives du profil ; la corbeille est exclue. Les critères et les liens de navigation proviennent de la page observée. La recherche visible de départ est conservée et une restauration de son filtre natif est tentée à la fin, même après annulation.

Le lecteur parcourt les onglets de consultation reconnus et leur pagination : description, champs, suivis, tâches, solutions, historique, liens et références de documents. Il vérifie la même fiche avant de naviguer et évite les actions de modification. Les pièces jointes sont référencées sans téléchargement automatique de leur contenu binaire.

Les nombres de tickets, d’onglets chargés et les limites rencontrées restent consultables. Une page ou un onglet manquant ne devient pas une lecture complète. L’index est relu à la fin pour recontrôler le périmètre. Une collecte longue reste une suite d’observations datées, pas un instantané transactionnel du serveur GLPI.

## Cache SQLite propre à chaque poste

Le cache conserve les valeurs recueillies, leurs sources et leurs dates. **Mettre à jour le cache** compare la dernière modification GLPI avant de relire les détails. Un jeton connu et inchangé permet la réutilisation d’un contenu complet, après une période de prudence adaptée à sa précision. Les dates à la minute sont prises en charge sans leur inventer des secondes ; le fuseau des dates GLPI doit être choisi explicitement lorsqu’aucun décalage n’est fourni. L’ordre jour/mois ou mois/jour peut aussi être déclaré pour interpréter correctement les dates des douze premiers jours du mois. Sans ce choix, une date ambiguë reste à relire.

Une date absente ou ambiguë force la lecture. L’âge maximal par défaut est de sept jours et **Connexion → Relire tous les contenus** permet une relecture forcée. Une nouvelle métadonnée invalide la fraîcheur d’un ancien détail sans l’effacer. Les versions concurrentes sont conservées ; un ancien contenu ou une lecture partielle ne remplace pas silencieusement une version plus récente ou complète.

Le dossier du cache est configurable sur disque local. Un déplacement vers un nouveau dossier utilise la sauvegarde SQLite et conserve l’ancienne copie. Les fichiers SQLite ouverts sur un partage réseau ne sont pas utilisés comme base commune.

## Recherche de solutions et corpus sourcé

Les vues **Bibliothèque** et **Résolution** recherchent dans les contenus locaux, avec des extraits reliés au ticket, à sa rubrique, à son URL et à sa date d’observation. La résolution exploite les tickets résolus ou clos dont le contenu est complet et courant. Les passages identifiés comme solutions proviennent des solutions réellement recueillies.

L’export JSONL fournit les documents et les passages citables pour une utilisation dans un autre outil ou une chaîne RAG. La recherche intégrée repose sur SQLite FTS5 ; cette version ne transmet pas automatiquement le corpus à un modèle distant et ne prétend pas fournir un moteur vectoriel.

**Utiliser le cache comme relevé** permet de calculer les statistiques depuis les métadonnées conservées. La provenance locale et les dates d’observation restent explicites. Ce chargement ne transforme pas le cache en relevé exhaustif et confirmé de l’état actuel de GLPI.

## Espace d’équipe et PostgreSQL facultatif

Les notes, commentaires, connaissances et affectations peuvent être conservés localement puis synchronisés. Les contributions liées aux tickets appartiennent à GLPI Pilot ; elles ne modifient pas GLPI. Les configurations métier et les observations statistiques peuvent aussi être publiées. Une configuration reçue est examinée avant application ; les dossiers, secrets, connexions et profils de navigateur restent propres au poste.

Les outils existants disposent aussi d’une publication explicite : note personnelle enregistrée, tournée enregistrée, scénario de répartition et compétences/capacités des techniciens du relevé. **Examiner et reprendre** compare le document reçu avec la destination locale avant confirmation. Une note est ajoutée sans effacer le texte privé ; une tournée devient une nouvelle copie en brouillon ; le scénario précédent est sauvegardé avant remplacement ; les préférences privées des techniciens sont conservées. Une modification de la contribution, du document local ou du contexte rend l’aperçu périmé. Ces reprises n’exécutent ni affectation dans GLPI ni tournée.

La synchronisation dispose de révisions, de files d’envoi transactionnelles, d’accusés idempotents et de curseurs de reprise. Elle conserve les versions en conflit et les transmet aux autres clients. Les suppressions de contributions se propagent sous forme de marques de suppression. Les horloges des PC ne servent pas à décider quelle version d’un ticket GLPI est la plus récente.

La connexion centrale est désactivée par défaut. Une fois configurée, les échanges sont manuels ou automatiques pendant l’exécution de l’application, avec cinq minutes par défaut. Cette automatisation synchronise les bases ; la collecte dans GLPI reste une action explicite. Une coupure conserve les modifications locales en attente.

Le serveur PostgreSQL, sa base dédiée, son certificat et ses autorisations doivent être provisionnés par l’équipe. Les ACL de GLPI ne sont pas répliquées dans PostgreSQL : les droits de lecture de la base centrale doivent correspondre au partage voulu. Le mode TLS par défaut est `VerifyFull`. La mémorisation du mot de passe est facultative et protégée par le compte Windows.

Le [guide Bibliothèque et partage](Bibliotheque-et-partage.md) détaille les opérations, les règles de fraîcheur, la configuration PostgreSQL, les vues SQL de lecture et les sauvegardes.

## Navigation adaptée au poste

La barre de gauche regroupe les pages par tâches. **Personnaliser**, en bas du menu et dans la palette, ouvre un éditeur permettant de réordonner les groupes et leurs pages, déplacer un raccourci dans **Épinglés**, ou masquer une entrée. La souris et les commandes clavier sont prises en charge. **Accueil** et **Réglages** restent visibles ; une page masquée reste accessible par la palette.

**Annuler** abandonne les changements du brouillon. **Enregistrer le menu** les conserve sur le poste ; **Réinitialiser le menu** prépare le retour à l’ordre proposé, à enregistrer ensuite. Ces préférences de navigation restent personnelles et ne font pas partie de la configuration d’équipe. La présentation et les transitions poursuivent le modèle visuel Domenia dans la WebView locale.

## Vérifications et distribution

Le workflow ajoute les suites locales de bibliothèque et d’équipe aux contrôles Windows, les vingt scénarios de navigation de bibliothèque et les parcours WebView de la bibliothèque, de la navigation personnalisable et des quatre outils partagés, avec captures. Les refus et la préservation des données locales sont aussi exercés par les scénarios Runtime. Un second job utilise un vrai PostgreSQL 17 éphémère pour tester les échanges entre bases SQLite, les versions anciennes, les modifications concurrentes, les accusés perdus, les annulations et la reprise des curseurs. La publication dépend de la réussite des **deux** jobs.

Les résultats à retenir sont ceux du [workflow du commit livré](https://github.com/M4D135/GLPI-Pilot/actions/workflows/windows.yml), avec les journaux Windows et PostgreSQL joints séparément. Ces notes décrivent les contrôles requis ; elles ne valent pas preuve d’une exécution réussie ni d’un essai sur un portail privé.

Les licences de Microsoft.Data.Sqlite 10.0.13, Npgsql 10.0.3 et de leurs dépendances sont ajoutées aux packs, avec les versions et empreintes des archives NuGet utilisées. Le serveur PostgreSQL n’est pas embarqué dans le client.

Les packs **PROD** et **DEV** gardent la structure `GLPI-Pilot.exe` et `wad`, avec le même exécutable et les runtimes nécessaires. DEV comprend les sources et les nouvelles suites de vérification. Extraire le ZIP complet avant lancement ; les données de bibliothèque restent dans le dossier local configuré.
