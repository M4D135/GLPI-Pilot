# Vérifications de l’extraction automatique

Projet console .NET 10, exécuté avec le véritable adaptateur Microsoft.Playwright 1.63.0 et Chromium. Il lie directement les fichiers `Core` de l’application et les scripts embarqués `bridge.js` et `native-export.js`.

Toutes les adresses de test utilisent `http://glpi.test`, avec `http://other-glpi.test` pour simuler une seconde instance, et sont interceptées en mémoire. Les autres hôtes sont bloqués. Aucun portail réel, compte Windows, SSO d’entreprise ou service distant n’est utilisé. Les téléchargements et classeurs temporaires sont effacés à la fin.

```bash
GLPI_TEST_CHROMIUM=/chemin/vers/chromium dotnet run --project tests/AutomationChecks/AutomationChecks.csproj -c Release
```

Pour reproduire un échec précis, `GLPI_TEST_FILTER` limite temporairement l’exécution aux noms de contrôle contenant cette chaîne. Le contrôle final du pack s’exécute sans ce filtre.

## Résultats vérifiés

1. Conservation exacte des filtres dans l’adresse mémorisée ; retrait des paramètres d’authentification ; remise à zéro du seul décalage `start`. Le diagnostic exclut les valeurs des filtres, cellules, mots de passe et jetons.
2. Redirection SSO simulée vers un tableau de bord, puis retour automatique à l’adresse complète demandée.
3. Réouverture des filtres que la redirection SSO a retirés.
4. Un écran de connexion avec champ de mot de passe visible est affiché temporairement après stabilisation, sans clic ni rechargement automatique du login. Le nouvel onglet simulant la fin du SSO est ensuite choisi ; l’événement de progression confirme la connexion établie et le navigateur redevient discret sans recharger la recherche.
5. Parcours complet en un clic avec le menu GLPI 10 : une seule page est visible, mais le vrai CSV des cinq lignes est téléchargé, importé et enregistré en Excel. Le CSV reste inchangé ; zéros initiaux et formules textuelles restent littéraux.
6. Choix de la section « All pages » du menu GLPI 11, déplacé dans le DOM à l’ouverture. Un fichier plus court que le total annoncé conserve un état incomplet et un classeur portant le préfixe `GLPI-a-verifier`.
7. Formulaire d’export ancien : choix explicite CSV toutes-pages et préservation des champs de filtre.
8. Export natif ambigu : aucun téléchargement ni action métier.
9. Parcours automatique par pagination : rechargement depuis la première page tout en conservant les filtres, puis récupération des cinq lignes et création des fichiers CSV/Excel.
10. Chargement AJAX différé : un tableau de présentation apparaît d’abord ; l’automatisation attend la vraie recherche GLPI avant d’exporter ses cinq lignes.
11. Repères personnalisés : un faux menu qui soumet un formulaire et un bouton dont `formaction` vise une action métier sont refusés avant tout clic.
12. Une autre instance GLPI, déjà ouverte au même chemin, ne peut pas remplacer l’origine demandée pendant sa connexion SSO.
13. Le portail contient sa recherche dans une iframe visible : l’attente, la sélection du tableau et le téléchargement natif aboutissent aux cinq lignes et au classeur attendu.
14. Une recherche GLPI dont le compteur officiel vaut zéro et dont le menu d’export est absent retourne zéro ligne. Les vrais en-têtes cachés permettent de produire un CSV et un classeur vides explicites.
15. Des proxys transparents transmettent les opérations au véritable Playwright et comptent les appels à `BringToFrontAsync` : aucun pendant une extraction automatique déjà authentifiée, puis un lors de l’affichage explicite, sans rechargement de la recherche. Les commandes CDP réduisent uniquement les fenêtres du contexte dédié. Une demande explicite restaure la fenêtre ; le retour au mode discret la réduit sans réouvrir la recherche. Ce contrôle ne mesure pas le comportement du gestionnaire de fenêtres Windows ni le positionnement du véritable hôte WinForms.
16. Une recherche legacy représentative contient six tableaux de présentation et de données, dont le résultat principal à 16 colonnes et 20 lignes. L’export GET adjacent utilise le vrai code CSV `-3`, conserve le filtre et produit les 828 lignes attendues ainsi que leur classeur.
17. La variante ancienne combine une disquette `input type="image"` et un vrai select masqué par son interface Select2 ; elle suit le même parcours natif complet.
18. Lorsque le repli par pagination est désactivé, une barre d’outils stable sans export CSV toutes-pages ni option d’affichage suffisante s’arrête en moins de huit secondes. Son diagnostic structurel ne contient ni en-têtes privés, cellules, valeurs des filtres, paramètres d’adresse ni jeton CSRF. Le repli activé est exercé séparément dans `BrowserChecks`.
19. Le choix existant `glpilist_limit=9999999` soumet le formulaire d’affichage du portail. La réponse simulée affiche 828 lignes sur 828 ; seules les données du tableau sont exportées en CSV/Excel, sans prétendre avoir téléchargé un CSV natif.
20. Une limite d’import inférieure au total bloque le changement d’affichage. Un serveur qui conserve 20 lignes sur 828 malgré le choix Afficher tout provoque un refus explicite, sans fichier automatique trompeur.
21. Le proxy simule une création de session CDP qui ne répond jamais. L’ajustement de fenêtre termine avant cinq secondes avec un avertissement unique ; une seconde tentative annulée après 100 ms répond rapidement. Deux appels CDP distincts prouvent que le premier délai a libéré le sémaphore. Aucun focus, rechargement ou clic métier n’a lieu.
22. Un CSV natif UTF-8 avec BOM contient 831 lignes, 16 colonnes et plus d’un Mo. Il inclut des caractères U+000B, U+000C, U+0085 et U+009D, du texte multiligne délimité par guillemets, des guillemets et séparateurs dans une cellule, ainsi que des littéraux `_x000B_` et apparentés. Le reçu est comparé octet pour octet. Toutes les valeurs importées puis toutes les cellules du classeur sont comparées après un seul décodage des échappements SpreadsheetML. Chaque partie XML du classeur doit être bien formée, sans formule exécutée. Aucun repli Afficher tout ne doit être nécessaire ; les diagnostics indiquent les codes Unicode sans révéler la sentinelle contenue dans les cellules.
23. Un téléchargement nommé CSV contient en réalité du HTML. Le reçu est conservé octet pour octet et provoque un échec explicite. Aucun changement d’affichage, CSV local ou classeur ne masque ce transfert invalide ; la phase Fichier prêt n’est jamais publiée.
24. Le même refus est exercé avec une signature de document binaire et un NUL. Le contenu n’est pas interprété comme CSV et n’est pas journalisé.
25. Après un reçu illisible, la présence ou l’absence d’un contrôle d’affichage suffisant ne change pas le refus. Aucun POST ne tente une récupération silencieuse. Seul le reçu natif intact reste sur disque : aucun CSV local trompeur et aucun classeur.
26. L’état CDP rapporté d’une fenêtre est simulé comme déjà réduit. Le mode discret ne demande aucun changement de fenêtre ni focus. Une demande explicite d’affichage demande ensuite l’état `normal`, transmet réellement la commande à CDP et effectue un seul `BringToFrontAsync`.
27. Une erreur CDP précise de cible disparue sur une page non active simule le cas d’un popup de téléchargement éphémère. Elle ne provoque pas le faux avertissement global et ne consomme pas l’avertissement unique : une restriction réelle de politique du navigateur, simulée ensuite, reste signalée.
28. Un relevé natif de cinq tickets est enrichi avec un dictionnaire de villes : MA, Mars et Marseille désignent Marseille. Les deux colonnes ajoutées respectent les indices des lignes ; le CSV reçu reste identique octet pour octet, un CSV enrichi distinct est créé et le classeur contient les valeurs attendues.
29. Le même enrichissement sans l’option Excel conserve le CSV enrichi comme fichier principal. Les colonnes et métadonnées du source demeurent intactes et aucun classeur n’est créé.

