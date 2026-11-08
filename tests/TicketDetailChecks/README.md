# Contrôles des fiches ticket — GLPI Pilot 1.3.0

Ce programme .NET 10 utilise Playwright et un vrai Chromium de test. Toutes les pages sont des fixtures synthétiques servies par l’interception des routes du contexte. Aucun profil utilisateur ni portail privé n’est ouvert.

## Exécution

Installer le SDK .NET 10 et restaurer le projet. Définir `GLPI_TEST_CHROMIUM` vers l’exécutable d’un Chromium/Chrome/Edge réservé aux tests.

PowerShell :

```powershell
$env:GLPI_TEST_CHROMIUM = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
dotnet run --project tests/TicketDetailChecks/TicketDetailChecks.csproj -p:PlaywrightPlatform=win32
```

Linux :

```sh
GLPI_TEST_CHROMIUM=/chemin/chromium dotnet run --project tests/TicketDetailChecks/TicketDetailChecks.csproj
```

Le navigateur est lancé en contexte neuf et en mode sans fenêtre, avec les arguments de bac à sable de l’environnement de test. Ce lancement ne reproduit pas la politique SSO Windows ou l’ordre des fenêtres du poste réel. Le logiciel livré utilise un vrai navigateur visible et son profil dédié.

`GLPI_TEST_FILTER` permet de ne lancer que les scénarios dont le nom contient le texte donné. Le code de sortie vaut zéro seulement si tous les scénarios sélectionnés passent.

## Couverture

Les 27 contrôles utilisent `tests/fixtures/ticket-legacy.html`, `ticket-modern.html` et `ticket-treatment.html`, plus des variantes en mémoire :

1. Formulaire legacy : cellules champ/valeur, sélection Select2, cases et valeurs multiligne.
2. Formulaire moderne : libellés, champs statiques, cases avec valeur cachée de repli, radios.
3. Tables imbriquées, pieds de tableau et exclusion des colonnes sensibles.
4. Sections CSS configurées, noms, absence, ambiguïté, CSS invalide et plafonnement explicite.
5. Onglet GLPI legacy dont le gestionnaire charge la consultation par POST.
6. Onglet GLPI moderne dont le gestionnaire charge la consultation par GET.
7. Panneau AJAX lent, ancien contenu visible et `aria-busy` : attente du résultat nouveau.
8. Panneau local portant le rôle d’onglet.
9. Refus de boutons métier, submit, gestionnaire inline, autre origine et autre identifiant, sans clic.
10. Refus d’un autre ID dans le formulaire ou après redirection de la fiche.
11. Validation de la référence avant ouverture de page.
12. Réutilisation de la page dédiée, URL/DOM de recherche inchangés et absence de rechargement.
13. Exclusion des pages de détail du choix de recherche, y compris quand la recherche est fermée.
14. Annulation réelle de la navigation, recherche conservée et sélection suivante isolée.
15. Redirection SSO naturelle, retour sur une page d’accueil puis réouverture de la fiche.
16. Popup SSO descendant de la seule page de détail, sans réaffectation de la recherche.
17. Cadre visible de même origine ; cadres masqué et étranger exclus.
18. Journaux structurels sans identifiant, titre, texte, query, mot de passe ou jeton.
19. Aucun contrôle modifié, formulaire soumis, clic ou `window.focus()` pendant une lecture automatique ordinaire.
20. Description d’un éditeur TinyMCE visible associé à son champ, sans lecture des zones de dépôt de fichiers comme valeurs métier.
21. Lecture automatique de l’onglet de traitement et de ses suivis, tâches, solutions, documents et validations.
22. Découverte des seuls onglets de consultation vérifiés et lecture différée dans la page dédiée.
23. Préférence de traitement désactivée et limite de lignes explicitement signalée.
24. Annulation pendant le chargement AJAX d’un onglet, sans altération de la recherche.
25. Panneau ciblé absent ou masqué : aucune lecture de repli du contenu précédent ; activation réelle exigée.
26. Réordonnancement des onglets après rechargement : la clé native et le libellé retrouvent la rubrique attendue.
27. Refus d’un onglet ou d’une cible secondaire relevant d’une autre installation GLPI, et des identifiants ambigus.

Le contrôle des actions de lecture observe les appels JavaScript et les requêtes HTTP du contexte. L’absence de `BringToFrontAsync` automatique découle du code du service de fiche et des tests séparés de `BrowserPresentation`; l’ordre réel des fenêtres Windows doit être validé sur Windows.

Les secrets synthétiques `NEVER_*` sont des sentinelles de non-divulgation. Les résultats ne sont jamais enregistrés comme données utilisateur. Le journal de validation est `artifacts/ticket-detail-checks-1.3.0.log`.
