# Vérifications de l’adaptateur DOM

Ces 46 vérifications ouvrent uniquement les fixtures HTML du projet. Elles n’utilisent aucun compte, profil de navigation existant, serveur GLPI ou fournisseur SSO. Les contrôles concernent l’extraction des cellules, la pagination, la sélection visuelle et les états d’authentification simulés. Ils couvrent aussi les identifiants GLPI affichés avec des espaces, les en-têtes répétés dans le corps du tableau et le total officiel `data-count`. Ils ne constituent pas une validation du véritable hôte WinForms/WebView2 ou d’un SSO réel.

## Prérequis

Node.js 20 ou supérieur et le paquet npm Playwright déclaré dans `package.json`. Les dépendances npm et les binaires de navigateur ne sont pas livrés dans les sources.

Depuis ce dossier :

```powershell
npm install
```

## Windows : utiliser Edge installé

Dans PowerShell, depuis ce dossier :

```powershell
$env:GLPI_TEST_CHANNEL = 'msedge'
node .\run.cjs .\result-local.log
```

Le navigateur est lancé avec un contexte temporaire isolé. Aucun profil Edge habituel n’est repris.

On peut aussi indiquer explicitement le chemin d’un exécutable Chromium, Chrome ou Edge. Cette variable est prioritaire sur le canal :

```powershell
$env:GLPI_TEST_CHROMIUM = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
node .\run.cjs .\result-local.log
```

Adapter le chemin à l’installation du poste. Il n’est pas nécessaire de télécharger Chromium si le navigateur choisi est disponible.

## Utiliser le Chromium fourni par Playwright

```powershell
npx playwright install chromium
Remove-Item Env:GLPI_TEST_CHROMIUM -ErrorAction SilentlyContinue
Remove-Item Env:GLPI_TEST_CHANNEL -ErrorAction SilentlyContinue
node .\run.cjs .\result-local.log
```

## Linux / macOS avec un navigateur existant

```bash
GLPI_TEST_CHROMIUM=/chemin/vers/chromium node run.cjs result-local.log
```

Les chemins des fixtures sont calculés à partir du script et convertis avec `pathToFileURL`, y compris sous Windows. Le second argument est facultatif et désigne le journal brut à enregistrer ; le script affiche aussi chaque vérification en console. Un échec donne un code de sortie non nul.

Le journal `result-20261021.log` provient d’une exécution locale réelle sur Chromium 138 avec Playwright 1.62.1. Le SSO de production et l’exécutable Windows ne sont pas évalués par ce journal.
