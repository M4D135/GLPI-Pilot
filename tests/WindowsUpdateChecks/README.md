# Remplacement et retour arrière réels sous Windows

Ce harnais utilise le pack PROD déjà construit, son **véritable EXE** et son auxiliaire de maintenance pour exercer les comportements de processus et de verrouillage de fichiers Windows. Il n’effectue aucun téléchargement et ne cible aucune installation existante. Les moteurs de production [UpdateService](../../src/GlpiPilot/Core/UpdateService.cs) et [UpdateService.Transaction](../../src/GlpiPilot/Core/UpdateService.Transaction.cs) sont liés au projet.

Sa compilation ne constitue pas une exécution Windows. Le résultat du commit livré doit être lu dans les journaux de [UpdateStartup](../../build-tools/Invoke-WindowsCi.ps1) et le [rapport de vérification](../../docs/Rapport-verification.txt).

## Prérequis et commande de référence

Il faut Windows x64, une session de bureau interactive hors session 0, le pack PROD complet et plusieurs gigaoctets libres pour les copies de travail. Le harnais refuse de démarrer si un processus `GLPI-Pilot` est déjà ouvert.

Depuis la racine du dépôt, après construction et contrôle du pack :

```powershell
.\build-tools\Invoke-WindowsCi.ps1 -Stage UpdateStartup
```

Cette étape publie le harnais dans `artifacts/windows-ci/update-harness`, puis lui passe l’archive PROD exacte du run.

Exécution directe avec une archive déjà construite :

```powershell
dotnet publish tests/WindowsUpdateChecks/WindowsUpdateChecks.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -o .\artifacts\update-harness

.\artifacts\update-harness\WindowsUpdateChecks.exe `
  .\artifacts\windows-ci\release\GLPI-Pilot_3.0.0_PROD_Windows-x64.zip `
  .\artifacts\windows-update-evidence
```

Le harnais attend exactement deux arguments : le ZIP PROD et le dossier de preuves. Adapter les chemins à la construction. La publication du harnais nécessite le SDK .NET 10 et ses dépendances ; dans un environnement hors ligne, effectuer d’abord la restauration depuis les sources locales autorisées, puis publier avec `--no-restore`. Les commandes du laboratoire sont décrites dans [GitHub et compilation](../../docs/GitHub-et-compilation.txt).

## Les quatre contrôles

| Contrôle | Comportement exigé |
| --- | --- |
| Application ouverte | Un véritable processus lancé depuis la copie du pack empêche le remplacement avant toute sauvegarde ; l’inventaire initial reste identique. |
| Fichiers verrouillés | Un EXE tenu ouvert sans partage de suppression empêche son remplacement. Un second cas bloque le retour arrière après le lancement simulé de la sonde : le résultat doit être `recovery-needed`, avec les anciens binaires et la sauvegarde indépendante conservés. |
| Démarrage sain | Le véritable auxiliaire installe le pack vérifié, lance l’EXE et reçoit l’acquittement de l’hôte après bootstrap et premier affichage WebView. La transaction devient `installed`, l’application reste ouverte et la sauvegarde est vérifiée. |
| Acquittement absent | Un dossier placé à l’emplacement du reçu empêche son écriture. L’auxiliaire doit arrêter le nouveau processus, rétablir les fichiers précédents, marquer `rolled-back` et relancer le pack antérieur. |

Les deux versions de fixture utilisent **le même EXE actuel**. La copie dite précédente possède des métadonnées de version `0.0.1` et un document propre à la fixture, avec un inventaire recalculé. Cela vérifie le remplacement et la restauration du pack entier ; cela ne prétend pas valider une migration de données entre deux véritables versions historiques.

Un fichier racine `personal-export.csv`, extérieur à l’inventaire du produit, doit être conservé. Le reçu de démarrage est contrôlé par nonce, version et PID. La politique de signature vérifie un état Windows explicite `Valid` ou `NotSigned` ; le harnais ne signe pas l’application et ne fournit aucun certificat.

## Isolation et preuve

Chaque exécution crée un dossier marqué `GLPI-Pilot-Checks-Update-*`. Le profil synthétique est un sous-dossier temporaire explicitement autorisé par `GLPI_PILOT_TEST_MODE=1` et `GLPI_PILOT_TEST_ROOT`. Aucun profil SSO ou réglage personnel existant n’est utilisé, déplacé ou remplacé.

Les extractions sont bornées et leurs chemins vérifiés. Les états, fichiers de transaction et sauvegardes restent dans ce périmètre. Le programme contrôle en fin d’essai les empreintes des réglages synthétiques et du ZIP original.

Pour lancer l’auxiliaire, un processus `cmd.exe` de fixture déjà terminé représente la fermeture ordonnée de l’ancienne application ; son PID et son heure de création sont inscrits dans la transaction. L’auxiliaire conserve ses contrôles réels de provenance et de processus parent.

Les preuves comprennent `windows-update-fixture.log`, les diagnostics de l’application et, pour chaque scénario ayant atteint cette étape, `transaction.json`, `outcome.json` et `health.json`. La CI conserve également `windows-update-build.log` et `windows-update-checks.log`.

## Temps, disque et nettoyage

La limite interne est de **15 minutes** et la limite externe de CI de **16 minutes**. Chaque véritable auxiliaire dispose de **300 secondes**. La sonde de santé du moteur attend jusqu’à **210 secondes** pour couvrir l’initialisation du runtime fixe, du contrôle et du premier affichage. Le scénario d’acquittement absent consomme volontairement ce délai.

Le runtime fixe représente environ 700 Mo décompressés. Installation, staging, sauvegarde et copie intermédiaire peuvent occuper plusieurs gigaoctets dans un seul cas. Le harnais nettoie les copies terminées **entre les scénarios**, y compris entre les deux cas de verrouillage ; il ne conserve que les petits diagnostics dans le dossier de preuves.

Dans `finally`, les auxiliaires sont arrêtés avant leurs enfants. Seuls les processus dont l’exécutable appartient aux dossiers marqués de la fixture sont ciblés ; les familles runtime incluent `msedgewebview2`, `node`, `notification_helper` et `platform_experiences_helper`. Les dossiers ne sont supprimés qu’après contrôle du marqueur et absence de liens de fichiers.

La réussite exige un code de sortie `0` et exactement :

```text
4/4 Windows update checks passed.
```

Une erreur d’environnement, de contrôle ou de nettoyage produit `1`. Ce protocole complète [UpdateChecks](../UpdateChecks), qui exerce les archives et transactions synthétiques hors ligne, et [WindowsStartupChecks](../WindowsStartupChecks/README.md), qui pilote la fenêtre et ses contrôles. Il ne valide ni un fournisseur externe ni le portail GLPI privé.
