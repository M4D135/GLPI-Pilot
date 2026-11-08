# KIT-GUIDES : fabriquer un guide Word et PDF à partir d'un texte

Une seule source (un texte en Markdown simplifié et une fiche JSON) donne le `.docx` et le `.pdf` : page de garde,
sommaire cliquable, chapitres sur une nouvelle page, étapes numérotées, tableaux, encadrés, figures légendées,
blocs de commandes, en-tête et « Page n sur N ». Outil tiré des guides de Pesto 1.0.

Prérequis : Node.js 18 ou plus ; pour le PDF, Google Chrome, Chromium ou Microsoft Edge (détecté sous macOS,
Windows et Linux ; sinon variable `CHROME`). Images PNG ou JPEG.

    npm ci
    node build_guide.js exemple/exemple.json          # essai : exemple/build/*.docx, *.html, *.pdf
    node build_guide.js guides/mon-guide.json         # ajouter --no-pdf sans navigateur

Recadrage exact d'une capture (macOS) : `swift crop_image.swift entrée.png sortie.png x y largeur hauteur`.
Texte lu dans les captures (macOS) : `swift ocr_image.swift images/*.png` (repérer « SIMULATION », « démo »… avant livraison).
Relecture des pages : `swift pdf_pages.swift fichier.pdf dossier 900` (macOS) ou `pdftoppm -png -r 80 fichier.pdf page`.
Texte et fiche en UTF-8 ; le programme refuse les écarts de syntaxe courants avec le numéro de ligne.

La syntaxe du texte, les champs de la fiche et la méthode complète sont dans `PROMPT_GUIDES_AUTRE_LOGICIEL.md`
(version à coller : `PROMPT_GUIDES_AUTRE_LOGICIEL.txt`). `exemple/` montre chaque élément de syntaxe.
