# CityDetectionChecks

Tests BCL sur données synthétiques, sans navigateur, portail privé ni appel réseau.

Les vérifications couvrent les variantes de ville (y compris `MA`, `Mars` et `Marseille`), les mots entiers, accents et ponctuation, les noms composés qui se chevauchent, les ambiguïtés, les correspondances exactes de colonnes, la conservation du relevé source, les métadonnées et indices originaux, les limites de configuration et coordonnées, ainsi que les sondes de dashboard limitées à leur périmètre réel et leur annulation.

```bash
dotnet run --project tests/CityDetectionChecks/CityDetectionChecks.csproj
```

La ville détectée reste une inférence depuis le titre. Aucun géocodage, rapprochement approximatif ni adresse réelle n’est inventé. Un alias court peut aussi être un mot courant : il doit être choisi en tenant compte des titres utilisés.
