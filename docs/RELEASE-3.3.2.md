# GLPI Pilot 3.3.2 — Lieux, sites et termes de détection

Cette version permet de préparer les sites d’une distribution avec un simple fichier facultatif `wad/sites.csv`. L’application les reprend discrètement dans le profil local au premier lancement concerné. Elle affiche aussi tous les termes de détection des sites et corrige la reprise du référentiel après l’import d’un profil de classeur.

## Voir les termes réellement reconnus

Dans **Lieux et sites**, sélectionner le type **Site** affiche une colonne **Termes de détection** contenant le nom canonique du site et ses alias. Le formulaire rappelle que le nom du site est reconnu automatiquement ; **Alias et variantes** sert à ajouter ses autres termes.

Les formulaires de villes et de bâtiments gardent leur fonctionnement. Un site peut être enregistré sans commune et rattaché plus tard en conservant son identité.

## Préparer les sites d’une distribution

Le logiciel et le pack public restent génériques. Pour une distribution privée, un fichier de données **`sites.csv`** peut être placé dans le dossier **`wad`**, à côté des autres ressources du pack :

| Élément | Rôle |
| --- | --- |
| `GLPI-Pilot.exe` | Application générique |
| `wad/` | Ressources habituelles du pack |
| `wad/sites.csv`, facultatif | Noms et termes de détection à reprendre dans le profil utilisateur |

Préparer un CSV **UTF-8**, avec l’en-tête **`Site;Termes de détection`**. À l’intérieur d’une cellule, les termes se séparent par `|`, `;` ou une nouvelle ligne. Les cellules contenant le séparateur du fichier doivent être citées conformément au format CSV. Exemple fictif :

```csv
Site;Termes de détection
Atelier exemple;"AX | Atelier annexe"
Bureau exemple;"BX"
```

Un tableau Excel peut être enregistré dans ce format CSV. Le nom du site est obligatoire ; ses termes supplémentaires peuvent être vides. Le nom canonique est déjà reconnu et n’a pas besoin d’être répété dans la seconde colonne.

Les données propres à une organisation sont préparées séparément pour sa distribution privée et restent hors du dépôt public. Aucune liste d’organisme n’est compilée dans l’application ni incluse dans le pack public.

## Initialisation discrète, une fois par profil

Lancer normalement **GLPI-Pilot.exe**. Lors du premier démarrage où `wad/sites.csv` est présent et valide, l’application réalise elle-même l’import dans `%LOCALAPPDATA%\GLPI-Pilot\settings.json`, section `Cities.Sites`. Cette initialisation native ne lance pas de script PowerShell.

Un repère d’initialisation est enregistré avec les sites dans le profil utilisateur. L’import automatique n’est ensuite plus rejoué sur ce profil, même si le CSV fourni est modifié. Les éditions, rattachements et suppressions réalisés par l’utilisateur restent donc conservés aux démarrages suivants.

**En l’absence de `wad/sites.csv`, le démarrage se poursuit normalement.** Aucun repère n’est enregistré ; les sites pourront être initialisés au premier lancement où le fichier sera disponible.

### Fusion et conservation des réglages

Les sites deviennent des réglages locaux persistants du compte Windows courant, consultables et modifiables dans l’éditeur habituel. Ils restent dans le profil lors d’une mise à jour du logiciel.

- Un nom normalisé doit correspondre à un site existant unique. Son identifiant, son rattachement et ses autres propriétés sont conservés ; les nouveaux termes complètent ses alias.
- Un site nouveau reçoit son propre identifiant et reste sans commune renseignée.
- Les sites absents du CSV sont conservés. Les autres rubriques des réglages sont préservées.
- Les termes équivalents selon la normalisation du moteur — casse, accents et ponctuation — sont dédoublonnés. Le nom canonique n’est pas ajouté comme alias supplémentaire.
- Une sauvegarde complète des réglages est conservée dans `%LOCALAPPDATA%\GLPI-Pilot\SettingsVersions` avant leur remplacement. Les sites et le repère d’initialisation sont enregistrés ensemble.
- Une entrée invalide, une correspondance ambiguë ou une erreur de lecture laisse le profil antérieur intact. L’application démarre normalement avec un diagnostic discret, sans exposer les noms du fichier. Une tentative refusée ne consomme pas l’initialisation.

### Parcours existants

Le bouton **Importer un CSV…** de l’interface conserve son format structuré de villes, sites et bâtiments, son comparatif puis sa fusion dans le brouillon. Aucun import Excel supplémentaire n’est ajouté à cet écran dans cette version. L’import Excel des incidents en simulation et la reprise d’un profil de pilotage restent disponibles par leurs parcours existants.

## Actualiser le référentiel après un profil de classeur

Lorsqu’un profil de classeur change le référentiel enregistré, un brouillon qui n’a pas été modifié se remet automatiquement à jour lors de sa prochaine consultation. Les sites importés apparaissent ainsi dans **Lieux et sites**.

Un brouillon déjà modifié est conservé. Le conflit avec le nouveau référentiel reste signalé ; l’application demande de recharger le brouillon avant de poursuivre son application. Cette distinction évite de perdre les modifications en cours.

## Vérifications et publication

Les validations locales établies pour cette correction sont :

| Contrôle | Résultat |
| --- | --- |
| Parcours d’interface terrain avec le backend C# réel | **21/21** |
| Rafraîchissement du brouillon du référentiel après changement des réglages | **10/10** |
| Initialiseur natif du fichier de sites, inclus dans ConfigurationChecks | **14/14** |
| Configuration et conservation des réglages | **62/62** |
| Compilation du backend utilisé par le parcours terrain | **0 erreur, 0 avertissement** |
| Compilation croisée du parcours de démarrage Windows | **0 erreur, 0 avertissement** |

Le parcours terrain vérifie notamment l’intitulé de la colonne, l’affichage du nom et des alias, l’aide du formulaire, l’import CSV existant et la conservation de l’identité lors d’un rattachement ultérieur. Les contrôles de brouillon distinguent la reprise des réglages enregistrés et la conservation d’un travail déjà modifié.

L’initialiseur du CSV et les réglages ont passé leurs contrôles locaux. Le parcours WindowsStartup comporte **27 contrôles préparés** ; sa compilation croisée ne constitue pas leur exécution dans le véritable hôte Windows. Cette exécution et celle des packs font partie de la validation du commit livré.

Les packs PROD et DEV sont publiés après réussite du workflow Windows et PostgreSQL correspondant ; le commit inscrit dans `wad/version.json`, ses journaux et ses inventaires d’intégrité font foi. Les résultats locaux ci-dessus ne constituent pas une attestation de publication. Voir le [rapport de vérification](Rapport-verification.txt).

Le renommage **Mon espace** reste décrit dans les [notes 3.3.1](RELEASE-3.3.1.md), et le pilotage natif avec simulation globale dans les [notes 3.3.0](RELEASE-3.3.0.md).
