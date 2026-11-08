# Préparer une présentation dans GLPI Pilot

## Une présentation par tableau

Ouvrir **Pilotage**, choisir le tableau et son périmètre, puis **Présenter**. L’éditeur travaille sur les chiffres déjà calculés pour cette sélection. Le bandeau rappelle la source et la date ; déplier les informations de provenance pour lire les filtres et les réserves du relevé.

Une première page rassemble quelques chiffres clés et analyses. **Composer automatiquement** répartit les widgets visibles par rubrique en plusieurs diapos. Cette proposition peut être entièrement modifiée.

## Construire les pages

- **Ajouter une diapo** crée une page vide après la page courante. Le titre se modifie au-dessus du canevas.
- **Dupliquer** copie la page et ses placements. **Supprimer** retire la page ; une page vide reste disponible si la dernière est supprimée.
- Les flèches sous chaque miniature déplacent les pages dans la présentation.
- La liste de droite contient les widgets visibles du tableau. Cliquer un widget ou le glisser sur la page pour l’ajouter. Il peut revenir sur plusieurs pages ; une même page ne le répète pas.
- Déplacer un widget par son en-tête. Tirer une de ses huit poignées pour changer sa taille. La **Grille magnétique** peut être désactivée.
- Après sélection d’un widget, régler **Gauche**, **Haut**, **Largeur** et **Hauteur** en pourcentage de la surface utile. Les valeurs restent dans les limites de la page. Les flèches déplacent le widget par pas de 0,1 % ; Maj + flèche utilise un pas de 1 %.
- **Premier plan** et **Arrière-plan** règlent l’ordre des superpositions. **Retirer le widget** enlève seulement son placement dans la présentation.
- **Répartir les widgets** propose une disposition régulière pour la page courante. **Annuler / Rétablir** permet de reprendre les modifications récentes.

Le format est 16:9. Une présentation accepte 40 pages, 20 widgets par page et 300 placements au total. Les pages vides peuvent être enregistrées pendant la préparation ; elles doivent être remplies ou supprimées avant de présenter.

## Enregistrer et retrouver la composition

**Enregistrer** mémorise les pages avec le tableau. Le contenu des widgets reste calculé à partir du relevé ouvert : la composition ne conserve pas une copie cachée des chiffres. Elle se retrouve après redémarrage, y compris avec le dernier relevé hors connexion.

Chaque tableau possède sa présentation. Les dispositions personnelles et communes restent séparées. La copie d’un tableau remappe ses placements vers les nouveaux identifiants de widgets. L’export/import de configuration et **Définir comme configuration par défaut** conservent les diapos. Les essais en démonstration restent dans l’espace fictif.

Si un widget a été supprimé ou masqué, son emplacement est signalé. Retirer cet emplacement ou ajouter le widget souhaité avant de présenter. Une modification concurrente de la composition ou des widgets empêche l’enregistrement depuis un ancien éditeur ; rouvrir la préparation depuis le tableau actualisé.

## Projeter

**Présenter** enregistre la composition puis l’ouvre en lecture seule. Utiliser les boutons **Précédent / Suivant**, la liste des diapos ou les flèches du clavier. **Revenir à la préparation** et Échap quittent la projection. La source et la date restent visibles sans occuper le centre de la page ; les détails de provenance se déplient.

Les widgets affichent les mêmes valeurs et seuils que le tableau. Ils ne relancent pas une collecte pendant la réunion. Les boutons de détail et d’édition sont retirés des copies présentées. Les scènes météo respectent le réglage global de **Réglages → Confort et présentation**.

## Exporter

| Format | Résultat |
| --- | --- |
| **Diapositives PDF (16:9)** | Une page de 320 × 180 mm par diapo, avec les mêmes placements et tailles. Les graphiques et aperçus de tableaux reprennent le rendu présenté. |
| **Diapositives HTML et données détaillées** | Les pages et une annexe complète des données des widgets sélectionnés, avec leur contexte et leurs réserves. Le fichier s’ouvre hors connexion. |
| **Bilan Excel / CSV** | Les tableaux détaillés des widgets présents, dans l’ordre de leur première apparition. Un widget répété sur plusieurs pages n’est exporté qu’une fois dans ces données. |

Un petit emplacement peut ne montrer qu’une partie d’un tableau. Agrandir le widget pour la réunion ou utiliser les données détaillées HTML, Excel ou CSV. Le PDF n’ajoute pas de pages de tickets à la composition choisie. La recherche, les filtres, le périmètre personnel et les réserves sont imprimés ; si ces mentions sont trop longues pour le pied de page, des pages de contexte identifiées sont ajoutées après les diapos pour en conserver le texte complet. Les exports ne doivent pas être utilisés pour donner une précision absente du relevé : les valeurs indisponibles et les réserves de source restent signalées.
