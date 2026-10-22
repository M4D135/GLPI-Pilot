# GLPI Pilot 3.0.1 — collecte, reprise des profils et robustesse WebView2

Cette version conserve l'architecture C# / WinForms / WebView2 et les 25 espaces de la [3.0.0](RELEASE-3.0.0.md). Elle améliore le rapprochement des exports et corrige la reprise des anciens profils, la fermeture après panne du moteur et la reconstruction du pack DEV. Les packs PROD et DEV sont publiés uniquement après réussite de tous les contrôles Windows du commit correspondant.

## Écarts entre la liste GLPI et son CSV natif

Un faux écart a été reproduit sur les structures publiques GLPI 9.5 et 10 : les cellules multivaluées longues peuvent n'afficher que leur premier élément, avec la liste complète dans un popup statique de la même cellule. L'adaptateur lit désormais ce popup seulement si son association est unique, son format reconnu et son premier élément identique à la valeur visible. Les éléments suivants sont réellement comparés au CSV ; ils ne sont pas exclus du contrôle. Aucun clic ou téléchargement supplémentaire n'est effectué.

La comparaison reconnaît également la transformation exacte des guillemets doubles ASCII en deux apostrophes par `Search::csv_clean` dans ces versions GLPI. Cette représentation est comptée au diagnostic, dans le seul sens liste vers CSV ; les mots, autres signes de ponctuation et caractères de contrôle restent comparés normalement. Les valeurs originales ne sont pas modifiées.

Le journal indique désormais les positions des colonnes concernées, leur rôle reconnu et le nombre d'écarts contenant un contrôle C1, une cellule vide, une date différente, une cellule absente ou un autre écart. Ces catégories décrivent ce qui a été observé ; elles ne supposent pas une cause. Aucun contenu de cellule, identifiant de ticket ou en-tête libre n'est ajouté à ce diagnostic.

Une ellipse au milieu d'un texte n'exclut plus ce texte du contrôle. Une abréviation terminale n'est exclue que si son préfixe correspond au CSV ; une valeur identique avec ellipse est comparée normalement. U+0085 n'est plus effacé comme un simple espace par la comparaison C#. Les formats de date reconnus restent rapprochés dans les seules colonnes de date ; les autres textes gardent leur sens littéral. Des en-têtes ambigus ou une identité devenue illisible au contrôle final empêchent de certifier un relevé complet.

Le contrôle continue à refuser une divergence réelle. Le CSV natif est conservé intact, et les caractères C1 ne déclenchent aucun recodage automatique. Un journal contenant seulement un décompte global ne permet pas d'attribuer chaque différence à une cause : une fixture synthétique de 33 écarts sur 260 cellules vérifie le nouveau diagnostic sans reproduire de données privées.

## Profils provenant de la version WPF 2.0

La version 2.0 conservait les repères de collecte dans les réglages globaux. Ses profils ne possédaient pas encore leur propre objet `Acquisition`. En 3.0.0, resélectionner ou renommer un ancien profil pouvait donc appliquer des champs vides et effacer des repères encore valables.

La lecture des anciens réglages reprend désormais les repères existants dans le seul profil actif dont l'adresse correspond au contexte enregistré. Elle reconnaît l'absence réelle du champ dans le fichier hérité : un objet d'acquisition explicitement vide dans un profil récent garde sa signification. Les autres sources ne reçoivent pas les repères d'un autre portail ou d'une autre recherche.

La sauvegarde préalable des préférences reste en place. Cette migration porte sur les données locales ; elle ne lance aucune collecte et n'ouvre aucune session GLPI. La version 2.0 publiée ne possède pas l'assistant de mise à jour : pour la quitter, fermer l'application et extraire intégralement le nouveau pack dans un nouveau dossier. Les données restent dans leur dossier local habituel.

## Panne du moteur de l'interface

Un arrêt du moteur de rendu ou du navigateur WebView2 pouvait laisser l'hôte attendre un accusé de fermeture d'une page qui ne pouvait plus répondre. La fenêtre passe désormais dans un état dégradé explicite, présenté par l'hôte Windows. Les opérations sont arrêtées sans rejouer de traitement métier, et la fermeture ne dépend plus de la page indisponible.

Le message de récupération distingue les données déjà enregistrées des brouillons qui n'ont pas confirmé leur sauvegarde. L'utilisateur peut fermer puis rouvrir l'application. Les incidents récupérables d'un processus auxiliaire restent distincts d'une perte complète de l'interface.

## Reconstruction depuis le pack DEV

Les sources exportées portent leur commit, leur arbre source et un inventaire SHA-256. La reconstruction vérifie les sources avant de reprendre cette provenance. Un dépôt Git parent sans rapport avec le projet ne peut plus fournir silencieusement son commit au pack.

Des sources modifiées, ajoutées ou retirées ne sont pas annoncées comme une reconstruction fidèle du commit d'origine. Pour poursuivre le développement, utiliser un dépôt Git propre avec les modifications enregistrées dans un nouveau commit. Le manifeste de sources établit une cohérence locale ; il ne remplace pas la signature d'éditeur ou la vérification de l'archive officielle.

Les instructions détaillées figurent dans [GitHub-et-compilation.txt](GitHub-et-compilation.txt). Le build hors ligne conserve ses sources NuGet explicites, le SDK minimal du laboratoire et le runtime WebView2 fixe vérifié.

## Vérification et livraison

Les régressions portent sur la comparaison liste/CSV, les profils hérités, la provenance des sources DEV et les pannes réelles du moteur WebView2 sous Windows. Le pack DEV est aussi extrait hors de tout dépôt puis reconstruit avec un cache NuGet initialement vide et une source de paquets locale ; le PROD reconstruit passe le vérificateur réel de mise à jour. Ces contrôles complètent les suites métier, navigateur, interface, packaging, démarrage et mise à jour avec retour arrière. Les journaux du workflow du commit livré donnent les résultats et les nombres exacts ; les contrôles du code ne sont pas présentés comme une exécution native.

PROD et DEV contiennent le même exécutable, avec .NET et WebView2 embarqués. DEV ajoute les sources, outils et tests dans `wad/sources/`. Extraire l'intégralité du ZIP et garder `wad` à côté de `GLPI-Pilot.exe`. Les deux archives sont accompagnées de leurs empreintes dans `SHA256SUMS.txt`.

La validation du portail/SSO et des périphériques de l'entreprise, ainsi que la fourniture d'un certificat de signature de confiance, restent les prérequis externes des points 197 et 198. Aucun résultat synthétique ne les remplace ; sans certificat fourni, l'exécutable reste non signé.
