# Vérifications d’intégration du navigateur

Ce programme exécute le véritable `BrowserService` et l’adaptateur JavaScript avec Microsoft.Playwright. Un navigateur Chromium de test doit être disponible ; son chemin est fourni par `GLPI_TEST_CHROMIUM`.

Les pages et le téléchargement CSV sont servis par interception Playwright sur `http://glpi.test`. Aucun portail distant ni compte réel n’est utilisé.

```powershell
$env:GLPI_TEST_CHROMIUM = 'C:\chemin\chrome.exe'
dotnet run --project tests/BrowserChecks/BrowserChecks.csproj -c Release
```

Un diagnostic local ciblé peut ajouter `-- --filter "Échantillon"`. La CI n'utilise aucun filtre et exécute toute la suite.

Les cas couvrent une page courante, trois pages avec réconciliation du total, une pagination bloquée, un délai de clic dépassé, l’annulation après une page, l’annulation du sélecteur par Échap, une pagination ambiguë, un bouton métier « Suivant » hors pagination, un sélecteur personnalisé dangereux, une page demandant un mot de passe et la réception effective d’un export CSV natif.

Les contrôles de fiabilité supplémentaires portent sur le prévol de la configuration et du dossier d’export, le verrou exclusif du profil, les changements de critères, d’entité et de total pendant la pagination, et les critères DOM recopiés dans l’adresse par les formulaires GLPI. Les filtres divergents restent détectés ; l’ordre des paramètres, le tri et la taille des pages ne changent pas l’identité du périmètre.

Le parcours automatique est exercé avec un export réellement absent (retour à la première page et pagination), le repli désactivé, une disparition du tableau avec sauvegarde partielle, un échantillon d’identifiants conforme ou divergent, un CSV corrompu, un export ambigu, un reçu incomplet et un contexte modifié après transfert. Un reçu corrompu ou correspondant à d’autres identifiants est conservé intact et provoque un refus explicite, sans repli silencieux. Les identifiants avec séparateurs de milliers sont rapprochés sans altérer les données exportées.

Les contextes de navigateur sont éphémères. Le contrôle de verrou crée un dossier temporaire dédié, sans utiliser le profil de l’application.

Le rapprochement des valeurs possède des régressions pour un échantillon synthétique de 20 lignes et 13 colonnes hors ID, avec 33 différences sur 260 cellules. Le journal situe les écarts par positions de colonne et rôles prédéfinis, sans ID, valeur, ni en-tête libre. Il distingue les contrôles C1, cellules vides, dates, cellules absentes et autres écarts. La fixture reproduit le décompte d’un signalement, pas ses données privées.

Deux parcours natifs complets utilisent une cellule multivaluée GLPI dont seul le premier élément est visible : le popup statique associé permet de comparer la valeur complète au CSV, et un second élément divergent reste chargé avec une réserve explicite, sans certification du relevé. Les variantes structurelles sont détaillées dans [NativeDomChecks](../NativeDomChecks/README.md). Un autre cas couvre la transformation exacte des guillemets doubles en deux apostrophes par [`Search::csv_clean` GLPI 9.5.13](https://github.com/glpi-project/glpi/blob/9.5.13/inc/search.class.php#L7791-L7799) et [10.0.22](https://github.com/glpi-project/glpi/blob/10.0.22/src/Search.php#L9335-L9341). Il ne s'agit pas d'une substitution générale de ponctuation, ni d'une conversion des cellules conservées.

Les contrôles C1, y compris U+0085 que .NET classe comme espace, restent des divergences ; aucun recodage Windows-1252 n’est déduit d’un caractère suspect. Les ellipses au milieu d’un texte restent comparées. Une abréviation terminale n’est exclue que si son préfixe correspond au CSV. Deux chaînes ressemblant à des dates ne sont rapprochées comme dates que dans un rôle de date identifié. Les en-têtes normalisés ambigus ne sont pas appariés par première occurrence ; un contrôle final devenu indisponible conserve le résultat avec une réserve. Un en-tête ambigu ne masque pas un identifiant manquant.

Le scénario d’import de régression reçoit un CSV UTF-8 avec BOM de **788 lignes**, retrouve les **20 identifiants** visibles et constate **32 écarts sur 260 valeurs comparables**, dont trois cellules contenant un contrôle C1. Il vérifie la disponibilité des 788 lignes, l’enrichissement des villes, le contrôle final du périmètre, la création d’Excel sous un nom « à vérifier » et la conservation du CSV natif octet pour octet. Le résultat garde `Complete=false` ; les écarts ne deviennent pas une concordance. Ce scénario échouait avec l’exception bloquante de 3.0.1.

Deux autres parcours complets associent des demandeurs avec fiches d’information imbriquées et des textes longs GLPI ` (...)`, dont le premier triplet est cloné dans le popup multivalué. Les six cellules sont effectivement comparées. Un changement dans la fin du deuxième texte reste détecté et chargeable avec réserve. Le contrôle de périmètre modifié est aussi exercé avec une divergence initiale de valeurs, afin de vérifier qu’il conserve la priorité dans le statut final.

Les options Chromium désactivant le sandbox concernent uniquement ce programme de test en environnement isolé. Le logiciel Windows utilise le navigateur réel avec son sandbox activé.

Ces cas ne valident pas le SSO d’entreprise réel ni le rendu du véritable hôte WinForms/WebView2 sous Windows.