30. Sept scénarios de SSO discret couvrent un écran intermédiaire sans URL de connexion standard, la case « Se souvenir de moi » et le bouton « Se connecter », la reprise discrète après validation, les choix manuels d’affichage et de masquage, l’annulation, les contrôles masqués, une iframe IdP visible et un popup IdP ouvert par le portail. La détection utilise le vrai DOM. Le contrôleur ne coche ni ne soumet rien ; le test simule la réussite séparément. Une fenêtre de connexion indépendante et les redirections passives ne sont pas amenées au premier plan. Le masquage manuel reste respecté tant que le formulaire n’a pas changé.

Les identifiants visibles des fixtures d’automatisation appartiennent à leur CSV correspondant. Le rapprochement de cet échantillon complète la vérification des compteurs ; il ne prétend pas prouver à lui seul l’identité de toute la population. Les scénarios de corruption restent distincts des caractères de contrôle textuels légitimes que l’import et le classeur conservent.

## Sources et portée du scénario CSV

Dans les sources officielles [GLPI 9.5.13, `inc/search.class.php`](https://github.com/glpi-project/glpi/blob/9.5.13/inc/search.class.php), le chemin CSV de `Search::showHeader` émet explicitement le BOM UTF-8 `EF BB BF`. Le séparateur vient de `glpicsv_delimiter` et les cellules passent par `csv_clean`. Ce chemin ne fournit aucune preuve d’un export UTF-16 sans BOM.

`csv_clean` passe par [`Html::clean`](https://github.com/glpi-project/glpi/blob/9.5.13/inc/html.class.php), qui appelle htmLawed. Le [`composer.lock` de cette version](https://github.com/glpi-project/glpi/blob/9.5.13/composer.lock) référence htmLawed 1.2.5. L’[archive officielle exacte](https://www.bioinformatics.org/phplabware/downloads/htmLawed125.zip), vérifiée en lecture seule, filtre les contrôles C0 invalides, notamment U+000B et U+000C, mais conserve des caractères déconseillés qui restent valides, dont les contrôles C1. La [documentation primaire htmLawed](https://www.bioinformatics.org/phplabware/internal_utilities/htmLawed/htmLawed_README.htm), sections 3.1 et 5.1, décrit cette distinction.

U+000B et U+000C sont donc ici des cas de robustesse supplémentaires. Leur présence n’est pas affirmée dans le fichier de l’utilisateur. Les journaux reçus établissent un rejet trop strict de l’import, mais ne permettent pas d’identifier son caractère exact ni la version exacte du serveur. Les tests utilisent exclusivement des données synthétiques.

Ces contrôles ne remplacent pas la validation finale du SSO et de l’interface Windows dans l’environnement de l’utilisateur.
