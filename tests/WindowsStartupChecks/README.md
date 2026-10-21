# Vérifications WPF réelles sous Windows

Ce projet référence `src/GlpiPilot/GlpiPilot.csproj` et instancie ses contrôles de production. Il complète les tests de calcul et de DOM, qui ne lancent pas WPF. Les résultats de réussite sont produits uniquement après une exécution réelle sous Windows ; une compilation depuis Linux vérifie le code, sans valider l’interface.

## Vues isolées et captures

Sur Windows x64, avec une session graphique interactive et le SDK .NET 10, depuis la racine du projet :

```powershell
dotnet run --project tests/WindowsStartupChecks/WindowsStartupChecks.csproj -c Release -- --screenshots .\ui-previews
```

L’option `--screenshots <dossier>` est facultative. Pour conserver aussi la sortie console :

```powershell
dotnet run --project tests/WindowsStartupChecks/WindowsStartupChecks.csproj -c Release -- --screenshots .\ui-previews 2>&1 | Tee-Object -FilePath WindowsStartupChecks.log
```

Le programme ouvre une fenêtre intitulée `GLPI Pilot — Windows UI checks (synthetic data)`, parcourt ses vues puis la ferme. La limite globale est de 90 secondes. Les attentes de premier rendu et de calcul asynchrone sont également bornées. Les PNG sont rendus à partir des vrais éléments WPF, avec leur taille et leur échelle DPI effectives ; ils contiennent uniquement les données synthétiques définies dans ce projet. Une erreur pendant le parcours produit aussi, si le rendu est possible, `failure-current-view.png`.

Le code de sortie est `0` si les contrôles demandés ont réussi, `1` en cas d’échec ou de fermeture prématurée, `2` pour un dépassement de la limite globale, et `3` pour des arguments ou un environnement inadaptés. La ligne finale indique le nombre de contrôles réellement terminés.

### Contrôles réalisés

- Ressources de production : le fichier `App.xaml` réel est lié comme ressource du harnais. Ses définitions sont chargées dans une `Application` WPF isolée ; aucune copie des styles n’est maintenue à part.
- Onze vues synthétiques : dashboard vide et renseigné, espace techniciens, quatre modes du lecteur de ticket (champs, traitement, tableau, texte), référentiel des villes, tournées, atelier de requêtes vide et alimenté.
- Premiers événements `Loaded` et `ContentRendered`, dimensions rendues non nulles, liaison et matérialisation des vrais templates du lecteur de ticket.
- Dashboards calculés par les services de production, dispositions prédéfinies, anneau, jauge, ajout de widgets de requête aux dispositions globale et personnelle sans partage des mutations.
- Personnalisation réelle du dashboard : ouverture de l’éditeur, catalogue, poignées de déplacement et contrôles de redimensionnement.
- Trois scénarios de mise en page : 1440 × 900 à 100 % en clair, 960 × 720 à 150 % en clair, 1280 × 850 à 125 % en sombre. Le zoom de ces vues isolées utilise une transformation WPF ; les boutons de la vraie fenêtre sont testés séparément ci-dessous.
- Mesure des cartes du dashboard : aucun recouvrement ni dépassement du panneau, aucun ascenseur vertical actif à l’intérieur des cartes. Le dashboard personnel garde un seul viewport vertical parent. L’ouverture des explications météo agrandit la carte et replace les autres sans les recouvrir.
- Liste complète d’une répartition : le moteur calcule 43 catégories synthétiques. Les commandes de pagination doivent toutes les rendre accessibles sans doublon, la recherche doit atteindre une catégorie absente de la première page, et son action doit conserver les index des lignes source. Le retour restaure le widget.
- À 150 %, les listes des techniciens, des étapes de tournée et de la bibliothèque de requêtes restent bornées dans la fenêtre et virtualisées, sans défilement vertical parent. La bibliothèque est vérifiée par rapport aux modèles réellement fournis par le moteur.
- Référentiel des villes : saisie groupée, ajout et fusion d’alias, conservation du brouillon sans mutation de la configuration fournie au contrôle.
- Tournées : ordre affiché et exporté, persistance dans un répertoire temporaire dédié, index de lignes et événement de carte sur action explicite, retour après l’état occupé et rejet d’un calcul devenu périmé.
- Requêtes : prévisualisation de la bibliothèque réelle, répartition, index des lignes source, invalidation des anciennes actions après modification et conservation du brouillon si la source disparaît.
- Rendu des onze vues et de tous les sous-onglets techniciens, tournées et requêtes dans les deux thèmes.

Les captures couvrent les dashboards global et personnel dans les trois scénarios, la météo dépliée, une liste complète, les techniciens, les tournées et la bibliothèque de requêtes. Elles permettent de compléter les assertions géométriques par une inspection visuelle.

### Isolation du mode vues

Le harnais crée une `System.Windows.Application` ordinaire, sans instancier `GlpiPilot.App` ni enregistrer ses gestionnaires de démarrage. Il ne construit pas `MainWindow`, ne lit ni n’écrit `AppSettings`, ne démarre pas `BrowserService` et n’utilise ni SSO ni réseau applicatif. Les préférences de tournée sont écrites seulement dans un répertoire temporaire propre à ce processus, supprimé à la fin ; les autres réglages restent en mémoire. Les captures sont écrites uniquement si leur dossier est demandé.

Cette isolation ne valide pas le démarrage complet, le verrou d’instance, les réglages du poste, le SSO ou l’extraction sur un vrai portail. Le contrôle séparé `PackStartup` lance l’exécutable publié dans un état de test temporaire et vérifie son démarrage et sa fermeture. Les tests WPF ne simulent pas un glisser-déposer de souris et les captures ne remplacent pas une inspection visuelle.

## Zoom de la fenêtre du pack

Le script de CI Windows lance les modes suivants sur le **PID exact de son exécutable de test**, avec des réglages isolés. Ils manipulent le zoom de cette fenêtre, qui est mémorisé par l’application elle-même ; le script garde la responsabilité de lancer et fermer le processus et de contrôler ses fichiers de réglages.

```powershell
WindowsStartupChecks.exe --inspect-pack-window <pid> exercise
WindowsStartupChecks.exe --inspect-pack-window <pid> verify-restore
```

`exercise` exige un zoom initial de 100 %, actionne les vrais boutons accessibles selon la séquence 100 → 105 → 110 → 105 → 100 %, parcourt les bornes 75 et 150 %, puis vérifie que les commandes de zoom et d’extraction restent dans la fenêtre redimensionnée à 960 × 720 à 150 %. Il restaure la taille de la fenêtre et laisse le zoom à 110 % pour la fermeture et le redémarrage effectués par le script.

`verify-restore` exige 110 % après ce redémarrage, vérifie les commandes puis revient à 100 % par le vrai bouton de réinitialisation. La validation des fichiers de préférences avant et après fermeture appartient à `PackStartup`, qui conserve aussi son premier lancement de référence sans aucune interaction ni mutation.

Ces deux modes utilisent UI Automation depuis un thread MTA sans fenêtre. Ils s’attachent au handle de la fenêtre du processus indiqué, vérifient son PID, son nom et son titre, puis cherchent uniquement dans ses descendants. Ils ne parcourent pas le bureau, ne ferment aucune fenêtre, ne déclenchent jamais le bouton d’extraction et n’ouvrent pas le navigateur. Leur limite interne est de 45 secondes.

Les raccourcis clavier et la molette ne sont pas simulés par ce contrôle UI Automation. Le résultat d’une exécution Windows et ses journaux restent nécessaires avant d’annoncer que ces scénarios ont réussi.
