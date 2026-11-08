# GLPI Pilot 3.13.7 · Windows x64

L’accueil permet de naviguer dès son affichage, même si une carte attend encore des données. Le calendrier distingue une valeur inconnue (—) d’un zéro confirmé. Une réponse reçue après avoir quitté l’accueil ne remplace pas la page active. Le thème et les animations sont conservés.

Après l’enregistrement d’une préparation terrain, la liste attend sa nouvelle révision avant de permettre une sélection pour aperçu ou export. Si la relecture échoue, le message confirme l’enregistrement et indique comment actualiser la liste.

Les deux téléchargements Windows sont **PROD** (WebView2 inclus) et **PROD-LIGHT** (WebView2 Evergreen requis sur le poste). Chacun contient les exécutables Standard et Admin et le dossier `wad/`. Pour accéder au code et aux tests, télécharger les sources du tag ; il n’y a pas de pack DEV à installer.

Avant de mettre à jour, sauvegarder les bases et les réglages puis aligner les versions des postes partageant le même espace. Les exécutables ne sont pas signés. Comparer l’empreinte du ZIP téléchargé à `SHA256SUMS.txt` avant extraction.

Pour la recette, les journaux, captures et ZIP originaux restent sur le banc privé. Les empreintes sont enregistrées dans le job GitHub du run qualifié ; les téléchargements utilisateurs restent les deux packs de la release.
