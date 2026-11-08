# GLPI Pilot 3.13.3 · Windows PROD + DEV

La collecte des tickets est plus de deux fois plus rapide.

## Collecte

La lecture d'un ticket n'ouvre plus les onglets GLPI sans intérêt pour la bibliothèque : Statistiques, Coûts, Projets, Tâches de projet, Base de connaissances, Problèmes, Changements, ni la vue « Tous », qui répète tous les autres onglets. Restent lus : la fiche, le traitement (suivis, tâches, solutions), les validations, les éléments, les documents, l'historique et tout onglet absent de cette liste.

GLPI rouvre un ticket sur le dernier onglet consulté dans la session. La lecture se termine désormais sur l'onglet « Ticket », toujours lisible, reconnu quelle que soit la langue de GLPI. Si GLPI rouvre une fiche sur un onglet vide, un historique vide par exemple, GLPI Pilot ouvre l'onglet « Ticket » au lieu d'attendre le délai maximal puis d'abandonner la fiche. S'il la rouvre sur un onglet ignoré, comme la vue « Tous », il passe aussi à l'onglet « Ticket » avant de lire.

Le lecteur de la bibliothèque passe en version 3. Une fiche complète lue par une version antérieure est conservée telle quelle tant que la date de dernière modification affichée par GLPI ne change pas, même lors du recontrôle des copies et de « Relire tous les contenus » : relue par le nouveau lecteur, elle apparaîtrait dans « Versions à comparer » sur tous les postes qui partagent la bibliothèque. Si elle attendait encore sa confirmation, le contrôle final de la collecte suivante la confirme quand la date GLPI n'a pas changé, que ce contrôle est complet et daté, et qu'aucune autre version n'a été conservée pour cette date. Sinon, le bilan la compte parmi les fiches non confirmées, avec la raison. Dès que cette date change dans GLPI, la nouvelle lecture la remplace. Une collecte suspendue ou interrompue en 3.13.2 se reprend en 3.13.3. Les règles de fusion des fiches sont inchangées : tous les postes qui partagent une bibliothèque rejouent son journal à l'identique.

## Vérification

Même banc que pour la 3.13.2 : Windows, Microsoft Edge, GLPI 9.5.13 jetable (PHP 7.4, MariaDB), tickets fictifs, contrôleur de collecte réel, une seconde entre deux navigations.

| Mesure | 3.13.2 | 3.13.3 |
|---|---|---|
| Collecte complète de 30 tickets, avec pause et reprise | 29 min 11 s | 13 min 30 s |
| Relecture de 5 tickets modifiés dans GLPI | 149 s | 71 s |
| Contenu d'un ticket, médiane sur 20 (moteur seul, sans rythme) | 27,6 s | 11,3 s |
| Passage sans changement | 17,8 s | 18,8 s |
| Index de 1 500 tickets sur 75 pages (moteur seul) | 58 s | 64,5 s |

Le passage sans changement et l'index n'ouvrent aucune fiche et leur code est inchangé : sur ce banc, ils varient d'un passage à l'autre (17,8 à 22,4 s et 58,7 à 72,5 s sur six passages de la 3.13.3). Le passage après cinq modifications ne relit que ces cinq tickets et conserve leur nouveau suivi ; chaque fiche n'est ouverte qu'une fois malgré la pause. Sur un vrai GLPI 9.5.13 sous Linux, 8 fiches sur 8 sont complètes avec suivis et solutions. Contrôles d'acquisition sur portail synthétique : 25 sur 25, dont quatre nouveaux (onglets ignorés jamais ouverts ; fiche rouverte sur un onglet vide ; fiche rouverte sur la vue « Tous » ; onglet de la fiche reconnu sous un libellé étranger). Bibliothèque : 44 sur 44, dont la conservation d'une fiche lue en 3.13.2. Collecte : reprise d'une collecte 3.13.2, confirmation sans conflit d'une fiche 3.13.2 en attente, et fiche 3.13.2 non datée laissée en attente et signalée au bilan.

## Qualification et distribution

Même procédure que 3.13.1 et 3.13.2 (run complet, inspection des packs, publication distincte vérifiée).

## Avant la mise à jour et limites

- Les tickets déjà conservés gardent leur contenu tant que GLPI ne les modifie pas ; leur nouvelle lecture ne conserve plus les sections des onglets désormais ignorés.
- Passer le poste collecteur en 3.13.3, synchroniser la bibliothèque partagée avant sa première collecte en 3.13.3 (en répétant la synchronisation jusqu'à ne plus rien recevoir), puis ne plus collecter depuis un poste resté en 3.13.2. Un poste 3.13.2 relirait les fiches de la 3.13.3, et un poste 3.13.3 non synchronisé relirait des fiches de la 3.13.2 qu'il n'a pas encore : dans les deux cas, elles apparaîtraient dans « Versions à comparer ». Un retour du poste collecteur à la 3.13.2 a le même effet.
- Une fiche 3.13.2 en attente lue moins de trois minutes après sa dernière modification dans GLPI ne peut pas être confirmée : elle le reste jusqu'à la prochaine modification du ticket, et le bilan l'indique.
- Une fiche 3.13.2 incomplète est relue ; si la nouvelle lecture reste incomplète, elle peut apparaître dans « Versions à comparer ». Il en va de même pour toute fiche 3.13.2 si la liste des tickets GLPI n'affiche pas la date de dernière modification.
- Les EXE ne sont pas signés. Sauvegarder les bases locales et les réglages avant la mise à jour.
- Aligner les clients utilisant le nouvel espace partagé et ses paquets sur cette version ; les anciens clients ne connaissent pas nécessairement le verrou du collecteur.
- La programmation nécessite l'application ouverte. Fermer le panneau ou naviguer pendant sa requête peut interrompre la collecte ; les transactions validées et la reprise restent conservées.
- Le transfert `.glpilibrary.zip` n'est pas chiffré et peut contenir des suivis privés : canal interne protégé et destinataires habilités. Les pièces jointes restent des références et métadonnées, sans analyse de leurs fichiers.
- Aucun portail GLPI/SSO privé, droit SMB ou périphérique d'impression de l'organisation n'a été testé. Les contrôles CI utilisent des profils isolés, des données fictives et des services de test.
