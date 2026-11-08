# GLPI Pilot 3.2.1 — recherche rapide, précédents et reprise des collectes

Cette mise à jour prolonge la bibliothèque, le partage d’équipe et la présentation Domenia 2 livrés en 3.2.0. Elle permet de retrouver une fiche sans quitter son travail, rechercher des précédents résolus, reprendre une collecte interrompue et comprendre les différences entre deux contributions partagées.

## Recherche rapide depuis toute l’application

**Ctrl+K** présente trois groupes : écrans, actions et tickets conservés. Les commandes se retrouvent avec ou sans accents ; les données de ticket gardent leur contenu d’origine. La recherche des tickets interroge le cache local, après une courte pause de saisie. Elle affiche jusqu’à huit propositions et donne accès à la bibliothèque filtrée pour consulter la suite.

Un numéro exact est placé avant les correspondances trouvées dans le texte, y compris avant la pagination SQL. L’installation GLPI accompagne chaque résultat et un filtre permet de distinguer les portails qui utilisent les mêmes numéros. Le résultat précise aussi l’état du ticket et sa date de lecture.

**Entrée** ouvre le contenu conservé dans un lecteur partagé avec la bibliothèque. La saisie non enregistrée, la position de lecture et le relevé de l’écran d’origine restent en place. Les flèches parcourent les propositions, **Échap** ferme le dialogue et rend le focus. Une palette déjà ouverte ne se duplique pas et ne recouvre pas un éditeur actif. Les réponses devenues anciennes ou reçues après fermeture sont ignorées.

La palette utilise les couleurs et interactions de l’interface Domenia, avec une liste défilante, des groupes visibles et les indications de navigation au clavier. Les thèmes, petites fenêtres et préférences de réduction des animations restent pris en charge.

## Retrouver des précédents résolus depuis une fiche

Le lecteur de la bibliothèque propose une recherche de précédents à partir du titre conservé du ticket. Le moteur C# retient les termes distinctifs et les codes d’erreur, puis cherche dans les tickets résolus ou clos de la même installation. Les contenus partiels, périmés, en conflit ou sans preuve de confirmation ne servent pas de précédent. Le ticket de départ est exclu des résultats.

Chaque proposition montre les termes communs dans le titre ou les extraits, la catégorie éventuelle et les passages exacts conservés, avec leur source et leur date de lecture. Les solutions recueillies sont présentées en priorité. Une catégorie identique ne suffit pas : la recherche automatique exige au moins deux termes utiles communs ou un code alphanumérique exact. Une recherche manuelle permet d’affiner le symptôme, l’équipement ou le code.

Le parcours reste dans un seul lecteur : consulter un précédent puis revenir conserve la fiche de départ, la recherche et l’écran d’origine. Si le contenu du ticket de départ n’est pas confirmé, l’interface le précise ; ses anciens détails ne sont pas utilisés pour fabriquer les termes de recherche.

Le bouton **Chercher des solutions similaires** dans la fiche de travail ouvre ce même parcours. Le rapprochement utilise le numéro et l’adresse GLPI ensemble : un numéro identique d’un autre portail ne prend pas sa place. Si la fiche n’a pas encore été recueillie dans la bibliothèque, un message l’explique en gardant la fiche et la saisie ouvertes.

Le traitement examine au maximum 100 candidats issus de l’index local, recontrôle jusqu’à 16 documents avant restitution et présente au maximum huit résultats. Les compteurs sont affichés ; les limites sont précisées lorsque la sélection est limitée. C’est une recherche lexicale expliquée par ses correspondances et ses extraits ; elle ne produit pas de réponse générée, de pourcentage de confiance ou d’envoi à un modèle externe.

## Bilan durable et reprise ciblée

La bibliothèque conserve désormais le bilan de chaque tentative dans son fichier SQLite local. Jusqu’à 20 bilans sont retenus, en préservant celui utilisé par une reprise en cours ; cette limite ne supprime pas les contenus des tickets. **Dernière collecte** reste accessible après fermeture et redémarrage. **Détail de la collecte** montre les tickets concernés, leur résultat, le motif d’une reprise et les limites du périmètre ; les lignes sont paginées et peuvent être filtrées sur les tickets à relire. L’action principale s’adapte à l’état de la bibliothèque : première aspiration ou mise à jour du cache.

Les compteurs décrivent la tentative réelle. Une nouvelle lecture partielle est comptée comme partielle même si les règles de conservation ont retenu une copie complète plus ancienne. Les catégories distinguent notamment lecture non aboutie, contenu incomplet, contrôle final manquant, date source insuffisante, changement de version et ticket absent du nouvel index. Les messages du journal sont fixes ; ils ne recopient ni texte de cellule ni message brut d’un fournisseur.

