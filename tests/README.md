# Vérifier GLPI Pilot

La chaîne de référence est [Windows](../.github/workflows/windows.yml). Le script [Invoke-WindowsCi.ps1](../build-tools/Invoke-WindowsCi.ps1) contrôle codes de sortie, résumés de réussite et délais des processus. Un simple build depuis Linux ne remplace pas les étapes Windows.

## Moteurs et fichiers locaux

Avec le SDK .NET 10, chaque projet console s’exécute ainsi :

```sh
dotnet run --project tests/CompletionChecks/CompletionChecks.csproj -c Release
```

Les dix-huit suites métier sont : StartupDiagnosticsChecks, StartupResilienceChecks, FoundationChecks, WorkplaceChecks, ConfigurationChecks, DashboardChecks, DashboardLayoutChecks, PreviewQueryChecks, TechnicianWorkloadChecks, CityDetectionChecks, TourPlanningChecks, QueryChecks, NativeImportChecks, ExportChecks, AnalyticsChecks, CompletionChecks, UpdateChecks et LanguageAssistantChecks.

Les dernières suites vérifient notamment :

- **AnalyticsChecks** : comparabilité, ruptures, cohortes, mouvements, scénarios, index, effectifs datés et reconstruction des périmètres historiques.
- **CompletionChecks** : isolation des profils/identités, points de reprise, échantillon de valeurs, horaires, recettes, santé des sources, alertes de variation, conflits de configuration et ordonnanceur.
- **UpdateChecks** : provenance et inventaire des ZIP, chemins Windows, intégrité, politique de signature, préparation et restauration sur fichiers synthétiques.
- **LanguageAssistantChecks** : contexte choisi, DSL borné, citations exactes, rejet des sorties incorrectes et du contexte périmé. Les fournisseurs sont simulés, sans appel externe.

Les tests créent leurs fichiers dans des répertoires temporaires. Les preuves finales et nombres de contrôles sont consignés dans [Rapport-verification.txt](../docs/Rapport-verification.txt) et les journaux du commit livré.

## Navigateur isolé

BrowserChecks, AutomationChecks et TicketDetailChecks utilisent Chromium sur des routes synthétiques. `GLPI_TEST_CHROMIUM` peut désigner l’exécutable Chromium disponible. Les contrôles DOM complémentaires s’exécutent avec `tests/NativeDomChecks/run.cjs` et le module Playwright du build.

La collecte couvre expiration SSO, réseau interrompu, reprise contrôlée, portée modifiée, pagination, téléchargements invalides et échantillon dont les identifiants concordent mais pas les cellules. Les formulaires d’affectation, suivi et tâche sont des pages de test ; aucun GLPI réel n’est modifié.

## Windows et packs

Exécuter les étapes dans l’ordre depuis PowerShell 7.2 ou plus :

```powershell
./build-tools/Invoke-WindowsCi.ps1 -Stage BclChecks
./build-tools/Invoke-WindowsCi.ps1 -Stage BrowserChecks
./build-tools/Invoke-WindowsCi.ps1 -Stage WpfChecks
./build-tools/Invoke-WindowsCi.ps1 -Stage BuildPack
./build-tools/Invoke-WindowsCi.ps1 -Stage PackStartup
./build-tools/Invoke-WindowsCi.ps1 -Stage UpdateStartup
```

**WpfChecks** exige un bureau Windows interactif. Il charge les vrais contrôles et vérifie thèmes, tailles/zooms, dashboards, lecteurs, champs, calendriers, correspondances, conflits, tableaux, cartes, assistant et exports PDF/PNG. Les captures utilisent exclusivement des données synthétiques.

**PackStartup** et **UpdateStartup** sont réservés au runner Windows GitHub hébergé et refusent une installation ou des données GLPI Pilot préexistantes. Ils utilisent une copie isolée du pack PROD. Le premier contrôle teste lancement, instance unique et zoom persistant. Le second teste refus d’un processus actif, EXE verrouillé, remplacement réel avec acquittement du premier rendu, puis retour arrière après échec de démarrage provoqué. La restauration bloquée conserve les sauvegardes et le diagnostic.

Les packs ne sont promus en Release qu’après réussite des étapes. Chaque ZIP possède `wad/version.json`, son inventaire SHA-256 et le même EXE que l’autre édition. Le certificat Authenticode demeure un prérequis externe facultatif ; absence de certificat signifie pack non signé.
