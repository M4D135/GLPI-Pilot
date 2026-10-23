# GLPI Pilot 3.2.2 — météo, pilotage et reprise des rapports métier

La version 3.2.2 rapproche les tableaux de bord de la lecture quotidienne d’un service : stock ouvert, demandes et incidents, attente, équipes, catégories, ancienneté et suivi. Elle ajoute des présentations et des règles de reprise aux moteurs C# et à l’interface WebView2 existants. Les dispositions déjà enregistrées restent utilisables.

## Météo du stock

Un indicateur peut utiliser la présentation **Météo du stock**. La carte affiche sa valeur réelle, un état textuel et une icône soleil, nuage ou pluie. Le nombre ouvre les tickets concernés ; les seuils, le périmètre et la définition restent consultables dans la carte. Les animations respectent la préférence de réduction des mouvements et les thèmes clair/sombre.

Pour un stock dont une valeur plus basse est préférable, les comparaisons sont strictes :

| Valeur | État |
| --- | --- |
| Valeur ≤ seuil orange | Maîtrisé |
| Seuil orange < valeur ≤ seuil rouge | À surveiller |
| Valeur > seuil rouge | Sous tension |

Ainsi, un seuil orange de 30 ne change de couleur qu’à 31. L’option inverse existe pour les mesures dont une valeur plus haute est préférable. Les deux seuils doivent être définis et cohérents ; sinon la carte indique **Seuils à définir**.

La couleur ne transforme pas un relevé incomplet en situation maîtrisée. Le contrôle conserve les preuves de réception, de périmètre, d’identité et de statuts du relevé source, avant ses filtres et son éventuel périmètre personnel. Un filtre qui masque les lignes problématiques ne rétablit pas artificiellement la fiabilité. Le nombre reste disponible avec un état neutre lorsque le relevé exige un contrôle. Les seules différences de représentation HTML/CSV ne neutralisent pas un volume dont le comptage est fiable.

Les météos existantes, fondées sur des indices de risque, restent distinctes et disponibles. La météo du stock compare un nombre à des seuils ; elle n’est ni une prévision ni un indicateur de productivité.

## Pilotage des groupes et des types

Le nouveau modèle **Météo et pilotage du service** fournit 14 widgets répartis dans cinq sections : stock, équipes et types, catégories, ancienneté et activité, analyse et actions. Ses seuils de volume restent à définir selon le service. Les titres, notes, sections, couleurs, tailles et positions sont modifiables avec les commandes existantes.

Deux matrices complètent le catalogue :

- **Groupes × statuts** : une participation par ticket et par groupe distinct. Les doublons de libellé dans une même affectation sont éliminés, sans distinction de casse ; les tickets sans groupe ont leur propre ligne.
- **Types × statuts** : les tickets actifs répartis selon leur type et leur statut observés.

Les quatre statuts actifs sont conservés, y compris leurs zéros : nouveau, en cours attribué, en cours planifié et en attente. Les tableaux présentent les totaux de ligne ; les lignes sont classées par leur total. Les barres empilées appliquent le nombre de lignes choisi et comportent une légende. Le détail paginé permet de retrouver les contributions au-delà de l’aperçu.

Un ticket présent dans deux groupes contribue à deux lignes du tableau des groupes, mais une seule fois au stock global. Le total des participations peut donc dépasser le nombre de tickets. Ce mode explicite ne dépend pas de la préférence d’affectation fractionnée utilisée par d’autres analyses.

Les filtres de répartition et de ligne de matrice acceptent plusieurs valeurs, une par ligne. Les valeurs d’un filtre sont reliées par **OU**, les filtres successifs par **ET**. Leur union dédoublonne les tickets communs. L’exclusion retire cette union du périmètre commun. Un libellé contenant une ponctuation reste un libellé littéral ; aucun découpage implicite ne crée de groupes. Une source de filtre absente est refusée plutôt qu’ignorée.

## Reprise de 19 champs d’un tableau d’incidents

Les rôles couvrent ID, titre, statut, dernière modification, ouverture, priorité, demandeur, technicien, catégorie, rédacteur des suivis, rédacteur des tâches, TTR avec progression, délai de prise en compte, type, groupe de techniciens, âge déclaré, nombre de groupes déclaré, nombre de rédacteurs déclaré et description.

