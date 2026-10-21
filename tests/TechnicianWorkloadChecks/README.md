# Contrôles de la gestion de charge des techniciens

Projet console .NET 10, BCL uniquement, sans portail GLPI, navigateur ni interface WPF. Les noms, tickets et notes des jeux d’essai sont synthétiques. Les fichiers de préférences de test sont créés dans un dossier temporaire propre au processus, supprimé à la fin.

```powershell
dotnet run --project tests/TechnicianWorkloadChecks/TechnicianWorkloadChecks.csproj -c Release
```

## Ce que vérifient les 33 contrôles

- Concordance de chaque volume et de ses indices source avec un dashboard calculé séparément sur le même technicien.
- Affectations multiples présentes dans les vues concernées, unions de lignes d’équipe sans double comptage, identifiants répétés conservés comme occurrences.
- Actifs, attente, priorités hautes, échéances, âge et dernière modification : population et couverture propres à chaque technicien ; valeurs inconnues distinctes des zéros.
- Dates futures ou incohérentes exclues, seuils configurés appliqués, moyennes calendaires contrôlées avec une horloge fixe.
- Colonnes absentes, ambiguës et correspondances explicites ; sous-ensembles dédoublonnés conservant les indices du relevé d’origine ; périmètres vides et sources partielles.
- Découpage prudent des noms, conservation des accents, virgules et barres obliques ; une identité répétée dans une cellule ne compte qu’une fois.
- Objectif local obligatoire pour calculer un ratio, dénominateur positif borné, ratio supérieur à 100 % possible.
- Annulation avant et pendant le calcul, cas de 1 200 techniciens avec affectations partagées.
- Persistance locale atomique, copie indépendante des préférences, bornes du fichier, des identités, des objectifs et des notes, documents invalides, conservation de l’état précédent après un échec d’écriture.

Le module calcule une snapshot globale canonique pour la comparaison puis intersecte les indices source du roster. Il ne calcule pas un dashboard complet pour chaque ligne du tableau. Le dashboard détaillé est calculé uniquement pour le technicien demandé.

Les tests ne valident pas le rendu Windows, le SSO, les droits du portail ni l’exhaustivité d’une extraction réelle. Le harnais Windows séparé reste nécessaire pour les contrôles d’interface.
