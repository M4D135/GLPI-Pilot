# GLPI Pilot 3.13.2 · Windows PROD + DEV

GLPI 10 et GLPI 9.5 sont pris en charge pour la collecte des tickets.

## Collecte

La collecte des tickets (3.13.0) ne reconnaissait que GLPI 10. Sur GLPI 9.5, elle restait sur la liste puis s'arrêtait sans ticket. Elle lit les pages natives GLPI 9.5 : `searchformTicket`, Statut = Tous, tableau des résultats, pagination écrite dans le bandeau, taille de page, colonnes par lien de tri, en-tête répété en fin de tableau. GLPI 10 inchangé.

Si la liste GLPI n'est pas reconnue, la fenêtre affiche « Liste GLPI non reconnue : … » au lieu d'un message trompeur sur la copie locale ; seules des phrases fixes du programme s'affichent, jamais le texte de la page. À la lecture de l'index, elle affiche page et tickets déjà indexés au lieu de rester à zéro.

## Vérification

Vérifié sur un vrai GLPI 9.5.13 jetable, tickets fictifs : index complet 30/30 sur 3 pages, statut Tous confirmé, dates jour/mois/année fiables, fiches complètes avec suivis et solutions. Pages réelles conservées en données de test ; non-régression GLPI 10/11 réussie.

Banc sous Windows avec Microsoft Edge et un GLPI 9.5.13 jetable (PHP 7.4, MariaDB), contrôleur de collecte réel : pause après 10 tickets puis reprise jusqu'au bout, chaque fiche ouverte une seule fois ; passage sans changement en 18 s sans ouvrir de fiche ; après cinq tickets modifiés dans GLPI, seuls ces cinq sont relus et leur nouveau suivi est conservé ; index de 1 500 tickets sur 75 pages en 58 s. La lecture d'une fiche parcourt chacun de ses onglets : un premier passage complet reste long sur un grand parc, d'où un seul poste collecteur.

Guides technicien et administrateur (Word, PDF) dans `DOC/`. Guide technicien couvre carte et tournées ; ne demande plus de lancer la collecte complète : liste « Lancer la collecte », fiche « Actualiser la fiche », contenus reçus du poste collecteur par le partage.

Menu de gauche harmonisé avec Domenia, Pesto et FILPRO : déplié, dernière ligne « Replier le menu », indice Ctrl B au survol ; replié, icône seule centrée le déplie, nom de chaque espace au survol ; choix mémorisé ; fenêtre étroite, panneau Navigation conservé.

## Qualification et distribution

Même procédure que 3.13.1 (run complet, inspection des packs, publication distincte vérifiée).

## Avant la mise à jour et limites

- Les EXE ne sont pas signés. Sauvegarder les bases locales et les réglages avant la mise à jour.
- Aligner les clients utilisant le nouvel espace partagé et ses paquets sur cette version ; les anciens clients ne connaissent pas nécessairement le verrou du collecteur.
- La programmation nécessite l'application ouverte. Fermer le panneau ou naviguer pendant sa requête peut interrompre la collecte ; les transactions validées et la reprise restent conservées.
- Le transfert `.glpilibrary.zip` n'est pas chiffré et peut contenir des suivis privés : canal interne protégé et destinataires habilités. Les pièces jointes restent des références et métadonnées, sans analyse de leurs fichiers.
- Aucun portail GLPI/SSO privé, droit SMB ou périphérique d'impression de l'organisation n'a été testé. Les contrôles CI utilisent des profils isolés, des données fictives et des services de test.
