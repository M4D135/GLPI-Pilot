# Démarrage réel du pack Windows avec WebView2

Ce harnais lance **le véritable `GLPI-Pilot.exe` extrait du pack PROD**, avec ses pages locales et son runtime WebView2 fixe. Il pilote l’interface par Playwright et un port CDP loopback réservé au mode de test. Les calculs, la persistance, la fenêtre WinForms et le contrôle WebView2 sont ceux de l’application livrée.

Une compilation croisée vérifie le code du harnais ; elle ne prouve pas que ces scénarios ont été exécutés sous Windows. Les résultats du commit livré figurent dans les journaux de la [chaîne Windows](../../.github/workflows/windows.yml) et dans le [rapport de vérification](../../docs/Rapport-verification.txt).

## Prérequis

- Windows x64 et une session de bureau interactive, hors session 0.
- Le pack PROD complet déjà construit et vérifié, contenant `GLPI-Pilot.exe`, `wad/web`, `wad/runtime/webview2` et le reste de `wad`.
- Aucun processus `GLPI-Pilot` déjà ouvert : le harnais refuse de commencer dans ce cas.
- Le SDK .NET 10 pour publier le harnais ; ses paquets NuGet doivent être disponibles. Le harnais publié inclut son pilote Playwright Windows.

Le test ne demande aucune session GLPI ni installation de navigateur supplémentaire pour l’interface : le processus piloté utilise le runtime fixe du pack. Pour une VM hors ligne, préparer les dépendances localement et conserver la session console ; les essais interactifs y sont réalisés en fin de chantier. Voir [GitHub et compilation](../../docs/GitHub-et-compilation.txt).

## Commande de référence

Depuis la racine du dépôt, après l’étape `BuildPack` :

```powershell
.\build-tools\Invoke-WindowsCi.ps1 -Stage PackStartup
```

Cette étape publie le harnais dans `artifacts/windows-ci/webview-native-harness`, recontrôle l’archive PROD, puis exige un code de sortie nul et le résumé de réussite.

Pour lancer le même protocole directement avec une archive déjà construite :

```powershell
dotnet publish tests/WindowsStartupChecks/WindowsStartupChecks.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -p:PlaywrightPlatform=win `
  -o .\artifacts\webview-native-harness

.\artifacts\webview-native-harness\WindowsStartupChecks.exe `
  --pack .\artifacts\windows-ci\release\GLPI-Pilot_3.0.0_PROD_Windows-x64.zip `
  --out .\artifacts\native-webview-evidence
```

Adapter le chemin du ZIP à la construction concernée. Les deux options `--pack` et `--out` sont obligatoires. La commande `dotnet publish` ci-dessus suppose une restauration autorisée ou déjà disponible ; dans un environnement hors ligne, restaurer avec la configuration de sources locales, puis publier avec `--no-restore`.

## Scénarios vérifiés

1. **Premier lancement sans interaction** : attente du bootstrap C# et du premier affichage web, fenêtre Windows visible et non vide, chargement des modules, repère `[PRÊT]` dans le diagnostic, fermeture normale et préservation exacte des octets des réglages synthétiques.
2. **Commandes réelles** : passage au thème clair et à 125 %, contrôle de la largeur effective du viewport WebView2, retour à 100 % par `Ctrl+0`, chargement de la démonstration et calcul du dashboard C#.
3. **Navigation et présentation** : ouverture de chaque page du menu, absence d’erreur de page, captures du dashboard clair et de l’accueil sombre, redimensionnement compact et absence de débordement horizontal.
4. **Instance unique et mémorisation** : réduction de la fenêtre, second lancement qui rappelle l’instance existante, ajustement à 110 % par raccourci, fermeture normale et vérification des réglages sauvegardés.
5. **Troisième lancement** : restauration du zoom à 110 % dans un nouveau processus, fermeture normale et vérification que le ZIP d’origine est inchangé.

Les trois fermetures passent par le dialogue de fermeture entre la page et l’hôte. Le harnais attend aussi jusqu’à 15 secondes l’arrêt des enfants WebView avant de réutiliser le profil avec un nouveau port CDP.

## Isolation et nettoyage

Le harnais crée un dossier marqué `GLPI-Pilot-Checks-Native-*` sous le répertoire temporaire, y extrait le pack et y crée les réglages synthétiques. Il transmet explicitement `GLPI_PILOT_TEST_MODE=1` et `GLPI_PILOT_TEST_ROOT` ; l’application n’accepte cette redirection que pour un dossier temporaire autorisé. Le profil WebView2 et l’extraction des bibliothèques .NET restent dans ce périmètre.

Le port de débogage est passé par `--test-port` ; l’hôte ne l’active que dans ce profil de test, avec l’adresse loopback `127.0.0.1`. Aucun réglage ou profil utilisateur existant n’est déplacé, remplacé ou relié par une jonction. Les automatismes de collecte sont désactivés et le portail synthétique utilise `example.invalid`.

Dans `finally`, le harnais ferme ses processus et les enfants dont l’exécutable appartient à son dossier temporaire, copie les diagnostics, puis supprime ce dossier seulement si son marqueur d’appartenance correspond. Il inclut les familles `msedgewebview2`, `node`, `notification_helper` et `platform_experiences_helper`.

## Délais, sorties et portée

Le délai interne global est de **5 minutes**. La CI ajoute un délai externe de **6 minutes** pour l’exécution du harnais. Les attentes CDP, de page, de fermeture et d’arrêt des enfants sont également bornées. Le code de sortie est `0` uniquement après réussite de l’ensemble ; un argument invalide, un environnement inadapté ou un échec produit `1`.

Le résumé attendu a cette forme, avec le nombre de contrôles réellement terminés :

```text
N/N native Windows WebView checks passed; isolated profile, no GLPI session accessed.
```

Le dossier de preuves reçoit `native-webview.log`, les diagnostics de démarrage et, si les étapes correspondantes ont abouti, `dashboard-light.png` et `home-dark.png`. La CI conserve aussi `windows-startup-build.log` et `windows-startup-checks.log`.

Ce parcours valide le pack et son hôte Windows. Les formulaires métier détaillés sont couverts séparément par les suites [TerrainBridgeChecks](../TerrainBridgeChecks), [TerrainWebChecks](../TerrainWebChecks), [WorkplaceBridgeChecks](../WorkplaceBridgeChecks) et [AnalyticsBridgeChecks](../AnalyticsBridgeChecks). Le portail privé, son SSO, la veille et les écrans physiques nécessitent leurs propres essais.
