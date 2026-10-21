# Contrat de l’adaptateur DOM

`bridge.js` est un IIFE autonome. Son injection expose `window.__glpiPilot`. Il ne fait aucun appel réseau, ne clique aucun contrôle de GLPI et ne lit ni cookie ni stockage du navigateur. Le relancer dans le même document conserve son instance ; après une navigation complète il faut le réinjecter.

## Appels

- `inspect()` renvoie `{url,title,origin,hasPasswordField,authenticatedHint,framesHint,tables,next,pageText,expectedTotal}`.
- Chaque description de tableau contient `{key,label,selector,headers,rowCount,columnCount,headerFingerprint}`.
- `readTable(key)` renvoie `{headers,rows,keys,signature,warnings,headerFingerprint,key,selector,label,next,expectedTotal,pageText}`. `rows` est une matrice de chaînes et `keys` a exactement une entrée par ligne. Une clé vide signifie qu’aucun identifiant métier stable n’a été reconnu : ne pas inventer de déduplication sur ce champ.
- `getNext(key, overrideSelector?)` renvoie `null` ou `{selector,label,disabled,ambiguous,reason}`. Ne jamais cliquer quand `disabled` ou `ambiguous` vaut `true`, ni si `selector` est vide. La détection automatique ne cherche que des contrôles explicitement « suivant » dans une pagination associée au tableau. Un sélecteur fourni explicitement par l’utilisateur est aussi accepté.
- `highlightTable(key)` encadre le tableau pendant 2,5 secondes et le fait apparaître dans la zone visible.
- `pick('table'|'next'|'ready')` installe une sélection visuelle ; le prochain clic choisit l’élément sans exécuter son action. Lire ensuite `window.__glpiPilotPick` : `null` tant qu’aucune décision n’est prise, puis `{kind,selector,label,cancelled}`. Pour un tableau, le résultat inclut aussi `key` et `headerFingerprint`. Échap donne `cancelled:true`.
- `cancelPick()` annule la sélection et publie un résultat annulé.

`authenticatedHint` est un booléen indicatif (élément de déconnexion reconnu, aucun mot de passe visible), jamais une preuve d’authentification. `framesHint` est une chaîne vide ou un avertissement. L’adaptateur n’inspecte pas les documents des iframes. `expectedTotal` est un entier ou `null` ; il provient seulement d’un compteur textuel reconnu, pas d’une estimation.

## Navigation et identification

`key` n’est valable que dans le document courant. Après navigation, réinjecter le script et refaire `inspect()`. Retrouver le tableau via son sélecteur stable puis vérifier `headerFingerprint`. Si le sélecteur ne correspond plus, comparer les en-têtes et demander un nouveau choix si plusieurs tableaux correspondent. Ne pas choisir simplement le premier tableau.

Le sélecteur peut être utilisé à la place de `key` dans `readTable`/`getNext`, uniquement s’il désigne exactement un tableau visible. `signature` identifie le contenu lu (en-têtes, cellules et clés), pas le numéro de page : il sert notamment à détecter une pagination qui boucle sans modifier les lignes.

Les tables HTML et grilles ARIA visibles sont prises en charge. Les cellules fusionnées sont développées dans une matrice rectangulaire et les en-têtes sont rendus uniques. Les colonnes masquées, tables imbriquées et champs mot de passe sont exclus. Les contrôles de sélection de lignes ne deviennent pas des valeurs métier. Une grille virtualisée déclenche un avertissement : la lecture du DOM n’est jamais présentée comme un export complet.

## Fixtures locales

`tests/fixtures/basic-glpi.html` : 5 ordinateurs, 3 pages, compteurs, fin de pagination désactivée, identifiants stables, zéros initiaux. Un bouton métier « Suivant » hors pagination doit être ignoré.

`tests/fixtures/complex-tables.html` : en-têtes à plusieurs niveaux, doublons d’en-tête, cellules fusionnées, colonnes masquées, tableau imbriqué, icône accessible, champ modifiable, grille ARIA partielle, résultat vide, absence d’en-tête et pagination contradictoire.

`tests/fixtures/sso-states.html` : page d’authentification simulée puis portail affiché. Le champ mot de passe ne doit jamais figurer dans une lecture de tableau. Aucun SSO réel n’est testé par cette fixture.
