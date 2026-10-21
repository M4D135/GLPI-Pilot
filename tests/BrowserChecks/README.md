# Vérifications d’intégration du navigateur

Ce programme exécute le véritable `BrowserService` et l’adaptateur JavaScript avec Microsoft.Playwright. Un navigateur Chromium de test doit être disponible ; son chemin est fourni par `GLPI_TEST_CHROMIUM`.

Les pages et le téléchargement CSV sont servis par interception Playwright sur `http://glpi.test`. Aucun portail distant ni compte réel n’est utilisé.

```powershell
$env:GLPI_TEST_CHROMIUM = 'C:\chemin\chrome.exe'
dotnet run --project tests/BrowserChecks/BrowserChecks.csproj -c Release
```

Les cas couvrent une page courante, trois pages avec réconciliation du total, une pagination bloquée, un délai de clic dépassé, l’annulation après une page, l’annulation du sélecteur par Échap, une pagination ambiguë, un bouton métier « Suivant » hors pagination, un sélecteur personnalisé dangereux, une page demandant un mot de passe et la réception effective d’un export CSV natif.

Les options Chromium désactivant le sandbox concernent uniquement ce programme de test en environnement isolé. Le logiciel Windows utilise le navigateur réel avec son sandbox activé.

Ces cas ne valident pas le SSO d’entreprise réel ni le rendu WPF sous Windows.
