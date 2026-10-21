# Vérifications WPF réelles sous Windows

Ce projet référence le vrai projet `src/GlpiPilot/GlpiPilot.csproj` et instancie les contrôles de production. Il complète les tests de calcul et de DOM, qui ne lancent pas WPF.

## Exécuter

Sur un poste **Windows x64 avec une session graphique interactive** et le SDK .NET 10, depuis la racine du projet :

```powershell
dotnet run --project tests/WindowsStartupChecks/WindowsStartupChecks.csproj -c Release
```

Pour conserver la sortie console :

```powershell
dotnet run --project tests/WindowsStartupChecks/WindowsStartupChecks.csproj -c Release 2>&1 | Tee-Object -FilePath WindowsStartupChecks.log
```

Le test ouvre une fenêtre intitulée `GLPI Pilot — Windows UI checks (synthetic data)`, parcourt ses vues et la ferme. Ne pas fermer cette fenêtre pendant le test. La limite globale est de 90 secondes ; `Loaded` et `ContentRendered` disposent aussi d’une attente bornée.

Le code de sortie est `0` uniquement si les étapes WPF ont réellement été exécutées avec succès. Une exception ou une fermeture prématurée produit `1`, un dépassement de la limite globale produit `2`, et un environnement inadapté produit `3`. Une compilation croisée depuis Linux ne constitue jamais un résultat `PASS` de ces tests.

## Couverture

- Vraies ressources chargées par `GlpiPilot.App.InitializeComponent()`.
- Constructeur de `DashboardView`, premier rendu vide et rendu alimenté par un calcul de statistiques réel sur trois lignes synthétiques.
- Constructeur de `TechnicianWorkspaceView`, liste de techniciens, identité et dashboard filtré synthétiques.
- Constructeur et templates compilés de `TicketReaderView`, pour les champs, le traitement, les tableaux et le texte.
- Apparition de la fenêtre, événements `Loaded` et `ContentRendered`, matérialisation de la première carte du lecteur.
- Affichage de sept **vues de test synthétiques**, correspondant aux contrôles ci-dessus ; ce ne sont pas les sept onglets de la fenêtre principale.
- Modèles Pilotage, Exploitation, Analyse et Page libre ; styles et présentations de widgets.
- Commande réelle `Personnaliser`, création de l’éditeur et des contrôles de manipulation.
- Deux largeurs de fenêtre et bascules de thèmes clair/sombre sur toutes les vues.

## Isolation et limites

Le harnais crée une sous-classe de `GlpiPilot.App` dont `OnStartup` et `OnExit` n’appellent pas le démarrage et la fermeture de production. Il ne construit **pas** `MainWindow`, ne lit ni n’écrit `AppSettings`, n’inscrit pas les gestionnaires d’erreurs de production et ne crée pas `BrowserService`. Il ne navigue pas sur GLPI et n’utilise ni SSO ni réseau applicatif. Les éventuels accès réseau de `dotnet restore` concernent uniquement les dépendances de compilation.

Cette isolation préserve la configuration, l’identité et le profil navigateur de l’utilisateur. Elle signifie aussi que ces tests ne valident pas le démarrage complet de `MainWindow`, le verrou d’instance, le chargement d’une configuration existante, le packaging publié, les contrôles du navigateur, l’authentification ou les permissions du poste. Le programme de diagnostic de démarrage du pack reste nécessaire pour ces points.

Les tests vérifient construction, chargement, liaison et rendu effectif des contrôles. Ils ne remplacent pas une inspection visuelle humaine et ne simulent pas un glisser-déposer de souris.

**Statut dans l’environnement de développement Linux : non exécutés.** Ils peuvent y être compilés avec le ciblage Windows pour vérifier le code, puis doivent être exécutés sur Windows pour produire un résultat de test UI.
