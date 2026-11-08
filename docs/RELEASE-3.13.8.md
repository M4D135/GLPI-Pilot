# GLPI Pilot 3.13.8 · Windows x64

Cette version ne change pas le comportement de l’application par rapport à la 3.13.7. Elle fiabilise la façon dont les packs sont qualifiés puis publiés.

Les deux ZIP scellés par le runner Windows qualifié sont relayés vers le runner de publication par une étape d’opérateur (`build-tools/relay-retained-packs.py`). Celle-ci ne déclare le succès qu’après confirmation de l’empreinte par le runner destinataire ; voir [Publication des preuves du banc](Publication-des-preuves-banc.md). Les déclencheurs de CI pointant vers des branches supprimées sont retirés.

La vérification navigateur de l’import d’un fichier local relit les onglets jusqu’à ce que l’inspection rende un tableau à collecter. En cas d’échec, elle affiche le message d’erreur de l’interface et une capture au lieu d’attendre 45 s sans explication.

Les deux téléchargements Windows sont **PROD** (WebView2 inclus) et **PROD-LIGHT** (WebView2 Evergreen requis sur le poste). Chacun contient les exécutables Standard et Admin et le dossier `wad/`. Pour accéder au code et aux tests, télécharger les sources du tag ; il n’y a pas de pack DEV à installer.

Avant de mettre à jour, sauvegarder les bases et les réglages puis aligner les versions des postes partageant le même espace. Les exécutables ne sont pas signés. Comparer l’empreinte du ZIP téléchargé à `SHA256SUMS.txt` avant extraction.