Les huit rôles nouvellement séparés sont conservés dans les correspondances de colonnes, les configurations portables et les requêtes. Les colonnes de rédacteurs ont leurs répartitions propres. Les nombres et âges déclarés fournissent des séries numériques et des statistiques de moyenne, médiane, percentile 90 et maximum, lorsque leurs valeurs sont exploitables.

Les définitions restent explicites :

- **Plusieurs groupes** est calculé à partir des groupes distincts réellement présents dans la cellule d’affectation, indépendamment du compteur déclaré par le fichier.
- **Sans rédacteur visible** exige que les deux colonnes de rédacteurs existent et que les deux cellules soient vides. Il ne prouve pas l’absence de travail sur le ticket.
- **Âge déclaré** reste distinct de l’âge recalculé depuis l’ouverture. Les comptes de groupes et de rédacteurs déclarés ne remplacent pas les participations observées.
- **TTR + Progression** reste un champ source ; un pourcentage n’est pas interprété comme une échéance. Le délai de prise en compte reste disponible sans lui attribuer une unité non démontrée.
- Une donnée absente ou non interprétable reste indisponible. Les widgets concernés peuvent être masqués automatiquement ; un zéro calculable reste affiché.

Des captures permettent de reprendre les champs, les seuils et les regroupements visibles. Elles ne donnent pas accès aux formules internes d’un classeur ni aux onglets non fournis. Les modèles métier privés documentent ces limites et conservent leurs règles modifiables.

## Explorer un extrait Excel ou CSV hors ligne

**Importer un fichier de test**, depuis l’accueil, la collecte ou le relevé, ouvre un mode de simulation avec le fichier local choisi. Les formats acceptés sont CSV, TSV, TXT, XLSX et XLSM. Pour Excel, l’aperçu propose les feuilles visibles et permet de saisir le numéro de la ligne d’en-têtes ; à défaut, la première ligne non vide sert d’en-têtes. Le format historique `.xls` demande un enregistrement préalable en `.xlsx` ou `.csv`.

Le lecteur utilise seulement les valeurs enregistrées dans le fichier. Il n’exécute aucune macro, formule ou liaison externe. Les durées Excel restent des durées ; les cellules uniquement mises en forme ne créent pas des milliers de colonnes. Les en-têtes vides ou répétés reçoivent des noms distincts. Une formule sans résultat enregistré est signalée : la ligne reste présente, le reçu ne prétend pas que toutes les valeurs sont disponibles et la météo reste partielle.

Un bandeau permanent rappelle le fichier chargé, les dimensions du tableau et l’état de lecture. Les correspondances, requêtes et dashboards permettent d’explorer cet extrait. Une météo calculable porte explicitement sur la simulation : elle ne certifie ni le stock réel, ni la recherche, ni le périmètre du portail GLPI.

La collecte, les actions sur les tickets, la synchronisation et l’archivage sont suspendus pendant le test. Les écrans opérationnels expliquent comment revenir au relevé courant ; la palette recherche les écrans et les commandes sans interroger la bibliothèque réelle. Les personnalisations de tableaux de bord, de requêtes et de vues restent enregistrées dans les réglages, comme l’interface l’indique.

**Quitter le mode test** restitue le relevé précédent, même après plusieurs imports. Les correspondances et la géographie de ce relevé sont conservées ; les définitions historiques d’une archive restent figées. Les requêtes volontairement modifiées pendant le test restent disponibles sur le relevé courant restauré. Annuler le choix d’une feuille laisse l’état précédent intact. Un fichier modifié depuis son aperçu doit être sélectionné de nouveau.

## Collecte discrète et intervention SSO

La fenêtre du navigateur dédié reste réduite pendant la collecte. Une redirection de connexion seule ne la ramène pas au premier plan. Si une étape de connexion visible demande une action — mot de passe, code temporaire ou écran avec **Se souvenir de moi** et **Se connecter** — GLPI Pilot affiche temporairement cette fenêtre afin que l’utilisateur puisse intervenir.

