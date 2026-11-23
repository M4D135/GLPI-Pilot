# Publication des preuves de qualification

Depuis l'arrêt du banc privé, la qualification (`.github/workflows/windows.yml`) tourne sur les runners
hébergés de GitHub (`windows-2025`, `ubuntu-latest`). Ces machines sont effacées après chaque job : rien
n'est gardé sur le runner, tout ce qui sert à la publication est joint au run.

## Ce que produit le job Windows

Le job `Windows x64 · contrôles réels et packs` :

1. scelle le ZIP PROD-LIGHT (seul pack publié depuis 3.14.0) avec `build-tools/retain-ci.py`, qui écrit
   dans le journal officiel du job un reçu `GLPI_CI_ORIGINAL` : dépôt, run, tentative, commit, job,
   taille et SHA-256 ;
2. joint ce même ZIP au run, **sans enveloppe ZIP** (`archive: false`), pendant 7 jours : l'empreinte de
   l'artefact est celle du pack lui-même.

Les journaux et captures de chaque job sont joints au run pendant 14 jours par l'action
`.github/actions/retain-evidence`.

## Publication

`publish-verified-release.yml`, lancé à la main sur `main` avec le run, sa tentative et l'empreinte du
ZIP PROD-LIGHT téléchargé et inspecté :

1. vérifie que le run est complet et vert sur le commit exact, que le job Windows a tourné sur le runner
   hébergé `windows-2025` et que son reçu correspond à l'empreinte inspectée
   (`build-tools/select-verified-release.py --stored`) ;
2. retrouve l'artefact du run portant le nom du pack, le commit et la même empreinte, puis le télécharge
   octet pour octet ;
3. place dans le brouillon le ZIP versionné, son empreinte et une copie identique nommée
   `GLPI-Pilot_PROD-LIGHT_Windows-x64.zip` pour le bouton du README. Les trois fichiers sont
   retéléchargés et comparés avant publication. Sur le dépôt public, le script télécharge aussi
   le ZIP via `releases/latest/download/` et le compare à l’original qualifié.

L'artefact expire après 7 jours : la publication doit suivre la qualification dans ce délai, sinon il
faut relancer la qualification.
