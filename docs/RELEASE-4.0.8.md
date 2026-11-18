# GLPI Pilot 4.0.8

## Collecte globale et infrastructure lente

L’acquisition de l’index de bibliothèque n’utilise plus un délai absolu de vingt secondes pour chaque navigation. Elle suit les requêtes natives de la liste, la préparation du critère Statut, les résultats AJAX et la progression réelle de la pagination. Une réponse encore active reste attendue et l’utilisateur peut annuler. Le document doit aussi avoir terminé son initialisation avant toute interaction avec ses contrôles.

Les erreurs de session ou de droits, les réponses HTTP en échec, les interruptions réseau, les changements de périmètre et les tableaux sans progression ont des catégories distinctes. Une erreur de l’index ne devient plus un message de délai de lecture de fiche. Aucun changement de critère n’est envoyé une seconde fois pour tenter de contourner une réponse lente.

La restauration du filtre précédent suit la même progression en fonctionnement normal. Après une annulation, le nettoyage est borné et vérifie la fermeture de l’ancienne page avant une autre lecture. Un échec de restauration est signalé sans remplacer la cause initiale. Le filtre n’est pas réappliqué après un changement de périmètre ou la perte du verrou du collecteur.

## Journal visible dès le premier événement

Le panneau distingue **Journal de la collecte** et **Fiches à reprendre**. Les événements comportent une date, une étape, un niveau et un message ; leur code de diagnostic est consultable dans les détails. Le journal se consulte pendant la collecte, sans attendre la fin de l’opération ni bloquer Pause et Arrêter.

Les événements sont enregistrés en SQLite avec le passage concerné. Une erreur avant le premier ticket reste donc visible même lorsque les compteurs et la liste de fiches à reprendre sont à zéro. Une reprise ajoute ses événements au même journal. Les anciens passages, créés avant cette version, gardent leur bilan et leurs limites lorsqu’ils n’ont pas d’événements détaillés.

Le journal général conserve désormais une entrée **Collecte globale des tickets**, avec son état final et sa cause. Si le stockage ou les préférences sont indisponibles avant l’ouverture d’un passage, une trace de secours reste visible dans la session et l’erreur est enregistrée dans le journal général. Le panneau indique alors que les événements ne sont pas sauvegardés dans la bibliothèque. Les diagnostics contiennent des messages applicatifs et des compteurs, sans corps HTTP, cookies, secrets ni copie du contenu des tickets.

## Base exploitable par un futur chatbot

Les documents déjà recueillis restent la source de référence dans SQLite et sont répliqués vers PostgreSQL lorsque ce partage est configuré. **Douze vues SQL versionnées** exposent directement les tickets, versions, champs, sections, en-têtes, lignes, cellules, éléments observés, onglets, limites, textes et citations. Les archives et conflits gardent leur origine explicite ; ils ne sont pas présentés comme le contenu actuel confirmé.

La recherche plein texte locale couvre aussi les champs et colonnes de l’index ainsi que les propriétés des éléments observés. L’index des bibliothèques existantes est reconstruit par petits lots reprenables, sans modifier leurs documents, révisions, confirmations ni échanges en attente.

En mode Résolution, les extraits de solution correspondant à la recherche sont présentés en premier, avec leur texte exact et leur source. L’ajout de métadonnées à l’index ne doit pas les faire disparaître des extraits affichés.

Le [contrat SQL](Bibliotheque-contrat-chatbot.md), inclus dans le pack, fournit les noms de vues, les champs de provenance et des requêtes SQLite/PostgreSQL. Les vues SQLite sont installées à l’ouverture. Une base PostgreSQL existante doit être préparée de nouveau depuis l’édition Admin pour ajouter les vues et accorder leur lecture au compte technique ; la synchronisation ordinaire ne modifie pas les droits ni le schéma central.

Le corpus couvre ce que la session GLPI a réellement permis de lire. Les pièces jointes restent référencées : leurs fichiers et leur texte ne sont pas téléchargés ou reconnus automatiquement. Les droits GLPI ne sont pas répliqués ; un chatbot devra filtrer les données selon les droits de ses utilisateurs. Aucun modèle ou fournisseur IA n’est connecté par cette version.

## Vérifications et mise à jour

Les tests couvrent les réponses de liste et AJAX lentes, les scripts d’initialisation différés, les erreurs HTTP/réseau, la restauration, l’annulation et le journal d’un échec à zéro ticket. Les contrôles de stockage vérifient la persistance, la reprise, l’isolation des sources et le secours sans base disponible. Le corpus SQL et ses citations sont vérifiés sur SQLite et sur PostgreSQL dans la qualification complète.

Les scénarios utilisent des données fictives ; ils ne constituent pas un test direct du portail privé de l’organisation. La publication exige la qualification complète du commit et l’inspection de l’archive originale.

Fermer GLPI Pilot puis remplacer les deux exécutables et le dossier `wad/` par ceux du pack 4.0.8. Conserver les données et réglages existants.
