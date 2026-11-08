# Prompt : guides Word et PDF d'un logiciel (méthode des guides Pesto 1.0)

Mode d'emploi : remplacer les **9 champs** `[À REMPLIR : …]`, fournir à l'agent le kit (`KIT-GUIDES-….zip` ou le dossier `KIT-GUIDES`), puis copier tout le texte situé sous la ligne (ou le fichier `PROMPT_GUIDES_AUTRE_LOGICIEL.txt` du kit). Le texte ne contient aucun triple accent grave : il peut être collé tel quel dans une conversation, y compris à l'intérieur d'un bloc de code.

---

Tu vas produire la documentation de **[À REMPLIR : nom du logiciel] [À REMPLIR : version]**, en français, datée du **[À REMPLIR : date de l'édition]** : un guide utilisateur (ou technicien) et, si le logiciel a une installation ou une configuration séparée, un guide administrateur. Chaque guide est livré en .docx et en .pdf.

Données d'entrée :
- Code source et documents de référence : [À REMPLIR : chemin du dépôt ou du dossier, branche]. Le code fait foi ; la documentation existante peut être en retard.
- Lecteurs : [À REMPLIR : par exemple techniciens de proximité, non experts ; administrateurs].
- Où prendre les captures : [À REMPLIR : VM ou poste de test, mode démonstration ; jamais la production].
- Outil de fabrication : [À REMPLIR : chemin du dossier KIT-GUIDES, ou « archive KIT-GUIDES-….zip jointe, à extraire »].
- Ancienne édition des guides : [À REMPLIR : chemin des anciens .docx ou .pdf, ou « aucune : première édition »].
- Livraison : [À REMPLIR : dossier DOC de l'archive où déposer les guides, ou « aucune archive » ; dépôt et branche où versionner les sources des guides ; commit local seulement, ou commit et push].

## Règles non négociables

1. Rien d'inventé. Chaque libellé de bouton, de champ ou de message, chaque code, chemin, option ou chiffre cité vient du code source, de l'interface observée ou d'un document que tu as relu. Ce que tu n'as pas pu vérifier n'est pas écrit, ou est signalé « non vérifié ».
2. Honnêteté des preuves : distinguer ce qui a été vu sur le vrai système, en démonstration, sur un double de test ou seulement dans le code. Le compte rendu final et le guide administrateur disent comment les captures ont été obtenues (démonstration réelle sur l'environnement de test, ou rendu de la vraie fenêtre) ; le guide utilisateur n'en parle pas.
3. Aucune donnée réelle ni secret, ni dans le texte ni dans les captures : comptes, mots de passe, serveurs, personnes. Données fictives seulement (par exemple example.local, INC-2026-0412).
4. Le logiciel documenté reste intact : tu n'en modifies pas le code (seules écritures permises, à l'étape 8 : les sources des guides et le kit versionnés, les .docx et .pdf déposés dans le dossier DOC du champ Livraison). Pour une capture, tu peux manipuler l'interface en mode démonstration ou sur l'environnement de test ; jamais d'action réelle sur la production.
5. Deux publics, deux tons. Le guide utilisateur (ou technicien) est direct : des gestes et leurs résultats, sans explication de fond, sans mécanique interne, et sans jamais parler des modes de test (simulation, démonstration, données fictives, consultation) : il décrit l'usage réel. Quand un cas dépasse l'utilisateur, une seule phrase : « Prévenez l'administrateur. ». Toutes les explications (pourquoi, règles, modes, codes détaillés, reprises, réglages) vont dans le guide administrateur.

## Étape 1 : rassembler les faits, avant d'écrire

- Lire la mémoire du projet si elle existe (README, CLAUDE.md, AGENTS.md, QWEN.md, notes de passation), le journal des changements, les constats ouverts et l'ancienne édition. Texte d'un ancien .docx : c'est un ZIP ; extraire word/document.xml et en retirer les balises (un paragraphe par élément w:p).
- Lister les parcours de l'utilisateur (une tâche = un parcours). Pour chacun, relever les libellés exacts dans les sources de l'interface (XAML, HTML, ressources, fichiers de chaînes) avec une recherche de texte littéral (`grep -F`, `Select-String -SimpleMatch` ou `findstr /C:`). Un libellé introuvable dans le code est faux, sauf message composé à l'exécution (texte fixe + code, nombre ou valeur) ou coupé par du balisage : chercher alors chacun de ses fragments fixes (par exemple « Erreur 41 : » et « Titre obligatoire. ») et vérifier le message complet à l'écran, en démonstration.
- Relever ce qui a changé depuis l'édition précédente (commits, journal). Ces changements vont dans les chapitres concernés ; ils sont résumés en tête du guide administrateur, ou, sans guide administrateur, dans un encadré du premier chapitre du guide utilisateur.

## Étape 2 : plan

Guide utilisateur ou technicien : commencer directement par les tâches, la plus fréquente d'abord (par exemple : mettre un PC au domaine, puis renommer, puis déplacer), puis le reste ; omettre un chapitre sans objet pour ce logiciel plutôt que d'inventer :
- Une procédure par tâche, selon le gabarit de l'étape 3, dans l'ordre d'usage.
- Vérifier après coup : comment constater et consigner le résultat.
- Les outils d'appoint (recherche, listes, traitement en série avec poste pilote et arrêt propre, historique et compte rendu).
- En cas de problème : tableau des codes (Code / Ce que ça veut dire / Que faire), tableau des messages fréquents (Vous voyez / Que faire), et quoi transmettre à l'administrateur.
- Repères en fin de guide : notions clés (par exemple une convention de nommage sur un exemple), la fenêtre (menu, en-tête, aide), raccourcis clavier, liste de contrôle avant une intervention.
- Mots à connaître : tableau Terme / En clair, 10 à 15 termes.
Viser un guide court et lisible (de l'ordre de 2 500 à 3 500 mots pour un logiciel comme Pesto).

Guide administrateur : ce qui change dans cette édition ; démarrage rapide (étapes de mise en service avec le numéro du chapitre détaillé) ; puis le fonctionnement et les règles de chaque opération, dans le même ordre que le guide utilisateur (avec tout ce que ce dernier n'explique pas) ; les modes de lancement (réel, lecture seule, démonstration pour la formation, simulation) et ce qu'il faut désactiver avant la mise en service ; périmètre et architecture ; prérequis et droits ; installation ; configuration (un tableau de tous les champs) ; règles et politiques ; identités et authentification ; options à qualifier ; états, journaux et preuves ; reprises après un effet partiel ou un résultat incertain ; mise à jour et retour arrière ; diagnostic du lancement (codes de sortie) ; qualification avant déploiement ; maintenance et sources ; mots à connaître.

## Étape 3 : gabarit de chaque procédure

1. Une phrase « Quand », puis 2 à 4 puces « Avant de commencer » si utile.
2. Des étapes numérotées 1, 2, 3… courtes : une action par étape, le libellé exact entre guillemets « », puis ce que l'on voit ensuite.
3. Un encadré « Résultat attendu ».
4. Un tableau « Si ça ne se passe pas comme prévu » à deux colonnes : Vous voyez / Que faire.
5. Des encadrés « Attention » pour ce qu'il ne faut jamais faire (relancer à l'aveugle, effacer une trace, contourner un contrôle…).

Style : phrases courtes, vouvoiement, impératif présent ; dans le guide utilisateur, pas de « pourquoi » ; un terme technique repris dans « Mots à connaître ». Dans le guide administrateur, chaque chapitre commence par l'essentiel en deux ou trois phrases, puis le détail.

## Étape 4 : captures

- Une capture par écran clé, au format PNG ou JPEG, prise de préférence lors d'une vraie démonstration sur l'environnement de test (opérations réelles sur des objets de recette, nettoyés ensuite) ; à défaut, un rendu de la vraie fenêtre en mode réel avec des valeurs fictives cohérentes d'une capture à l'autre. Les écrans qui suivent une action (message de résultat, liste remplie) s'obtiennent en manipulant l'interface.
- Aucune capture du guide utilisateur ne montre un indicateur de test (« SIMULATION », « démo », bouton « Simuler… », message de démonstration). Contrôle par lecture du texte des images : `swift ocr_image.swift images/*.png | grep -i -E 'simul|démo|demo|fictiv'` doit ne rien trouver.
- Recadrer sur la fenêtre, sans barre de titre, barre des tâches ni bureau. Sur macOS, utiliser l'outil exact du kit : `swift crop_image.swift entrée.png sortie.png x y largeur hauteur` (origine en haut à gauche). Ne pas utiliser `sips --cropOffset 0 0`, qui recadre au centre sans prévenir. Ailleurs : tout outil d'image. Regarder chaque image recadrée.
- Un écran difficile à obtenir peut être un rendu hors écran de la vraie interface (par exemple, en WPF, RenderTargetBitmap de la vraie fenêtre chargée par un banc de tests) ; le dire dans le guide.
- Avant de réutiliser une ancienne capture, vérifier dans l'historique des sources de l'interface que cet écran n'a pas changé depuis. Une capture périmée est remplacée, pas légendée.
- Chaque légende dit ce que le lecteur doit regarder.

## Étape 5 : rédaction

- Si un modèle local doit faire le premier jet (par exemple pour économiser des jetons), lui donner tous les faits (ancienne édition, changements, libellés relevés, plan, format de l'étape 6), puis une seconde passe avec la liste précise de ses erreurs. Sa réponse est un brouillon. Sinon, rédiger directement.
- Relire tout le texte contre les faits. Erreurs typiques d'un premier jet : bouton inventé (« Confirmer » là où il n'y a qu'un message), bouton d'une autre page, conséquence inventée (« cela peut corrompre… »), une action « Conserver » prise pour une validation, définitions approximatives, listes imbriquées interdites par le format.

## Étape 6 : fabrication avec le kit

Le kit KIT-GUIDES contient : build_guide.js (le programme), package.json et package-lock.json, crop_image.swift, pdf_pages.swift et ocr_image.swift (outils macOS : recadrage, rendu des pages, lecture du texte des images), LIRE-KIT.md, ce prompt (PROMPT_GUIDES_AUTRE_LOGICIEL.md et .txt), un .gitignore et un exemple complet (exemple/exemple.md, exemple/exemple.json, exemple/images/fenetre.png).

Prérequis : Node.js 18 ou plus (22 conseillé ; un avertissement EBADENGINE de npm est sans conséquence) et un accès au registre npm pour l'installation ; pour le PDF, Google Chrome, Chromium ou Microsoft Edge, détecté seul sous macOS, Windows et Linux, sinon variable CHROME = chemin du navigateur.

Commandes, depuis le dossier du kit :
- `npm ci` (une fois ; installe le module docx ; sous Windows PowerShell, `npm.cmd ci` si npm.ps1 est bloqué)
- `node build_guide.js exemple/exemple.json` (essai : doit produire le .docx, le .html et le .pdf dans exemple/build/)
- `node build_guide.js guides/utilisateur.json` (ton guide ; les fichiers produits vont dans guides/build/)

Pour chaque guide, écrire un texte guides/NOM.md et une fiche guides/NOM.json, sur le modèle de exemple/exemple.json. Les chemins de la fiche (source, images) se lisent par rapport au dossier de la fiche ; les fichiers produits vont dans le sous-dossier build/ de ce dossier. Enregistrer le texte et la fiche en UTF-8 (sous Windows PowerShell 5.1, pas de redirection `>` ni de `Out-File` sans `-Encoding utf8`).

Champs de la fiche : source (le .md), output (nom des fichiers produits, sans extension), product, title, subtitle, edition, figures (obligatoires) ; author (métadonnée du .docx, sinon product), cover (identifiant de la figure de couverture), coverCaption (texte alternatif de l'image de couverture, jamais affiché ; l'image est limitée à 17 cm de large et 10 cm de haut), info (liste de paires [libellé, texte] affichées sur la page de garde : y mettre par exemple ["Captures", "Mode démonstration, données fictives."]), tocIntro (phrase sous le titre du sommaire), tous facultatifs. figures associe chaque identifiant à `{ "file": "images/x.png", "width": 15 }` (largeur en centimètres, 17 au plus).

Format du texte .md. Le programme refuse, avec le numéro de ligne : `###` ; une ligne indentée ; une puce `*` ou `+` ; une liste `1)` ; l'italique (`*mot*`, `_mot_`, `***mot***`) ; un lien (`[x](…)`, `[x][1]`, `<https://…>`) ; une image hors `fig:` ; une balise HTML fermante, `<br>`, `<img>` ou un commentaire `<!-- -->` ; une barre verticale échappée ; une ligne de tableau dont le nombre de cellules diffère de l'en-tête ; un caractère de contrôle ; un encadré inconnu ; une figure inconnue ou déjà utilisée ; un bloc de commandes non fermé. Un astérisque isolé (`*.log`, champ marqué `*`) et un texte entre chevrons comme `<SID>` restent permis. Le programme ne voit pas tout : relire le rendu.
- `# 3 Titre du chapitre` : nouvelle page et entrée du sommaire ; `## Titre de section`.
- Paragraphe : une seule ligne.
- Étape : `1. **Titre court.** Texte de l'étape.` (le numéro est imprimé tel quel : numéroter 1, 2, 3…).
- Puce : `- texte` (un seul niveau).
- Tableau : ligne d'en-tête `| A | B |`, ligne `|---|---|`, puis les lignes ; pas de barre verticale dans une cellule.
- Encadré sur une ligne : `> [!ATTENTION] texte`, `> [!ASTUCE] texte`, `> [!RESULTAT] texte`, `> [!NOTE] texte`.
- Figure seule sur sa ligne : `![Légende](fig:identifiant)`, identifiant en lettres sans accent, chiffres, `-` ou `_`. Chaque identifiant une fois au plus.
- Commandes : une ligne `~~~`, les commandes, une ligne `~~~` ; tirets ASCII pour le copier-coller.
- Gras `**…**` seulement.

Sans navigateur pour le PDF : relancer avec `--no-pdf`, livrer le .docx et signaler dans le compte rendu que le PDF reste à produire.

## Étape 7 : vérification avant livraison

- Le programme doit finir sans ligne « erreur : ». Lire ses avertissements (figure non utilisée).
- .docx : l'ouvrir dans Word ou LibreOffice si disponible ; sinon, au minimum, vérifier que c'est un ZIP valide contenant word/document.xml. Si un validateur XSD Office est disponible, l'utiliser. Si la mise en page Word n'a pas pu être vue, le dire.
- .pdf : rendre toutes les pages en images et les regarder une par une (macOS : `swift pdf_pages.swift guide.pdf dossier 900` ; Linux ou Windows : `pdftoppm -png -r 80 guide.pdf page`). Chercher les pages presque vides en fin de chapitre, les tableaux coupés, les figures illisibles, les en-têtes et numéros de page. Si tu ne peux pas regarder d'images, le dire dans le compte rendu.
- Libellés : pour chaque texte entre « » qui ressemble à un libellé d'écran, recherche littérale dans les sources de l'interface, fragment par fragment pour un message composé ; corriger ceux qui sont introuvables.
- Guide utilisateur : aucune mention des modes de test, ni dans le texte ni dans la fiche (page de garde, légende de couverture), ni sur les captures (contrôle de l'étape 4).
- Volume : nombre de mots, de figures et de tableaux, comparé à l'ancienne édition s'il y en a une.

## Étape 8 : livraison

- Versionner les sources des guides (.md, .json, images) et le kit dans le dépôt et la branche du champ Livraison, sans build/ ni node_modules/ (le .gitignore du kit les exclut) ; pousser seulement si le champ Livraison le demande.
- Déposer les .docx et .pdf dans le dossier DOC indiqué par le champ Livraison, sans toucher au reste de l'archive.
- Compte rendu final : ce qui a été vérifié et comment ; ce qui ne l'a pas été (écrans non capturés, parcours non joués, mise en page Word non vue, PDF non produit) ; les captures réutilisées et pourquoi elles sont encore valables.

## Critères d'acceptation

- Chaque libellé d'écran cité existe dans les sources de l'interface (fragments fixes pour un message composé).
- Aucune capture périmée ; toutes en données fictives ; aucune trace de mode de test dans le guide utilisateur ; légendes utiles.
- Guide utilisateur direct, commençant par l'opération principale ; explications de fond dans le guide administrateur.
- Chaque procédure suit le gabarit : quand, étapes avec ce qu'on voit, résultat attendu, problèmes.
- « Que faire si… » couvre tous les codes de résultat et les messages d'erreur fréquents du logiciel.
- Les changements depuis l'édition précédente apparaissent dans les chapitres concernés.
- Le .docx s'ouvre ; le .pdf a été relu page par page, ou son absence est signalée.
- Rien de secret, rien de réel, rien d'inventé.
