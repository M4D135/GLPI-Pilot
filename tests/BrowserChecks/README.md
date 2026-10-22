# Vérifications d’intégration du navigateur

Ce programme exécute le véritable `BrowserService` et l’adaptateur JavaScript avec Microsoft.Playwright. Un navigateur Chromium de test doit être disponible ; son chemin est fourni par `GLPI_TEST_CHROMIUM`.

Les pages et le téléchargement CSV sont servis par interception Playwright sur `http://glpi.test`. Aucun portail distant ni compte réel n’est utilisé.

```powershell
$env:GLPI_TEST_CHROMIUM = 'C:\chemin\chrome.exe'
dotnet run --project tests/BrowserChecks/BrowserChecks.csproj -c Release
```

Les cas couvrent une page courante, trois pages avec réconciliation du total, une pagination bloquée, un délai de clic dépassé, l’annulation après une page, l’annulation du sélecteur par Échap, une pagination ambiguë, un bouton métier « Suivant » hors pagination, un sélecteur personnalisé dangereux, une page demandant un mot de passe et la réception effective d’un export CSV natif.

Les contrôles de fiabilité supplémentaires portent sur le prévol de la configuration et du dossier d’export, le verrou exclusif du profil, les changements de critères, d’entité et de total pendant la pagination, et les critères DOM recopiés dans l’adresse par les formulaires GLPI. Les filtres divergents restent détectés ; l’ordre des paramètres, le tri et la taille des pages ne changent pas l’identité du périmètre.

Le parcours automatique est exercé avec un export réellement absent (retour à la première page et pagination), le repli désactivé, une disparition du tableau avec sauvegarde partielle, un échantillon d’identifiants conforme ou divergent, un CSV corrompu, un export ambigu, un reçu incomplet et un contexte modifié après transfert. Un reçu corrompu ou correspondant à d’autres identifiants est conservé intact et provoque un refus explicite, sans repli silencieux. Les identifiants avec séparateurs de milliers sont rapprochés sans altérer les données exportées.

Les contextes de navigateur sont éphémères. Le contrôle de verrou crée un dossier temporaire dédié, sans utiliser le profil de l’application.

Les options Chromium désactivant le sandbox concernent uniquement ce programme de test en environnement isolé. Le logiciel Windows utilise le navigateur réel avec son sandbox activé.

Ces cas ne valident pas le SSO d’entreprise réel ni le rendu WPF sous Windows.
