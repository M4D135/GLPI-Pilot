# GLPI Pilot 4.0.2

## Indicateur de cache après lecture

Une première lecture complète pouvait laisser le pictogramme de base de données orange alors que la fiche venait d’être lue et mise en cache. La couleur dépendait de la preuve de stabilité du repère de modification : une date récente, située dans la marge de deux minutes plus sa précision, empêchait cette preuve. Une date absente ou inexploitable produisait le même affichage.

Le pictogramme devient maintenant **vert** pour une lecture complète réussie datant de cinq minutes au plus, avec le libellé **« Cache récent · lecture réussie »**. Cela s’applique dans la recherche et dans le détail. L’infobulle explique séparément la vérification de version ; les dates connues de modification et de lecture restent affichées.

- Une modification détectée dans le relevé reste **orange**, même si la copie vient d’être lue.
- Une lecture incomplète, une date de lecture absente ou future ne devient pas verte.
- Une version non comparable cesse de bénéficier du vert de lecture récente après cinq minutes, lors du recalcul de l’indicateur.
- Une copie dont le repère correspond au relevé conserve son indicateur vert jusqu’au seuil visuel existant de sept jours ; son ancienneté ne force pas de relecture si la version est inchangée.
- Le calcul de la couleur ne modifie pas la décision de lecture automatique, la lecture manuelle, les données ni le cache.

Le diagnostic distingue désormais une modification trop proche de la lecture d’une date absente ou non reconnue. La marge de comparaison reste conservée ; une lecture récente ne devient pas une preuve que GLPI est resté inchangé après cette observation.

## Vérification

Les tests reproduisent une modification à 12:00:00, un relevé observé à 12:01:09 et une lecture réussie à 12:01:25, avec des tickets entièrement fictifs. Ils vérifient le passage immédiat au vert, le maintien du diagnostic de version, la limite de cinq minutes, les horloges incohérentes et le retour à l’orange lorsqu’une modification est détectée. Le parcours d’interface contrôle le premier passage du rouge au vert dans le détail puis dans la liste conservée.

La qualification Windows et PostgreSQL et le contrôle de l’archive originale restent requis avant publication du pack PROD-LIGHT. La page Releases fait foi pour la disponibilité du pack.

## Mise à jour

Pack Windows x64 : `GLPI-Pilot.exe`, `GLPI-Pilot-Admin.exe` et dossier `wad/`. Les réglages et les données existantes sont conservés. Le runtime WebView2 de Windows ou du Socle reste utilisé.
