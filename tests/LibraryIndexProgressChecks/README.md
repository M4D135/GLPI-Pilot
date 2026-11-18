# Progression de l’index — contrôles sans navigateur

```sh
dotnet run --project tests/LibraryIndexProgressChecks/LibraryIndexProgressChecks.csproj -c Release
```

Cette suite ne dépend pas de Playwright et ne lance aucun navigateur. Une horloge simulée vérifie qu’une requête native active ne subit aucun délai absolu, que la réception des seuls en-têtes ne vaut pas fin de réponse, et que la période d’inactivité repart à la fin réelle de la requête. La barrière du vrai `DOMContentLoaded`, alimentée uniquement par l’attente du navigateur, reste également active après réception du document et avant exécution des scripts différés. Des observations DOM identiques et des événements provenant de requêtes inconnues ne constituent pas une progression.

La politique de sélection vérifie l’origine et le chemin exacts de l’installation, le GET documentaire natif, les actions de lecture de `ajax/search.php`, l’itemtype `Ticket` et le champ Statut `12`. Les autres profils de requêtes, le polling, les actions de préférences, les paramètres ambigus et les installations voisines sont exclus.

Résumé attendu : `8/8 contrôles purs de progression de l’index réussis.` Les événements Playwright, les pages GLPI et les délais réels de réponse sont exercés séparément par `TicketLibraryAcquisitionChecks`, sur Windows CI uniquement.
