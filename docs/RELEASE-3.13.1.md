# GLPI Pilot 3.13.1 · Windows PROD + DEV

Cette correction reprend les fonctions de la [version 3.13.0](https://github.com/M4D135/GLPI-Pilot/releases/tag/v3.13.0) : collecte complète et différentielle reprenable, bibliothèque transférable, ergonomie terrain, pilotage des tickets et identité orange pastel. Les couleurs, la disposition et les animations restent inchangées.

## Correction du panneau de partage

Quand la synchronisation automatique est désactivée, le panneau ne doit afficher aucune ligne de programmation. La stabilisation des boutons en 3.13.0 avait transmis une valeur `null` à `replaceChildren`, qui la convertissait en texte visible. Le rendu omet maintenant ce nœud optionnel. Les boutons conservent leur identité, leur focus et le clic commencé pendant une actualisation.

Le contrôle de bibliothèque avec le vrai pont C# couvre le mode local et l'actualisation après un échange réel via dossier temporaire. Les textes recueillis dans les tickets, y compris le mot littéral « null », restent intacts. Le contrôle du vrai EXE Admin vérifie aussi l'absence de ce texte parasite.

La même omission de nœuds optionnels est appliquée au bloc « Données communes », y compris lorsque des conflits sont présents. Cette seconde occurrence, plus ancienne, a été relevée en inspectant les captures avant publication. Les assertions portent sur les deux résumés, sans filtrer ni modifier les contenus métier.

## Qualification et distribution

La chaîne complète Windows du commit intégré vérifie moteurs, interfaces Standard/Admin, PostgreSQL 17, collecte, reconstruction du DEV, démarrage des vrais EXE extraits et mise à jour avec retour arrière. La qualification ne publie rien automatiquement. Après inspection des packs et captures, un déclenchement distinct exige le run complet réussi sur le même commit main et les SHA-256 des deux originaux inspectés. La publication conserve ces ZIP, les retélécharge et vérifie leurs manifestes et SHA-256. La version 3.13.0 et ses fichiers publiés ne sont pas remplacés.

Chaque distribution contient `GLPI-Pilot.exe`, `GLPI-Pilot-Admin.exe` et `wad/`. Le DEV ajoute les sources et tests ; les scripts de support restent dans `wad/support/`. Les ressources web de fonctionnement correspondent aux sources exportées.

## Avant la mise à jour et limites

- Les EXE ne sont pas signés. Sauvegarder les bases locales et les réglages avant la mise à jour.
- Aligner les clients utilisant le nouvel espace partagé et ses paquets sur cette version ; les anciens clients ne connaissent pas nécessairement le verrou du collecteur.
- La programmation nécessite l'application ouverte. Fermer le panneau ou naviguer pendant sa requête peut interrompre la collecte ; les transactions validées et la reprise restent conservées.
- Le transfert `.glpilibrary.zip` n'est pas chiffré et peut contenir des suivis privés : canal interne protégé et destinataires habilités. Les pièces jointes restent des références et métadonnées, sans analyse de leurs fichiers.
- Aucun portail GLPI/SSO privé, droit SMB ou périphérique d'impression de l'organisation n'a été testé. Les contrôles CI utilisent des profils isolés, des données fictives et des services de test.
