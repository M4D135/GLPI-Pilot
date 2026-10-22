# Régressions de session, de sélection et de fermeture

Ce programme portable compile les sources réelles `Core` et `Runtime`, sans dépendance WPF. Chaque scénario utilise un profil temporaire distinct, activé explicitement par `GLPI_PILOT_TEST_MODE` et `GLPI_PILOT_TEST_ROOT`. Aucun profil ni fichier utilisateur n’est lu ou modifié.

```sh
dotnet run --project tests/RuntimeSessionChecks/RuntimeSessionChecks.csproj -c Release -p:NuGetAudit=false
```

Pour inclure les parcours de navigateur et le vrai éditeur WebView, indiquez un Chromium de test local :

```sh
GLPI_TEST_CHROMIUM=/chemin/vers/chrome dotnet run --project tests/RuntimeSessionChecks/RuntimeSessionChecks.csproj -c Release -p:NuGetAudit=false
```

Sans cette variable, les scénarios Chromium sont explicitement ignorés. Le navigateur reste limité aux fixtures `session-glpi.test` et `session-ui.test` : toutes les requêtes sont interceptées et les autres destinations sont refusées.

## Ce qui est contrôlé

- Changement de recherche ou de profil, modification du profil actif, capture explicite de ses réglages et refus des formulaires périmés.
- Isolation des imports et des archives, correspondances locales, retrait des localisations devenues périmées et conservation de l’interprétation lors d’un nouvel enrichissement.
- Application réelle du référentiel terrain et d’une correction locale sans adopter le mapping d’un autre profil.
- Bibliothèque : premier accès sans table précréée, dates avec fuseau explicite et ambiguïtés de calendrier, copie SQLite cohérente vers un nouveau dossier, ouverture d’un cache existant sans l’écraser, refus des chemins réseau, secret de session sans retour au WebView, publication et aperçu des sections de configuration, exports protégés et installations GLPI distinctes.
- Bilan de capture : journal durable après interruption, diagnostics fixes paginés et refus d’une reprise après changement de source avant toute navigation. Avec Chromium, trois parcours utilisent le vrai formulaire GLPI synthétique et le lecteur de fiches : échec/lecture partielle puis reprise des seuls tickets encore accessibles, échec du contrôle final puis nouvelle confirmation, et annulation après la dernière fiche suivie d’une reprise explicite. Les contenus en attente restent consultables mais sont exclus des solutions confirmées ; le navigateur conserve la page de recherche et son onglet dédié de consultation.
- Outils partagés : huit scénarios de publication/reprise avec le vrai dispatcher, SQLite et les fichiers locaux. Ils couvrent les notes enregistrées sans bibliothèque préaspirée, l’ajout sans écrasement privé, les jetons périmés après modification locale/partagée/de contexte, les documents mal formés ou d’une autre installation, la tournée reprise en nouveau brouillon, la sauvegarde exacte du scénario précédent, les compétences/capacités sans préférences privées, les limites et l’annulation.
- Indices, vues et sélections obsolètes ; copie des paramètres JSON d’une opération mise en file.
- Annulation des opérations actives, en attente et programmées ; place de contrôle réservée lorsque la file du pont est pleine.
- Arrêt de l’admission, attente des nettoyages puis fermeture du vrai contexte Chromium.
- Inspection d’un onglet choisi après fermeture d’un autre onglet, changements de recherche et générations de sélection de tableau.
- Extraction réelle d’une page synthétique, sans attribuer à une lecture manuelle des preuves de recherche ou de contexte qu’elle n’a pas établies.
- Chargement du module `workplace-actions.js` et appels aux handlers C# réels : annulation d’une sauvegarde qui attend derrière une collecte, sauvegarde du dernier texte avant fermeture, échec réel d’écriture et reprise de l’éditeur.

Les scénarios de concurrence utilisent des tâches contrôlées pour rendre l’ordre déterministe. Un seul prédicat de disponibilité réseau est remplacé dans le scénario de programmation ; il évite les dépendances aux interfaces réseau de la machine, et le traitement programmé synthétique ne fait aucun accès réseau. Les scénarios de fichiers utilisent les moteurs d’enregistrement réels.

Les reprises d’outils partagés ne lancent aucune action GLPI. Le parcours WebView des boutons de publication et de comparaison est vérifié séparément par `WebViewChecks --shared-operations-only` ; la suite ci-dessus exerce notamment les refus et la conservation des données privées.

Ces contrôles n’établissent pas la compatibilité d’un portail GLPI particulier et ne remplacent pas l’exécution de l’hôte WebView2 sous Windows.
