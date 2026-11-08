# Données communes entre profils

Cette suite passe par le vrai bridge C#, trois profils locaux indépendants et le transport de dossier partagé. Elle vérifie la configuration, les tournées avec leur identité conservée, les compétences et capacités, les corrections de localisation, le relevé de 19 colonnes, son suivi, les coupures, la résolution d’éditions concurrentes et l’isolation de la simulation.

Les 22 scénarios contrôlent aussi les périmètres GLPI distincts derrière une même URL, le recalcul d’un relevé ouvert après réception de seuils, la conservation des colonnes après enrichissement cartographique, le premier profil vierge et le déplacement vers une nouvelle base locale. Une comparaison périmée ou une modification locale non synchronisée ne peut pas écraser le travail courant. Les tests de reprise simulent un échec d’application sur les fichiers locaux, puis une nouvelle édition ; les deux versions doivent rester disponibles. L’empreinte d’un relevé reste identique lorsque sa représentation JSON change sans modifier ses valeurs.

Les dossiers et données sont synthétiques, temporaires et détruits en fin de vérification. Le transport est exercé sur le système de fichiers réel du runner ; ce test ne certifie pas les propriétés d’un serveur SMB particulier.

```sh
dotnet run --project tests/SharedBusinessChecks/SharedBusinessChecks.csproj -c Release
```
