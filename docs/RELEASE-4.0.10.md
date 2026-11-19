# GLPI Pilot 4.0.10

## Table active après préparation des colonnes

La collecte peut s’arrêter avec le message « Plusieurs éléments correspondent au contrôle natif des colonnes. Aucune commande n’est exécutée. » lorsque GLPI conserve une ancienne copie masquée du tableau avec le même identifiant HTML.

L’adaptateur natif sait déjà distinguer la table visible et produire sa référence unique. La fermeture des préférences de colonnes utilisait toutefois encore le sélecteur initial pour suivre le renouvellement de la liste. Elle utilise maintenant la référence fraîche issue de cette inspection. La collecte observe ainsi le remplacement de la table active sans sélectionner la copie masquée.

Les protections restent appliquées : origine GLPI, profil, entités, critères de recherche, identité de la vue personnelle et confirmation des ajouts. Deux tables actives qui ne peuvent pas être distinguées continuent d’interrompre la collecte. Aucun premier résultat arbitraire n’est choisi et aucune commande d’ajout n’est renvoyée pour contourner une ambiguïté.

## Vérifications et utilisation

Les régressions de navigateur couvrent une copie masquée partageant l’identifiant de la table, puis deux collectes successives avec les préférences déjà enregistrées. Elles vérifient les commandes de préférences et les exports réellement émis. Le cas existant de deux tables de remplacement actives reste bloquant.

Ces parcours utilisent des données synthétiques dans la qualification Windows. Le portail privé et le SSO de l’organisation ne sont pas testés directement. La publication exige le succès de la qualification Windows et PostgreSQL complète sur le commit exact, puis l’inspection de l’archive originale PROD-LIGHT.

Fermer GLPI Pilot, puis remplacer les deux exécutables et le dossier `wad/` par ceux du pack 4.0.10. Les données, les réglages et les contenus déjà recueillis sont conservés. Les filtres de collecte ciblée, les dates d’historique, la reprise des lots et le taux de couverture introduits en 4.0.9 restent disponibles.
