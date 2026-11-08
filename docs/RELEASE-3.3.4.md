# GLPI Pilot 3.3.4 — Cartes intégrées, animations et météo prête à régler

## Cartes directement dans l’application

Les sites et les tournées s’affichent dans une carte interactive intégrée à l’écran GLPI Pilot. Aucun compte, clé API ou navigateur externe n’est nécessaire. Leaflet 1.9.4 et les ressources de rendu sont fournis dans le pack.

- **Sites** : le bouton **Carte des sites du référentiel** montre l’ensemble des lieux enregistrés. **Voir la carte** sur une ligne ouvre ce site. La carte générale du référentiel ne représente pas le stock filtré d’une équipe.
- **Référentiel géographique** : les cartes des sites et communes utilisent aussi le brouillon courant.
- **Tournées** : **Carte · ordre des communes** montre le regroupement courant ; **Carte de la tournée** conserve les étapes numérotées, séparées par journée et affectation.

La liste des lieux et les marqueurs sont synchronisés. Le zoom, le déplacement et **Tout afficher** permettent de parcourir les sites. Les adresses et coordonnées restent celles du référentiel. Une position manquante est signalée dans la liste, sans utiliser le centre de sa commune comme position du site.

Les pointillés montrent l’ordre des visites **à vol d’oiseau**. Ils s’interrompent aux étapes non localisées et ne sont pas présentés comme un trajet routier. La carte ne modifie ni les étapes, ni les temps du planning. Les longs parcours conservent toutes leurs étapes dans la même vue, sans limite de lien Google.

### Fond local et rues

Le fond terrestre Natural Earth, les repères et les parcours locaux fonctionnent sans Internet. Ce fond à petite échelle ne contient pas de rues.

En usage normal, ouvrir une carte charge les images OpenStreetMap dans la même vue. En **simulation CSV**, le fond local est utilisé par défaut. **Afficher les rues** permet d’activer le fond détaillé avec Internet ; **Fond hors ligne** revient au fond local. Si les images ne sont pas accessibles, les lieux restent visibles sur le plan local.

Les marqueurs, leurs noms et les adresses restent locaux. Le serveur de tuiles reçoit les indices des images visibles et les métadonnées HTTP habituelles, sans contenu de ticket ni nom d’agent. Il n’y a pas de téléchargement préalable de ville ou de région. Le cache HTTP et l’attribution OpenStreetMap sont conservés.

Les anciens réglages de clé Google et les actions d’ouverture externe ont été retirés. Une ancienne clé protégée encore présente dans le profil n’est plus lue ni utilisée.

## Mon espace et animations

La section principale du menu est renommée **Mon espace**, tout en conservant sa clé, les épingles, les rangements et les masquages personnels.

Le thème inspiré de Domenia ajoute des apparitions progressives des cartes et des retours visuels lors des actions et chargements. Les chiffres affichés restent les valeurs réelles. Les effets n’altèrent pas les dimensions des tableaux ni les zones de déplacement et redimensionnement des widgets.

Les préférences de réduction des animations, celles du système, le contraste renforcé et la mise en arrière-plan sont pris en compte. L’information reste disponible quand les animations sont coupées.

## Seuils météo de départ

Les cartes natives de météo de stock utilisent les valeurs suivantes lorsqu’aucune règle personnalisée n’est enregistrée :

| Périmètre | Seuil nuage | Seuil pluie |
| --- | ---: | ---: |
| Stock global | 100 | 200 |
| Incidents ou demandes | 50 | 100 |
| Chaque catégorie ou équipe | 10 | 20 |

Le franchissement reste **strict**, comme dans le classeur : pour le stock global, 100 reste soleil, 101 devient nuage, 200 reste nuage et 201 devient pluie. Ces valeurs sont des repères de démarrage à adapter au service ; elles ne constituent pas des engagements de service.

Les réglages sont accessibles dans **Paramètres du pilotage**. Un profil de classeur ou une règle déjà enregistrée garde ses valeurs, y compris zéro ou une case volontairement vide. **Remplir les seuils vides** permet de compléter explicitement les seuls champs absents d’un profil déjà configuré.

La météo de stock reste distincte de l’indice de risque, du contrôle de fraîcheur et des prévisions météorologiques de tournée. Une colonne absente ou une donnée non fiable reste signalée. Un fichier CSV ne crée ni historique ni mesure manquante.

## Mise à jour

Extraire le pack PROD complet, avec **GLPI-Pilot.exe** et **wad** côte à côte. Le profil utilisateur et les sites déjà importés sont conservés. Le fichier facultatif `wad/sites.csv` reste une donnée indépendante ; son mécanisme d’initialisation et d’enrichissement n’est pas modifié par cette version.

Les packs PROD et DEV sont publiés seulement après réussite du workflow Windows et PostgreSQL du commit. Le [rapport de vérification](Rapport-verification.txt) détaille les contrôles locaux et leurs limites ; les journaux de la [construction Windows](https://github.com/M4D135/GLPI-Pilot/actions/workflows/windows.yml) établissent la validation native du pack publié.

## Sources des composants cartographiques

- [Leaflet — distribution officielle et empreintes](https://leafletjs.com/download.html) ; version stable 1.9.4 embarquée, licence BSD-2-Clause fournie.
- [OpenStreetMap — politique des tuiles](https://operations.osmfoundation.org/policies/tiles/) et [attribution](https://www.openstreetmap.org/copyright).
- [Natural Earth — domaine public](https://www.naturalearthdata.com/about/terms-of-use/) ; source exacte et transformation du fond local documentées dans les licences du pack.
