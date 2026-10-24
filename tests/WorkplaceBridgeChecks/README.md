# Lecteur, travail personnel et techniciens dans WebView

Cette suite compile les moteurs et handlers C# du domaine Workplace puis charge les modules de `web/js/pages/workplace*.js` dans Chromium. Elle ne dépend pas de WPF.

```sh
dotnet run --project tests/WorkplaceBridgeChecks/WorkplaceBridgeChecks.csproj -c Release -p:NuGetAudit=false
```

Ajoutez un Chromium local pour les parcours de page et les formulaires natifs synthétiques :

```sh
GLPI_TEST_CHROMIUM=/chemin/vers/chrome dotnet run --project tests/WorkplaceBridgeChecks/WorkplaceBridgeChecks.csproj -c Release -p:NuGetAudit=false
```

Le programme indique explicitement les parcours ignorés lorsque ce navigateur n’est pas fourni. Les fichiers sont confinés à des profils temporaires de test. Les destinations réseau des fixtures sont interceptées et aucun portail d’entreprise n’est utilisé.

## Couverture

Les contrôles portables vérifient la lecture, les limites et la persistance, les identités séparées par installation, les favoris, les positions, les observations datées, les notes et modèles, les rappels, les files d’enrichissement, les déclarations de calendrier et les propositions de répartition. Ils vérifient également deux courses d’annulation et de fermeture dans le pont JSON réel.

Les parcours Chromium utilisent les pages et handlers réels pour les notes, filtres restaurés, brouillons, fermeture immédiate d’un éditeur, cellules longues rendues comme texte, calendriers avec champs typés, propositions locales, restauration d’une file sans lecture automatique, modèles de Mon espace et refus des liens non autorisés.

Les actions GLPI utilisent des formulaires synthétiques et une vraie soumission navigateur. Les assertions vérifient l’absence de mutation à l’aperçu, le reçu écrit avant le POST, la confirmation requise, l’envoi unique, la relecture et l’absence de relance d’un résultat ambigu. Les requêtes et la session restent entièrement synthétiques.

Les autres domaines sont exclus du registre de ce harnais ciblé. La composition de l’ensemble des modules, les révisions de session et la fermeture passent aussi par [RuntimeSessionChecks](../RuntimeSessionChecks/README.md) et `tests/WebViewChecks`. Les contrôles de l’exécutable Windows publié appartiennent à `tests/WindowsStartupChecks`.
