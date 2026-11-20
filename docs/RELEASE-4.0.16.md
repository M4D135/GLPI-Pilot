# GLPI Pilot 4.0.16 — lire la collecte conservée sans la recommencer

## Retrouver les fiches dans le cache local

Le lecteur de Rechercher ouvre les descriptions, suivis, tâches et autres rubriques déjà recueillies dans la bibliothèque locale. Le mode « Cache local » ne bloque plus cette lecture. Les fiches partielles et les versions non confirmées restent consultables avec leur date de lecture et leurs limites.

L’identifiant du ticket est associé à l’installation GLPI et au mapping du relevé conservé. Changer le profil de la prochaine collecte ne fait pas emprunter le contenu d’un ticket de même numéro sur un autre portail ou une autre installation. Une fiche sans contenu conservé est indiquée comme telle et n’affiche pas le corps du ticket précédent.

La consultation reste locale : elle ne déclenche ni navigateur, ni lecture d’onglet GLPI, ni actualisation automatique. Les actions qui nécessitent GLPI sont désactivées avec une explication. La bibliothèque propose aussi l’ouverture directe des copies conservées, sans connexion.

## Vérifier ce qui est déjà enregistré

L’écran de collecte affiche un bilan distinct des contenus conservés sur ce poste : fiches du passage, contenus présents, lectures complètes ou partielles, contenus absents, copies archivées et versions à confirmer. Le bouton **Vérifier les contenus conservés** actualise ce bilan local ; il ne lance pas de collecte et ne réécrit pas les tickets.

Ce bilan recense les copies locales et leur cohérence élémentaire. Il ne constitue pas une comparaison avec l’état actuel de GLPI ni une certification de l’exactitude des informations importées. Le lecteur vérifie ensuite le document au moment de l’ouverture. Les limites de sections restent affichées, même lorsqu’un problème de date existe également.

Dans un relevé GLPI restauré, le bilan et le journal sont liés à l’installation du cache et clairement présentés comme son dernier passage conservé. Un profil actif différent ne remplace pas ce bilan par celui de sa propre installation.

Une date sans fuseau ou sans format exploitable ne permet toujours pas de certifier la fraîcheur. Ce doute ne signifie pas que le contenu a disparu et n’empêche plus de le consulter. Aucun fuseau, aucune date et aucune preuve de fraîcheur ne sont inventés.

## Des commandes de reprise compréhensibles

Dans le cache hors connexion, les commandes de collecte en ligne sont désactivées et leur raison est visible. La vérification locale et les liens de lecture restent disponibles. Depuis une source active, l’écran explique qu’un nouveau passage exige une prévisualisation du périmètre. Les erreurs d’une commande sont ramenées dans la zone visible au lieu de rester en haut d’un écran long.

**Compléter seulement les contenus absents** reprend les seules fiches sans contenu conservé dans le passage choisi. La liste est enregistrée avant toute navigation. Une pause, une interruption ou un redémarrage conserve ce sous-ensemble ; les contenus déjà enregistrés pendant le complément ne sont pas relus pour leur seule fraîcheur incertaine. Les copies partielles, les copies archivées et les documents nécessitant un examen restent conservés.

**Reprendre les erreurs** reste un choix distinct pour demander explicitement la reprise plus large des fiches partielles ou des versions non confirmées. Cette action peut relire des contenus ; elle n’est pas nécessaire pour leur consultation locale.

## Réutiliser les tickets inchangés, en live et pendant la collecte

La lecture live automatique réutilise les contenus disponibles lorsque leur version correspond au relevé courant. La collecte globale applique désormais cette même règle sans expiration fondée sur l’âge : une copie complète et confirmée, toujours à la même version GLPI, n’est pas relue simplement parce qu’elle date de plus de sept jours. La collecte ciblée et les reprises conservent également les copies admissibles.

La lecture explicite d’un onglet supplémentaire conserve aussi le repère de version du corps déjà chargé depuis la bibliothèque. Ce repère reste disponible après redémarrage avec la copie hors connexion : l’ouverture d’un onglet ne déclenche plus, à elle seule, une nouvelle lecture automatique du corps. Les dates de lecture du corps et de l’onglet restent distinctes ; un onglet incomplet ne devient pas confirmé par cette conservation.

L’index GLPI reste contrôlé pour détecter les changements. Les fiches absentes, modifiées, incomplètes ou sans version comparable peuvent nécessiter une lecture ; les contrôles d’identité, de précision des dates et de couverture restent appliqués. La date de lecture d’un détail réutilisé n’est pas artificiellement actualisée. Une relecture forcée reste une demande volontaire distincte.

Le réglage d’expiration automatique des contenus n’est plus proposé dans l’interface. Sa valeur ancienne reste conservée dans les profils pour leur compatibilité, sans provoquer de relecture. Cette règle dépend du repère de modification exposé par GLPI et n’invente pas de preuve pour une modification que le portail ne signale pas.

## Mise à jour et conservation des données

Fermer GLPI Pilot puis extraire le pack publié dans un nouveau dossier local en conservant ensemble `GLPI-Pilot.exe`, `GLPI-Pilot-Admin.exe` et `wad/`. Les réglages du profil et le chemin existant de la bibliothèque sont réutilisés. Aucune réinitialisation, suppression, migration du contenu ou recollecte automatique n’est ajoutée par ce correctif.

Après ouverture, charger le cache habituel et ouvrir un ticket dans Rechercher, ou utiliser **Consulter les contenus** depuis l’écran de collecte. Les fiches enregistrées par les versions précédentes restent utilisables. Les données réellement absentes ne sont pas reconstituées artificiellement.

## Qualification

Les régressions utilisent des données synthétiques et de vraies bases SQLite : accès hors connexion aux contenus partiels, absence de modification des observations et du fichier de données pendant la lecture, isolation des installations, audit local, complément borné et reprise après interruption. Les parcours d’interface exécutent les modules de production et le pont C#.

La publication exige la qualification Windows et PostgreSQL complète du commit exact, puis l’inspection de l’archive PROD-LIGHT originale. La page Releases et le reçu de qualification font foi pour la livraison. Aucun essai sur le portail privé de l’organisation n’est revendiqué.
