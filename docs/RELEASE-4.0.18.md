# GLPI Pilot 4.0.18 — un fichier pour Relais

La bibliothèque propose **Exporter pour Relais**. Ce bouton crée un fichier **`.relais`** autonome à choisir directement dans **Relais 0.6.3 ou ultérieur → Importer les connaissances**. Aucun script, Node.js, arrêt de Pilot ou copie manuelle de la base et de ses journaux n’est nécessaire pour cet export.

## Utilisation

1. Dans **Bibliothèque**, vérifier l’installation indiquée sous le bouton **Exporter pour Relais**.
2. Cliquer sur le bouton et enregistrer le fichier proposé. Son nom comporte la date et l’heure ; choisir un fichier nouveau.
3. Lire le bilan : tickets et fragments exportés, fiches non incluses et motifs éventuels. Si aucun contenu n’est admissible, Pilot explique les motifs et ne crée aucun fichier.
4. Dans Relais, ouvrir **Importer les connaissances**, choisir le `.relais`, vérifier les compteurs et importer avec l’audience adaptée. L’audience initiale reste administrateurs uniquement.

Le bouton utilise les contenus déjà collectés. Une nouvelle collecte n’est nécessaire que pour les fiches dont les contenus ou les preuves doivent être complétés. Les filtres de recherche, les pages affichées et le dernier périmètre de collecte ne réduisent pas l’export : il couvre toute la bibliothèque locale admissible de l’installation indiquée. L’import suivant dans Relais remplace le précédent corpus de cette installation.

## Contrôles intégrés

- Une seule lecture cohérente de la source, y compris ses écritures validées encore dans le journal SQLite. Aucun changement des corps, dates, révisions ou confirmations.
- Un fichier créé de zéro avec les fragments admissibles, leurs citations exactes et des compteurs de qualité. La base opérationnelle, les profils, les réglages, sessions, historiques, conflits et données d’équipe ne sont pas copiés.
- Les contenus privés et ceux de visibilité inconnue conservent cette qualification. Le fichier contient des données métier et n’est pas anonymisé ; il reste destiné à un usage interne autorisé.
- Une limite dépassée, une donnée invalide ou une annulation ne produit aucun export partiel. Le fichier n’est publié qu’après fermeture et vérification ; une destination préexistante n’est jamais écrasée.
- Les limites correspondent aux valeurs par défaut de Relais : 200 000 fragments, 256 Mio de JSON normalisé, 2 Mio par corps et 512 Mio pour le fichier. Le calcul conservateur peut refuser un export avant la limite effective de Relais.

Les cinq critères restent obligatoires : contenu présent, complet, confirmé, sans confirmation en attente et sans conflit. L’export ne force aucune preuve et ne certifie pas que tous les tickets du portail GLPI ont été collectés. Les motifs par ticket peuvent se recouper ; leur somme n’est pas un nombre d’erreurs.

Le [contrat de fichier versionné](Format-export-Relais.md) décrit les objets SQLite et la validation. Les anciens exports JSONL et les transferts de bibliothèque entre postes restent distincts.

## Mise à jour et données existantes

Fermer les deux lanceurs, conserver une sauvegarde du profil `%LOCALAPPDATA%\GLPI-Pilot` et du dossier de bibliothèque configuré, puis extraire le nouveau pack dans un dossier d’application distinct. Garder `GLPI-Pilot.exe`, `GLPI-Pilot-Admin.exe` et `wad/` ensemble. Ouvrir avec le même compte Windows : les réglages et la bibliothèque existants sont réutilisés.

Il n’est pas nécessaire de réinitialiser la bibliothèque pour utiliser cet export. Si le bilan signale des contenus non confirmés, consulter les [contrôles de collecte et de dates](RELEASE-4.0.17.md) avant une reprise ciblée.

## Qualification

Les essais utilisent des bases et des tickets fictifs, les vraies vues SQL de Pilot, l’exporteur C# et le lecteur Relais. Ils couvrent le journal WAL actif, la lecture concurrente cohérente, les limites, l’annulation, les destinations existantes et le respect des citations, identités et visibilités. La qualification Windows complète a réussi **16/16 jobs**. Le ZIP original a été publié, retéléchargé et comparé octet par octet. Le [reçu de qualification et publication](QUALIFICATION-4.0.18.md) identifie le commit, les contrôles et les fichiers distribués.

La base privée et le portail de l’entreprise n’ont pas été ouverts dans cette qualification.
