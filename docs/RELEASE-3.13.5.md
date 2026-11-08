# GLPI Pilot 3.13.5 · Windows PROD + DEV

GLPI Pilot change de thème et ouvre sa démonstration comme Domenia 2, FILPRO 2 et PESTO 2.

## Thème

La barre du haut remplace le bouton « Thème » par quatre icônes, dans le même ordre et avec les mêmes libellés que les autres outils : « Thème clair », « Thème sombre », « Contraste élevé » et « Thème : suivre Windows ». L'icône allumée indique le choix enregistré. Les mêmes choix sont proposés dans la palette Ctrl K et dans « Réglages », où le menu « Thème » remplace la case « Suivre le thème Windows ».

Le nouveau thème « Contraste élevé » affiche le texte en blanc sur fond noir, avec des accents cyan et jaune. « Suivre Windows » l'applique aussi quand Windows demande du contraste. Il reste propre à chaque poste : une configuration complète ne l'exporte pas et ne le remplace pas. Les fichiers de configuration produits par la 3.13.5 restent lisibles par la 3.13.4. La case « Contrastes renforcés » reste disponible pour les thèmes clair et sombre.

## Démonstration

La démonstration s'ouvre, se réinitialise et se quitte uniquement depuis la nouvelle carte « Démonstration » de « Réglages ». Chaque action demande une confirmation. Données, Pilotage, les vues métier, la palette et le choix « Démonstration » de la source conduisent à cette carte. Les réglages modifiés pendant la démonstration ne sont pas conservés en la quittant, comme auparavant.

## Vérification

Qualification Windows complète sur la branche (run 41079326501) : contrôles WebView2 réels du sélecteur de thème (quatre icônes, contraste élevé, suivi de Windows), ouverture de la démonstration depuis Réglages avec sa confirmation, palette, transitions avec un enregistrement en attente. Un contrôle de partage métier dont le jeu d'essai portait une date figée au 30 octobre 2026 est désormais daté par rapport au jour du test.

## Qualification et distribution

Même procédure que 3.13.1 à 3.13.4 (run complet, inspection des packs, publication distincte vérifiée).

## Avant la mise à jour et limites

- Un poste qui suivait le thème Windows le suit toujours ; le menu « Thème » de Réglages affiche alors « Suivre Windows ».
- Les EXE ne sont pas signés. Sauvegarder les bases locales et les réglages avant la mise à jour.
- La programmation nécessite l'application ouverte. Fermer le panneau ou naviguer pendant sa requête peut interrompre la collecte ; les transactions validées et la reprise restent conservées.
- Le transfert `.glpilibrary.zip` n'est pas chiffré et peut contenir des suivis privés : canal interne protégé et destinataires habilités. Les pièces jointes restent des références et métadonnées, sans analyse de leurs fichiers.
- Aucun portail GLPI/SSO privé, droit SMB ou périphérique d'impression de l'organisation n'a été testé. Les contrôles CI utilisent des profils isolés, des données fictives et des services de test.
