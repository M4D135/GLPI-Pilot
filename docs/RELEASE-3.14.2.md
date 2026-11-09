# GLPI Pilot 3.14.2 · Windows x64

Les messages de l’application ne renvoient plus vers GitHub pour obtenir le runtime WebView2 du Socle. Ils indiquent : « Obtenez le ZIP WebView2 du Socle auprès de votre administrateur ou dans le dossier de diffusion, puis installez-le dans Réglages · Composants. » Au démarrage sans moteur WebView2, le message propose toujours de choisir le ZIP dans la boîte Windows ; les réglages ne sont pas encore accessibles. L’option WebView2 Evergreen reste disponible.

La figure de mise à jour dans les sources du guide administrateur montre les deux sources actuelles : dossier réseau partagé et ZIP local. Elle remplace l’ancienne carte GitHub par deux extraits réels côte à côte, au format 1220 × 490, avec une génération reproductible. Les documents Word/PDF de `DOC/` restent les instantanés 3.13.6 et n’ont pas été régénérés.

Les empreintes du Socle, les contrôles d’intégrité, l’installation des composants, la mise à jour et le retour arrière sont inchangés. Seul le pack **PROD-LIGHT** est publié, sans runtime WebView2 embarqué. Le reste de l’application se comporte comme en 3.14.1.

Avant de mettre à jour, sauvegarder les bases et les réglages puis aligner les versions des postes partageant le même espace. Les exécutables ne sont pas signés. Comparer l’empreinte du ZIP téléchargé à `SHA256SUMS.txt` avant extraction.
