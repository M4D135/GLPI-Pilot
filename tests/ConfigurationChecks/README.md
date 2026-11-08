# Vérifications de configuration — GLPI Pilot 1.4.0

Ce programme exécute 37 scénarios métier sans ouvrir de navigateur. Il utilise les fichiers du cœur de l’application par liaison de compilation ; les contrôles portent donc sur le code distribué.

## Exécution

Depuis la racine du projet, avec le SDK .NET 10 :

```sh
dotnet run --project tests/ConfigurationChecks/ConfigurationChecks.csproj --configuration Release
```

Le premier lancement restaure Microsoft.Playwright 1.63.0, utilisé par le modèle de choix de tableau partagé. Aucun navigateur, portail GLPI, compte SSO ou accès à un serveur n’est nécessaire pour exécuter les scénarios. Après restauration, ajouter `--no-restore` permet de réutiliser les dépendances disponibles.

Le programme renvoie le code `0` lorsque les 37 vérifications réussissent et `1` si une vérification échoue. Il crée un dossier temporaire distinct puis le supprime à la fin, sans toucher aux réglages locaux de l’application.

## Périmètre vérifié

- Copie indépendante des sections de ticket et des correspondances du tableau de bord ; normalisation des anciens réglages incomplets ou nuls.
- Export JSON limité à une liste explicite de réglages partageables, conservation des filtres de recherche et retrait des paramètres d’authentification reconnus. Le dossier d’export du poste et les champs de profil, de cookies ou d’authentification sont exclus.
- Import sans sauvegarde ni mutation des réglages courants ; conservation du dossier d’export local ; migration d’un ancien `settings.json`.
- Validation du schéma, de la version, des propriétés dupliquées ou inconnues, des types, des bornes numériques, des sections et des sélecteurs CSS. Le fichier est limité à 1 Mio et à une profondeur JSON bornée.
- Correspondances et seuils du tableau de bord : clés reconnues, cultures de dates explicites, seuil d’ancienneté supérieur ou égal au seuil d’inactivité.
- Écriture atomique : une validation ou une écriture refusée ne remplace pas un fichier existant et ne laisse pas de fichier temporaire.
- Résolution d’un identifiant de ticket à partir d’une colonne unique ; normalisation des zéros initiaux et des groupements numériques français, refus d’un nombre extrait d’un titre, d’une valeur ambiguë ou d’un identifiant nul.
- Vérification de la provenance du relevé, conservation du préfixe GLPI et génération d’une fiche `front/ticket.form.php` en lecture. Les modèles d’adresses externes, les paramètres d’action métier, les identifiants dupliqués et les substitutions arbitraires sont refusés avant navigation.
- Aller-retour des dispositions et widgets : ordre, taille, titre, note, styles, couleurs, présentation, masquage et verrouillage. Les copies sont indépendantes ; les anciens réglages reçoivent une disposition initiale.
- Dispositions globale et personnelle indépendantes, jauges, objectifs et seuils, avec migration des anciens réglages et refus des valeurs non finies ou incohérentes.
- Villes et variantes : partage des identifiants, codes postaux et coordonnées ; validation du JSON brut avant normalisation, sans perdre des alias invalides silencieusement.
- Bibliothèque de requêtes : partage des conditions, regroupements, favoris et références de widgets ; refus des propriétés inconnues et des définitions invalides avant normalisation. Les anciens fichiers reçoivent les modèles initiaux sans données de tickets synthétiques.
- Validation du JSON brut des dispositions avant normalisation : bornes, nombre de tableaux et de widgets, identifiants uniques, tableau actif, styles et propriétés autorisées. Les valeurs invalides ne sont pas silencieusement ramenées dans les limites.
- Identité personnelle exclue de la configuration partageable, conservée sur le poste à l’import, y compris lors de la migration d’un ancien fichier local contenant une identité.
- Préférence de lecture automatique du traitement et type de section `timeline`, avec valeur initiale compatible pour les anciens réglages.
- Diagnostic des intitulés et correspondances sans valeurs de cellules, nom du technicien, nom des dispositions ni paramètres d’URL. Les contrôles Unicode des intitulés sont échappés.

Les scénarios utilisent uniquement des données synthétiques et des domaines `.test`. Ils ne démontrent pas le comportement du SSO d’un environnement réel ; celui-ci relève des vérifications de navigation distinctes.
