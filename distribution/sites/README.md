# Fichier de sites

Ce complément contient le référentiel de **26 sites et leurs termes de détection**, repris du tableau fourni. Les données restent dans un CSV séparé et modifiable. Le logiciel utilise un mécanisme d'import générique.

## Téléchargement

- [sites.csv](https://github.com/M4D135/GLPI-Pilot/releases/download/v3.3.2/sites.csv) : le fichier seul.
- [GLPI-Pilot-Sites.zip](https://github.com/M4D135/GLPI-Pilot/releases/download/v3.3.2/GLPI-Pilot-Sites.zip) : les fichiers rangés dans le dossier `wad`, prêts à ajouter à une installation.
- [SITES-SHA256SUMS.txt](https://github.com/M4D135/GLPI-Pilot/releases/download/v3.3.2/SITES-SHA256SUMS.txt) : les empreintes des deux fichiers.

Le [CSV dans le dépôt](wad/sites.csv) et la [notice complète](wad/support/LIRE-SITES.txt) sont également consultables directement.

## Installation avec GLPI-Pilot 3.3.2 ou ultérieur

1. Fermer complètement GLPI-Pilot, y compris une ancienne version encore ouverte.
2. Extraire `GLPI-Pilot-Sites.zip` dans le dossier qui contient `GLPI-Pilot.exe`. Fusionner son dossier `wad` avec le dossier `wad` existant.
3. Vérifier la présence de `wad/sites.csv`, puis lancer `GLPI-Pilot.exe`.
4. Ouvrir **Terrain → Lieux et sites → Références → Type : Site**.

Le CSV est lu automatiquement au démarrage. Les réglages existants sont sauvegardés avant modification, puis les sites sont enregistrés dans `%LOCALAPPDATA%\GLPI-Pilot\settings.json`, dans `Cities.Sites`. L'import s'effectue une seule fois par profil et préserve les modifications ou suppressions ultérieures. Un fichier absent laisse l'application démarrer normalement.

Si une instance est déjà ouverte, un nouveau lancement réactive cette instance et n'exécute pas l'initialisation du nouveau dossier. La démonstration intégrée utilise ses propres références fictives ; quitter cette démonstration pour vérifier le référentiel habituel.

## Adapter le fichier avant distribution

`wad/sites.csv` est encodé en UTF-8 avec les colonnes `Site` et `Termes de détection`, séparées par un point-virgule. La deuxième colonne peut contenir plusieurs termes séparés par des points-virgules ; elle doit alors être entourée de guillemets CSV. Le nom du site est reconnu automatiquement, et le moteur normalise la casse et les accents.

Le fichier est destiné à l'initialisation dans `wad`. En version 3.3.2, le bouton d'import CSV du référentiel utilise un autre format de tableau : utiliser le placement dans `wad` décrit ci-dessus pour ce complément.

## Publication du complément

Le workflow `sites.yml` valide le CSV, produit le petit ZIP et ses empreintes, puis joint ces trois fichiers à la version déjà publiée. Les archives binaires PROD et DEV gardent leurs fichiers et leurs empreintes d'origine.

Pour produire le complément localement :

```bash
python3 distribution/sites/package.py --output-dir /tmp/glpi-sites
```
