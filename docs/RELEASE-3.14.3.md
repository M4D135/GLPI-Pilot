# GLPI Pilot 3.14.3 · Windows x64

La collecte GLPI actualise la page depuis laquelle elle a été lancée : depuis l’Accueil, elle conserve l’Accueil. Si l’utilisateur change de page pendant la collecte, sa nouvelle page et les champs en cours de saisie restent en place. Les boutons explicites pour ouvrir Pilotage restent disponibles.

Les widgets conservent la même géométrie en édition et après validation. Les cartes des vues natives utilisent par défaut trois colonnes et une hauteur régulière ; les dimensions et positions personnalisées restent conservées. Le contenu long propose un aperçu et une vue agrandie, sans retirer de données. Les cartes à hauteur automatique suivent aussi le chargement de l’historique.

Un filtre propre à un widget devenu indisponible — par exemple un filtre d’équipe sans colonne Groupe ou Statut exploitable — ne bloque plus toute la page Pilotage. La carte indique « Donnée indisponible » et permet de vérifier les correspondances. Son périmètre inconnu n’est remplacé ni par zéro ni par l’ensemble du service. Les détails, la copie d’identifiants, l’historique et les exports respectent cet état. Les filtres et seuils enregistrés sont conservés ; la carte redevient calculable lorsque les données nécessaires sont de nouveau présentes. Les filtres communs et de comparaison restent stricts.

Cette version reprend les corrections de la 3.14.2 concernant le Socle et la figure du guide de mise à jour. Les documents Word/PDF de `DOC/` restent les instantanés 3.13.6.

Seul le pack **PROD-LIGHT** est destiné à la publication, sans runtime WebView2 embarqué. La qualification Windows du commit exact et l’inspection du ZIP original précèdent sa publication.
