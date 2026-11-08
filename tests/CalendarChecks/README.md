# Contrôles du calendrier d’interventions

Exécuter :

```text
dotnet run --project tests/CalendarChecks/CalendarChecks.csproj --configuration Release
```

Les contrôles sont autonomes : aucun relevé, navigateur, compte GLPI, réseau ou base réelle n’est nécessaire.

Le programme vérifie les interventions libres, les liens à plusieurs tickets et installations, les réservations communes à plusieurs participants, les récurrences quotidiennes/hebdomadaires/mensuelles, les exceptions et les séparations de série. Les identités restent stables après déplacement d’une occurrence, déplacement d’une intervention libre et séparation d’une série.

Les cas horaires couvrent la fin exclusive, minuit, les deux changements d’heure Europe/Paris, l’heure répétée choisie explicitement et une fin locale antérieure au début mais postérieure en UTC. Une occurrence récurrente invalide produit un diagnostic sans masquer les autres rendez-vous.

Les contrôles de réservation vérifient les collisions, les indisponibilités, les visites de tournée, les temps de déplacement déclarés, la présence, les capacités explicites et l’absence d’horaires. Une capacité inconnue reste inconnue ; une capacité sur une plage n’est pas arbitrairement répartie entre deux demi-journées.

Les exports sont contrôlés sur leurs UID stables, les dates de journée entière, les annulations, les alarmes, l’échappement HTML et le pliage UTF-8 d’iCalendar. Les contrôles couvrent aussi les périodes bornées, l’annulation et une ancienne série ouverte.

Les scénarios de stockage, de concurrence, de synchronisation et d’interface sont exécutés séparément dans les suites du moteur et de la WebView.
