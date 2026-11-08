# Guides GLPI Pilot

Sources des deux guides livrés dans `DOC/` : `technicien.md` et `administrateur.md` (texte), `technicien.json` et `administrateur.json` (page de garde et figures), `images/` (captures recadrées).

Fabrication avec le kit des guides Pesto (`build_guide.js`, méthode dans `PROMPT_GUIDES_AUTRE_LOGICIEL.md`) :

    npm ci
    node build_guide.js technicien.json
    node build_guide.js administrateur.json

Les fichiers produits vont dans `build/` (non versionné) ; copier les `.docx` et `.pdf` dans `DOC/`.

Recadrage des figures : `figures.json` donne, pour chaque figure tirée d'une capture de la CI, la fin de chemin de sa capture et ses dimensions en pixels. `node crop_figures.js --captures <artefacts extraits>...` recadre depuis le coin haut gauche (la barre du haut reste visible) et écrit directement dans `images/` ; `build_guide.js` refuse une figure dont les dimensions ne correspondent pas. Ne pas recadrer avec `sips`, qui recadre au centre. Le workflow `guide-captures.yml` joint aussi les figures déjà recadrées (`figures/`) pour les captures qu'il produit lui-même.

Thème clair pour toutes les figures : le workflow manuel `guide-captures.yml` relance les suites existantes avec `GLPI_GUIDE_CAPTURES` ; chaque capture de test est alors doublée d'une copie en thème clair (même attribut data-theme que l'application), sans changer les tests (run 40833196058 pour cette édition).

Captures : vraie interface de GLPI Pilot 3.13.1 avec son moteur C#, tirées des artefacts de la qualification complète de main 7e52ab3 (run 40823652475) ; certaines viennent des EXE Windows réels (`native-webview`). Les deux vues du menu (déplié et replié) sont des rendus locaux des vrais modules web. Données fictives ou de démonstration uniquement. Le guide technicien ne montre aucun indicateur de mode de test : contrôle par `swift ocr_image.swift images/*.png`. Chaque libellé cité entre « » a été retrouvé dans les sources de l'interface.
