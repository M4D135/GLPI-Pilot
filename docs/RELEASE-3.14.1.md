# GLPI Pilot 3.14.1 · Windows x64

L’assistant de mise à jour ne contacte plus GitHub. Les boutons « Vérifier GitHub », « Télécharger et vérifier le pack officiel » et « Ouvrir les publications » sont retirés : un pack s’installe depuis le dossier réseau partagé ou depuis un ZIP local, avec les mêmes contrôles d’empreinte, de commit et de contenu avant l’installation, la même sauvegarde et le même retour arrière.

Une configuration complète exportée par une version antérieure reste importable : l’adresse des publications GitHub qu’elle contient est ignorée. Les nouveaux exports ne la contiennent plus.

Le reste de l’application se comporte comme en 3.14.0. Seul le pack **PROD-LIGHT** est publié ; le runtime WebView2 fixe reste dans le dépôt [Socle Windows](https://github.com/M4D135/socle-windows/releases).

Avant de mettre à jour, sauvegarder les bases et les réglages puis aligner les versions des postes partageant le même espace. Les exécutables ne sont pas signés. Comparer l’empreinte du ZIP téléchargé à `SHA256SUMS.txt` avant extraction.
