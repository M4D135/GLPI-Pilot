# GLPI Pilot 3.13.4 · Windows PROD + DEV

La collecte ouvre chaque fiche directement sur son onglet « Ticket » : un clic de moins par fiche, et 16 % de temps en moins au banc GLPI 9.5.

## Collecte

La lecture d'un ticket demande la fiche avec son onglet « Ticket » (paramètre forcetab, honoré par GLPI 9.5 et 10), lit cet onglet en premier sans clic, puis les autres onglets conservés. Elle ne réactive plus l'onglet « Ticket » à la fin de chaque fiche ; un seul clic, en fin de collecte, le fait de nouveau mémoriser par GLPI pour les lectures de fiches suivantes. Si une version de GLPI ignore forcetab, GLPI Pilot ouvre lui-même l'onglet « Ticket », comme en 3.13.3.

En 3.13.3, environ une fiche sur cinq s'ouvrait sur « Traitement du ticket » et coûtait un clic de plus : GLPI ajoute aux liens vers les tickets résolus un forcetab qui désigne un onglet absent de GLPI 9.5. GLPI Pilot remplace désormais ce forcetab.

Le lecteur de la bibliothèque passe en version 4, car l'ordre des sections conservées change. Comme en 3.13.3, une fiche complète lue par une version antérieure (3.13.2 ou 3.13.3) est conservée telle quelle tant que la date de dernière modification affichée par GLPI ne change pas, et une collecte suspendue se reprend. Les règles de fusion des fiches sont inchangées.

## Vérification

Même banc que pour la 3.13.3 : Windows, Microsoft Edge, GLPI 9.5.13 jetable (PHP 7.4, MariaDB), tickets fictifs, contrôleur de collecte réel, une seconde entre deux navigations.

| Mesure | 3.13.3 | 3.13.4 |
|---|---|---|
| Collecte complète de 30 tickets, avec pause et reprise | 13 min 30 s | 11 min 24 s |
| Relecture de 5 tickets modifiés dans GLPI | 71 s | 66 s |
| Contenu d'un ticket, médiane sur 20 (moteur seul, sans rythme) | 11,3 s | 10,2 s |

Journal du serveur GLPI : chacune des 55 fiches du banc ouverte une seule fois avec forcetab=Ticket$main, cinq onglets chargés par fiche, aucun clic de réactivation par fiche. Sur un vrai GLPI 9.5.13 sous Linux, 8 fiches sur 8 sont complètes avec suivis et solutions. Contrôles d'acquisition sur portail synthétique : 25 sur 25 (forcetab demandé, forcetab d'un lien GLPI remplacé, onglet « Ticket » chargé une seule fois, clic de fin de collecte sans nouvelle ouverture de fiche) ; bibliothèque 44 sur 44 (conservation des fiches 3.13.2 et 3.13.3) ; collecte 30 sur 30.

## Qualification et distribution

Même procédure que 3.13.1 à 3.13.3 (run complet, inspection des packs, publication distincte vérifiée).

## Avant la mise à jour et limites

- Passer le poste collecteur en 3.13.4, synchroniser la bibliothèque partagée avant sa première collecte en 3.13.4 (en répétant la synchronisation jusqu'à ne plus rien recevoir), puis ne plus collecter depuis un poste resté en version antérieure : il relirait les fiches de la 3.13.4 et les ferait apparaître dans « Versions à comparer ». Un retour du poste collecteur à une version antérieure a le même effet.
- Après une collecte interrompue (pause, arrêt, budget atteint), GLPI peut mémoriser le dernier onglet lu ; la lecture suivante d'une fiche hors collecte s'ouvre alors sur cet onglet, comme en 3.13.3.
- Les EXE ne sont pas signés. Sauvegarder les bases locales et les réglages avant la mise à jour.
- La programmation nécessite l'application ouverte. Fermer le panneau ou naviguer pendant sa requête peut interrompre la collecte ; les transactions validées et la reprise restent conservées.
- Le transfert `.glpilibrary.zip` n'est pas chiffré et peut contenir des suivis privés : canal interne protégé et destinataires habilités. Les pièces jointes restent des références et métadonnées, sans analyse de leurs fichiers.
- Aucun portail GLPI/SSO privé, droit SMB ou périphérique d'impression de l'organisation n'a été testé. Les contrôles CI utilisent des profils isolés, des données fictives et des services de test.
