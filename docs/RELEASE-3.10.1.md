# GLPI Pilot 3.10.1 — GLPI en premier et configuration dans wad

## Collecter depuis GLPI dès l’ouverture de Données

La carte **GLPI en direct** apparaît en premier dans **Données**, avec l’adresse de recherche, le navigateur, la méthode et le bouton principal **Lancer la collecte**. **Se connecter** reste disponible pour préparer la connexion et terminer le SSO si nécessaire.

L’import CSV / Excel se trouve dans **Autres façons de travailler**, avec les accès secondaires utiles. Le dernier relevé hors connexion et la démonstration restent disponibles. La source actuellement chargée conserve son libellé, sa date et ses limites : afficher le formulaire GLPI ne transforme pas les données d’un fichier ou d’un cache en données nouvellement collectées.

## Comprendre et préparer une collecte groupée

Le bouton **Collecter plusieurs recherches** remplace le lancement peu explicite d’une « campagne ». Il ouvre une préparation avec les recherches mémorisées à sélectionner, avant de lancer **Collecter les recherches sélectionnées**.

Sans recherche mémorisée, le panneau explique comment en ajouter dans **Mes recherches**. Une simple adresse renseignée dans **Configuration courante** s’utilise avec **Lancer la collecte**. Une sélection vide ne déclenche pas le moteur de collecte groupée.

La sélection est mémorisée. Une préparation devenue périmée ne doit pas écraser une autre configuration ni démarrer sur une sélection différente. Le moteur conserve ses identifiants et options historiques pour la compatibilité des configurations et des programmations.

Les recherches sont collectées successivement, avec un bilan par recherche. Les archives détaillées dépendent de l’option d’historique. Les tickets ne sont pas fusionnés en un tableau unique. Le bilan distingue les réussites, les résultats à vérifier et les échecs ; il propose un accès au relevé courant lorsque celui-ci reste disponible. La démonstration utilise toujours son espace fictif et ses adaptateurs locaux.

## Générer la configuration par défaut dans wad

**Réglages → Configuration complète et déploiement → Définir comme configuration par défaut** écrit désormais **`wad/GLPI-Pilot.deployment.json`**. L’export complet vers une autre destination reste disponible.

Le démarrage donne priorité au fichier de `wad`. Si celui-ci est absent, l’ancien fichier situé à côté des EXE reste reconnu. Les deux peuvent coexister ; l’ancien fichier n’est pas déplacé automatiquement. Un fichier présent mais invalide ou inaccessible dans `wad` est signalé, sans repli silencieux vers une autre configuration.

Les aperçus et confirmations contrôlent la configuration en cours et les octets du fichier avant l’écriture. Un défaut déjà présent dans wad est sauvegardé avant remplacement. Le fichier historique à la racine reste intact. Les profils neufs et existants gardent leur parcours d’application automatique ou de comparaison. Les secrets et données personnelles ne deviennent pas des données de déploiement ; les droits du lanceur Standard ou Admin restent inchangés.

L’assistant de mise à jour de 3.10.1, par GitHub, partage réseau ou ZIP local, et le retour arrière préservent le défaut local lorsque `wad` est remplacé. Le ZIP officiel et son manifeste restent intacts. Pour un remplacement manuel complet du dossier, conserver le JSON souhaité ou le générer de nouveau depuis les réglages du profil.

Voir le [guide de configuration et déploiement](https://github.com/M4D135/GLPI-Pilot/blob/v3.10.1/docs/Configuration-deploiement.txt).

## Livraison et validation

Les packs **PROD** et **DEV** ont leur propre numéro **3.10.1** et contiennent **GLPI-Pilot.exe**, **GLPI-Pilot-Admin.exe** et **wad/**.

Les contrôles couvrent les parcours de collecte directe et groupée Standard/Admin, l’import CSV conservé, la simulation, la sélection et ses révisions, le démarrage avec défaut dans `wad` ou à l’ancien emplacement, les conflits et la préservation du défaut lors d’une mise à jour ou d’un retour arrière. Les tests Windows du commit publié, les journaux et les empreintes des fichiers réellement téléchargés constituent les preuves de livraison. Les exécutables des anciennes releases restent conservés.
