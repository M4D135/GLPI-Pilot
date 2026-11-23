# GLPI Pilot 4.0.19 — pagination des onglets de ticket

GLPI 9.5 affiche « De 0 à 0 sur 0 » lorsqu’une liste paginée est vide. La bibliothèque reconnaît cette plage sur la première page lorsque les compteurs concordent, que le tableau ne contient aucune ligne de données et que la pagination ne propose aucune action. Une liste vide peut ainsi compter comme un onglet entièrement lu. Les pages non vides continuent de commencer à 1.

Une plage incohérente laisse la fiche partielle. Le bilan conserve alors le numéro de l’onglet, la plage observée et les compteurs attendus pour aider à identifier le désaccord.

Pour vérifier une bibliothèque existante, sélectionner quelques numéros de tickets, prévisualiser le périmètre et laisser le passage aller jusqu’au contrôle final. Vérifier le nombre de fiches complètes, confirmées et sans conflit. Dans Relais 0.6.2, analyser une nouvelle sauvegarde SQLite cohérente. À partir de Relais 0.6.3, utiliser aussi **Exporter pour Relais**. Les anciennes fiches partielles doivent être relues pour obtenir leur confirmation.
