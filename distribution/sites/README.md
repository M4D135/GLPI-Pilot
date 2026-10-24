# Fichier de sites

Ce complément contient **26 sites et leurs termes de détection**, rattachés à **13 communes**, avec adresses et coordonnées. Les deux colonnes d’origine sont conservées. Les données restent dans un CSV séparé et modifiable ; le logiciel utilise un mécanisme d’import générique.

## Téléchargement

- [sites.csv](https://github.com/M4D135/GLPI-Pilot/releases/download/v3.3.3/sites.csv) : le fichier seul.
- [GLPI-Pilot-Sites.zip](https://github.com/M4D135/GLPI-Pilot/releases/download/v3.3.3/GLPI-Pilot-Sites.zip) : les fichiers rangés dans le dossier `wad`, prêts à ajouter à une installation.
- [SITES-SHA256SUMS.txt](https://github.com/M4D135/GLPI-Pilot/releases/download/v3.3.3/SITES-SHA256SUMS.txt) : les empreintes des deux fichiers.

Le [CSV dans le dépôt](wad/sites.csv), la [notice](wad/support/LIRE-SITES.txt) et les [sources et précisions géographiques](wad/support/SOURCES-SITES.md) sont également consultables directement.

## Installation avec GLPI-Pilot 3.3.3 ou ultérieur

1. Fermer complètement GLPI-Pilot, y compris une ancienne version encore ouverte.
2. Extraire `GLPI-Pilot-Sites.zip` dans le dossier qui contient `GLPI-Pilot.exe`. Fusionner son dossier `wad` avec le dossier `wad` existant.
3. Vérifier la présence de `wad/sites.csv`, puis lancer `GLPI-Pilot.exe`.
4. Ouvrir **Terrain → Lieux et sites → Références → Type : Site**.

Le CSV est lu automatiquement au démarrage. Les réglages existants sont sauvegardés avant modification, puis les sites et communes sont enregistrés dans `%LOCALAPPDATA%\GLPI-Pilot\settings.json`, dans `Cities.Sites` et `Cities.Cities`.

Pour un profil neuf, les sites, leurs termes et leur géographie sont ajoutés. Pour un profil ayant déjà reçu le fichier à deux colonnes, cette édition complète seulement les champs vides des sites et communes encore présents. Elle conserve les identifiants, les valeurs locales et les rattachements personnalisés ; elle ne recrée pas les sites supprimés ou renommés et ne réintroduit pas les alias retirés. Un fichier identique n’est pas réappliqué. Un fichier absent laisse l’application démarrer normalement.

Si une instance est déjà ouverte, un nouveau lancement réactive cette instance et n’exécute pas l’initialisation du nouveau dossier. Fermer complètement l’application avant la mise à jour. **Importer ensuite le CSV d’incidents en simulation** reprend ce référentiel enrichi dans l’espace d’essai. La démonstration intégrée sans fichier possède ses propres références fictives.

Les 26 adresses sont renseignées. **La Ciotat conserve une adresse historique**, dont l’accueil a été annoncé fermé en mars 2024 ; l’usage actuel du bâtiment est à confirmer avant une intervention. Les positions désignent un numéro de voie, un lieu publié ou un complexe, sans garantir un portail précis. Les sources et les corrections sont consignées dans la notice géographique jointe.

## Adapter le fichier avant distribution

`wad/sites.csv` est encodé en UTF-8 avec des séparateurs point-virgule. Les colonnes `Site` et `Termes de détection` restent obligatoires. Les colonnes géographiques sont facultatives :

```text
Site;Termes de détection;Ville;Code postal;Adresse;Latitude ville;Longitude ville;Latitude site;Longitude site
```

La deuxième colonne peut contenir plusieurs termes séparés par des points-virgules ; elle doit alors être entourée de guillemets CSV. Le nom du site est reconnu automatiquement et le moteur normalise la casse, les accents et la ponctuation.

Une paire de coordonnées doit être complète, en degrés décimaux. Les valeurs de commune doivent être cohérentes sur toutes ses lignes. Le code postal de commune peut rester vide pour une commune qui en possède plusieurs ; le code propre au site figure dans son adresse. Les coordonnées de commune proviennent ici du point mairie ; les coordonnées de site sont distinctes.

Ce format est destiné à l’initialisation dans `wad`. Le bouton d’import CSV du référentiel utilise son format structuré `Type;Nom;Alias` et son aperçu : utiliser le placement dans `wad` décrit ci-dessus pour ce complément. La version 3.3.2 ne prend pas en charge les nouvelles colonnes géographiques.

## Contrôles du fichier enrichi

Le contrôle d’intégration local a réussi **30/30 vérifications** : installation initiale, enrichissement d’un profil ancien, conservation des identifiants et des 36 alias supplémentaires distincts, sauvegarde et absence de réécriture au second démarrage. Les 26 noms et ces alias retrouvent le site et la commune attendus.

Un CSV fictif à 19 champs construit avec ces références a donné **26 localisations reconnues sur 26 tickets**, une projection sur 13 communes et une tournée de 26 visites avec les adresses et coordonnées du fichier. Le parcours simulé n’a émis aucun appel réseau et a restitué le profil habituel à sa fermeture. Ce contrôle n’est pas une mesure de couverture d’un extrait réel de tickets non fourni.

## Publication du complément

Le workflow `sites.yml` valide le CSV, produit le petit ZIP et ses empreintes, puis joint ces trois fichiers à la version déjà publiée. Les archives binaires PROD et DEV gardent leurs fichiers et leurs empreintes d'origine.

Pour produire le complément localement :

```bash
python3 distribution/sites/package.py --output-dir /tmp/glpi-sites
```