Les formulaires visibles dans une iframe et les fenêtres de connexion ouvertes par le portail suivi sont également reconnus. Après le retour à la recherche attendue, la collecte retrouve son affichage discret. Une annulation réduit seulement une fenêtre révélée automatiquement. Les choix manuels d’afficher ou de masquer le navigateur restent respectés ; masquer un formulaire ne provoque pas sa réouverture répétée tant que cette étape ne change pas.

GLPI Pilot ne coche ni ne soumet le formulaire d’authentification à la place de l’utilisateur. Le parcours reste soumis aux règles du SSO de l’organisation. Les scénarios locaux couvrent le comportement sur des pages de test isolées ; le fournisseur privé doit être essayé sur le poste réel.

## Ajouter une disposition et importer seulement les réglages fournis

Dans **Modifier le dashboard → Disposition → Importer une disposition**, **Ajouter à mes tableaux** est l’action recommandée. L’aperçu présente les tableaux ajoutés et les noms éventuellement renommés. Les tableaux actuels, leurs identifiants, les favoris et les contextes existants sont conservés.

L’ajout ne peut pas dépasser douze tableaux. Un dépassement est refusé avant mutation. Les collisions d’identifiants sont résolues uniquement sur les nouveaux tableaux. Les dispositions globale et personnelle sont indépendantes. Une empreinte empêche de valider un aperçu devenu périmé ; l’import peut être annulé et rétabli en une seule opération. **Remplacer cette disposition** reste un choix explicite.

L’aperçu de configuration portable ne propose que les sections présentes dans le fichier. Les propriétés absentes restent celles du poste : importer une disposition globale seule ne réinitialise pas la disposition personnelle, et importer un seuil seul ne remet pas les autres indicateurs à leurs valeurs par défaut. Les tableaux fournis restent des collections explicites soumises au choix fusion/remplacement et à la validation avant application. Les versions portables 3.2.x sont acceptées ; une version de format future inconnue reste refusée.

## Sites sans commune renseignée

Un site peut maintenant être enregistré avec son nom et ses variantes alors que sa commune n’est pas connue. La détection conserve son identifiant et affiche **Commune non renseignée**. Ce site reconnu n’est pas classé par défaut parmi les lieux à revoir. Les homonymes et les correspondances multiples restent ambigus.

Le formulaire permet de rattacher la commune plus tard sans changer l’identité du site. Les bâtiments, corrections manuelles, statistiques par site, archives, configurations portables et paniers de tournée conservent ces références. Un point cartographique nécessite toujours des coordonnées réellement fournies.

Le CSV minimal `Type;Nom;Alias`, avec `Type=Site`, suffit pour un site autonome. Lors de la mise à jour d’un site identifié, une colonne `VilleId` absente conserve son rattachement ; une colonne présente et vide permet un détachement explicite, visible dans l’aperçu. Les alias fournis remplacent la liste d’alias, comme dans les imports précédents.

Pour ajouter un référentiel à des lieux déjà configurés, choisir **Fusionner** et examiner les conflits. La fusion s’appuie sur les identifiants, pas sur une déduction de proximité entre noms. Les noms et correspondances propres à une organisation sont fournis dans ses fichiers privés d’import et ne figurent pas dans le dépôt public.

## Vérification de la livraison

Les scénarios ciblés couvrent les comparaisons météo aux frontières, les filtres masquant une anomalie source, les unions de groupes, les quatre statuts et leurs zéros, les imports sans perte, les nouveaux rôles et les sites autonomes. Ils contrôlent aussi le choix d’une feuille Excel, les valeurs calculées manquantes, l’annulation, le retour du mode test et les transitions visibles du SSO. Des parcours Chromium utilisent les vrais handlers C#, la persistance et la politique de sécurité des pages de production.

La compilation locale et les tests portables ne constituent pas une validation du pack Windows. La livraison exige le workflow du commit exact, avec les contrôles Windows, les parcours de l’hôte WinForms/WebView2, les packs PROD/DEV, la reconstruction DEV, la mise à jour et le serveur PostgreSQL réel. Les résultats définitifs sont ceux de ce workflow et des journaux qui accompagnent la release. Le portail privé GLPI et son SSO restent à vérifier dans leur environnement réel.
