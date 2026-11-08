# GLPI Pilot 3.11.2 : ajouter les groupes sans ressaisie

Dans Pilotage → Analyser et organiser → Seuils et équipes, le panneau « Groupes GLPI → cartes météo » indique les groupes détectés, ceux sans règle et ceux dont la règle prioritaire est masquée.

- « Ajouter des groupes à la météo » ouvre une recherche avec cases à cocher et ajout multiple. Tous les groupes du relevé sont proposés, pas seulement les 18 premières cartes proposées par défaut.
- Le filtre « Sans carte ou masqués » cible les groupes à ajouter ou à réafficher. Les cartes existantes ne sont pas dupliquées et leurs seuils et leur ordre sont conservés.
- Chaque règle d’équipe dispose de « Choisir les groupes », d’une sélection multiple et de boutons de retrait individuel. Les correspondances absentes du relevé restent visibles et conservées. La saisie manuelle reste disponible dans les options avancées.
- Les noms exacts, espaces, accents et ponctuation sont conservés. La recherche tolérante ne change jamais l’identité d’un groupe.
- L’ajout reste volontaire et soumis à « Enregistrer les paramètres ». La capacité existante de 48 règles est affichée, sans ajout partiel silencieux. Une équipe accepte jusqu’à 32 groupes.

## Vérifications avant push

Syntaxe JavaScript contrôlée pour les trois modules modifiés/ajoutés. Six contrôles ciblés exécutés avec Node : groupes au-delà du dix-huitième, identité, ajout multiple, réaffichage sans doublon, refus atomique des dépassements et conservation des correspondances absentes.

Le parcours Windows/WebView2 et le SSO d’entreprise n’ont pas été testés localement pour ce correctif. Les essais utilisateur restent à réaliser. Aucun catalogue de sites, nom d’équipe privé, ticket ni JSON privé de déploiement n’est ajouté au dépôt.

Les workflows de livraison ne sont pas modifiés. Un push n’est ni une compilation ni une publication de packs Windows.