**Reprendre les tickets à relire** réactualise d’abord l’index GLPI. La reprise est liée à l’installation, aux réglages et au contexte du bilan. Elle relit les tickets sélectionnés encore accessibles ; un numéro ancien n’est pas utilisé seul pour ouvrir une fiche d’une autre installation. Une absence conserve le cache et donne un état explicite.

Une annulation enregistre l’arrêt avec un délai de sauvegarde indépendant de l’annulation du navigateur. Après un arrêt brutal, les tentatives abandonnées sont reconnues à la prochaine ouverture de la bibliothèque. Les observations déjà écrites restent disponibles. Ce journal décrit les collectes du poste ; il n’est pas diffusé dans l’espace d’équipe.

## Confirmation de fraîcheur liée au contenu

Un contenu nouvellement reçu est conservé avant la vérification finale, avec une fraîcheur à confirmer. La confirmation doit retrouver sa version GLPI et un périmètre vérifiable. Elle porte sur le contenu précis de la tentative ; elle ne peut pas relever une copie devenue concurrente entre-temps.

La confirmation fait partie des mutations synchronisées. Les autres postes peuvent donc recevoir le contenu non confirmé, puis la confirmation correspondante. Un conflit historique conserve son état et ses deux versions. Une interruption ne transforme pas la date de lecture du poste en preuve de modification de GLPI.

Les contenus hérités restent consultables. La nouvelle signature du lecteur et les preuves de confirmation nécessitent une relecture avant leur réutilisation comme cache confirmé. Les postes partageant une base doivent utiliser la version courante pour bénéficier des mêmes règles de fraîcheur.

Une synchronisation restée en attente avant la mise à jour garde l’identité exacte de sa requête d’origine. Si le serveur avait déjà reçu le contenu mais que son accusé de réception avait été perdu, le nouvel envoi retrouve ce reçu sans créer de doublon. Réutiliser cette identité pour un contenu différent reste refusé. La reprise d’un ancien reçu ne transforme pas son ancien indicateur de fraîcheur en nouvelle confirmation.

## Examiner les conflits d’équipe

**Examiner les versions → Comparer tous les champs** compare les champs métier et les données structurées complètes des deux versions archivées. Des horaires, capacités, propositions d’affectation ou réglages différents sont visibles même lorsque le titre et le résumé sont identiques.

La comparaison distingue un champ absent, une valeur nulle, un texte vide et une valeur renseignée. Le texte conserve ses retours à la ligne et les balises apparaissent comme texte. Un filtre recherche les chemins et les valeurs de l’aperçu. Les paires archivées sont paginées pour rendre accessibles les versions plus anciennes.

L’aperçu est borné : au maximum 12 000 nœuds examinés par document, 500 différences affichées, 4 096 caractères par valeur et 400 000 caractères de valeurs au total. Un contenu écourté ou un examen incomplet est signalé. La recherche de différences précède la réduction du texte affiché : une variation située après la limite d’affichage est donc détectée.

**Exporter les deux versions** enregistre un JSON comprenant les deux documents complets, leurs empreintes, l’identité de la paire et le motif du conflit. Le choix d’un fichier peut être annulé ; la base active et ses journaux ne peuvent pas être remplacés par cet export. La comparaison et l’export laissent les contributions et la file de synchronisation inchangées.

L’éditeur précise aussi la destination d’une contribution. Un document créé dans **Bibliothèque → Équipe**, même en brouillon, entre dans le partage de l’équipe. Les notes personnelles restent dans les fiches de l’écran **Tickets** jusqu’à leur action explicite de partage.

## Vérifications et distribution

Les scénarios C# couvrent les compteurs de lecture, les confirmations, les interruptions, les reprises liées au contexte, les conflits et la recherche de précédents. Les nouveaux parcours WebView utilisent le véritable pont C# et SQLite : palette de commandes, lecteur de précédents, bilans, reprises et comparaison des versions. Les tests vérifient notamment la conservation de la saisie en cours, l’isolation entre installations, les réponses tardives, l’exclusion des contenus non confirmés, les limites d’aperçu et l’export des valeurs entières.

La publication exige les contrôles Windows, le démarrage du pack avec le véritable WebView2, la mise à jour et le retour arrière, la reconstruction depuis les sources DEV ainsi que les échanges avec PostgreSQL 17. Les résultats effectifs sont ceux du [workflow du commit livré](https://github.com/M4D135/GLPI-Pilot/actions/workflows/windows.yml), avec journaux et captures associés. Les tests restent synthétiques ; ils ne valident pas le portail privé ni son SSO.

Les packs PROD et DEV gardent la structure `GLPI-Pilot.exe` et `wad`. Extraire le ZIP complet. Le guide [Bibliothèque et partage](Bibliotheque-et-partage.md) présente les opérations, la configuration PostgreSQL et la conservation des données.
