# Ergonomie terrain et commandes tickets

Le chantier initialement conservé en patchs est intégré aux sources de la version 3.13.0. Les notes `RELEASE-3.13.0.md` décrivent le périmètre de cette livraison ; ce guide distingue les contrôles et leurs limites.

## Parcours

- Menu de gauche : comme Domenia, Pesto et FILPRO, la dernière ligne de la barre « Replier le menu » (indice Ctrl B au survol) replie le menu ; replié, il ne reste qu’une icône centrée qui le déplie (également Ctrl B ou la palette Ctrl K). Menu replié, le nom de chaque espace apparaît à côté de son icône au survol ou au focus clavier. Le choix est enregistré avec les préférences d’affichage et retrouvé au prochain démarrage. En fenêtre étroite, le bouton Navigation de la barre supérieure ouvre le menu en panneau ; en mode concentration (F11), il ramène le menu.
- Tableau de bord : consulter une synthèse, agrandir une carte pour lire tout le contenu chargé, fermer et retrouver sa position. Personnaliser reste un mode distinct, avec positions et dimensions enregistrées.
- Carte : la molette fait défiler la page. +/− fonctionnent sans changement de mode ; la vue agrandie autorise la molette pour zoomer. La liste et les options restent accessibles sans commandes permanentes supplémentaires.
- Matériel : rechercher une référence, saisir la quantité et Entrée ; préparer une ligne ou confirmer le groupe ; enregistrer. Les détails, le catalogue et les kits restent accessibles. Préparer n'est pas gérer un inventaire.
- Pilotage : cliquer un compteur ou appliquer des filtres ; sélectionner les préparations ; ouvrir leurs fiches. Le résumé des filtres reste visible même lorsque les contrôles sont repliés.
- Tournée : lire les étapes, modifier leur ordre dans les limites des verrouillages, ouvrir Matériel ou Fiches. Un brouillon nécessite une sauvegarde confirmée avant ces lectures de la tournée enregistrée.
- Ticket : choisir suivi/tâche/affectation, inspecter le formulaire reconnu, relire, confirmer une seule soumission et consulter le reçu. Statut/priorité/résolution restent dans GLPI natif.

## Contrôles

`tests/ErgonomicsChecks/run.cjs` exerce les vrais modules et styles avec des réponses API synthétiques : molette, zoom, agrandissement, retour de focus, widgets interactifs, clavier, quantités, unités, refus de sauvegarde, sélection périmée, réponses tardives, filtres, tournées et pilotage ticket. Fenêtres 1280×800, 1366×768, 1366×800, 1093×614 et 390×800 selon les parcours. Le menu de collecte produit des captures clair/sombre à 1280×800, 1366×768 et 1093×614 (espace utile réduit correspondant à une interface agrandie).

```sh
GLPI_TEST_PLAYWRIGHT_MODULE=/chemin/vers/.playwright/package \
GLPI_TEST_CHROMIUM=/chemin/vers/chromium \
node tests/ErgonomicsChecks/run.cjs
node --test tests/WorkspaceGridChecks/run.mjs
python tests/ReleasePackChecks/test_verify.py
```

Le mode `GLPI_TEST_OFFLINE_BLOBS=1` est uniquement une solution locale pour les environnements qui refusent la navigation aux URL synthétiques. Il ne désactive pas la politique de navigation du navigateur et ne prouve pas la CSP de production. La CI exécute les mêmes tests sans ce mode.

`ergonomics-check.yml` contrôle également les vrais handlers C#, le parcours terrain avec SQLite dans les deux éditions et les régressions de grille, partage et actualisation. Le workflow Windows principal appelle cette qualification et conditionne la publication à sa réussite, en plus de ses suites et du PostgreSQL réel.

Les tests natifs vérifient dans les véritables EXE le formulaire de matériel compact, la préparation d'une ligne, sa persistance, la fiche, l'export par le sélecteur Windows et la prise en compte d'une annulation. Les autres contrôles de démarrage, zoom, PDF, mise à jour et retour arrière restent requis.

## Garde-fous

Aucune nouvelle lecture GLPI à l'ouverture des panneaux, aucun envoi implicite, aucune fusion de quantités d'unités différentes, aucun élargissement des droits. Les compteurs concernent les préparations et sources connues ; une source absente n'est pas inventée. Les révisions et conflits restent contrôlés par le moteur existant.

Les kits ne s'ajoutent qu'après confirmation. Les avertissements de tournée ne disparaissent pas avec les commandes secondaires. Un résultat d'envoi indéterminé reste un résultat à vérifier. Aucune re-soumission automatique.

Les idées générales du cadrage ne sont pas toutes des fonctionnalités supplémentaires : pas de nouveaux adaptateurs de statut/résolution, pas de gestion de stock, pas de nouveau service de routage automatique, pas de sauvegarde silencieuse de tous les formulaires. Les protections des saisies et brouillons existants sont conservées.

Les permissions réseau privées, GLPI/SSO, imprimantes et données de l'organisation restent hors de l'environnement de test. Aucun résultat n'est anticipé : consulter les runs du SHA effectivement publié et leurs preuves.
