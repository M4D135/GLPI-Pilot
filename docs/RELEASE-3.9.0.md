# GLPI Pilot 3.9.0 — Seuils par sélection, pourcentages et import des sites

## Définir les seuils depuis une carte météo

Le bouton **Seuils pour cette sélection** ouvre les réglages du widget dans le périmètre affiché. Les seuils de vigilance et de tension peuvent être créés, modifiés ou supprimés. Les valeurs absolues sont conservées ; les décomptes de tickets compatibles proposent aussi un calcul en pourcentage, avec choix explicite de la population de référence.

Deux portées sont disponibles :

- **Cette sélection uniquement** : une exception en valeur absolue ou en pourcentage, rattachée aux filtres exacts du widget et du tableau.
- **Ce widget, toutes les sélections** : une règle en pourcentage réutilisable lorsque les équipes ou les filtres changent. Une exception pour la sélection courante reste prioritaire.

Le moteur distingue la source et la recherche, les groupes réellement sélectionnés, les filtres communs et propres au widget, les dates, les inclusions et exclusions, les sous-catégories, le technicien, les requêtes et les règles d’interprétation des colonnes. Il reconnaît les ensembles équivalents lorsque seul leur ordre change. Une autre sélection n’hérite pas d’une exception. Les seuils des équipes ne sont jamais additionnés.

La sélection est enregistrée dans la configuration du tableau. Après redémarrage, elle est retrouvée avec ses seuils, y compris depuis le dernier relevé conservé sans le CSV d’origine. Les copies de tableaux, export/import de configuration et **Définir comme configuration par défaut** conservent ces réglages. L’édition reste accessible dans les éditions Standard et Admin ; la démonstration conserve sa configuration isolée.

## Des bases préparées à partir des colonnes disponibles

Le choix de la base précise toujours ce qui est compté. Les bases de sélection sont calculées avant les filtres propres au widget : une carte « incidents » peut ainsi mesurer les incidents parmi les tickets actifs de la sélection. Les bases « source entière » restent disponibles quand la part recherchée porte sur toutes les données chargées.

| Indicateur | Base conseillée |
| --- | --- |
| Tickets en attente, nouveaux, en traitement, sans technicien ou autres sous-ensembles actifs | Tickets actifs de la sélection |
| Volume actif sans filtre propre au widget | Tickets actifs de la source entière |
| Total, tickets résolus ou clos, flux de création/résolution/clôture | Tous les tickets de la sélection |
| Priorité haute ou majeure | Tickets actifs dont la priorité est reconnue |
| Urgence ou impact élevés | Tickets actifs dont le champ correspondant est reconnu |
| Tickets anciens | Tickets actifs avec une date d’ouverture exploitable |
| Tickets sans modification récente | Tickets actifs avec une date de modification exploitable et cohérente |
| Échéance dépassée ou proche | Tickets actifs avec une date d’échéance exploitable |

Les bases sont proposées, les valeurs de vigilance et de tension restent à saisir. **TTR + Progression** n’est pas assimilé à une date d’échéance. L’âge déclaré, le délai de prise en compte et les nombres de groupes ou de rédacteurs restent des champs distincts. Une durée ne devient pas un pourcentage sans population définie ; un indicateur déjà exprimé en pourcentage n’est pas divisé une seconde fois.

La carte montre son volume ainsi que le numérateur, la base et le ratio utilisé. Une base absente, nulle ou incompatible laisse la météo sans évaluation. Une acquisition incomplète ou une identité de tickets incertaine reste signalée. Les présentations et exports HTML/PDF/CSV/Excel reprennent les seuils effectivement appliqués et leur base. Le [guide des seuils](Seuils-meteo.txt) détaille le choix et les limites des ratios.

## Importer un référentiel privé sans effacer les personnalisations

Le CSV utilise le véritable format du référentiel C#, avec **Fusion=Completer**, disponible à partir de 3.9.0. Il complète les champs vides, conserve les valeurs personnalisées et réunit les alias sans doublon. Les identités et rattachements existants sont protégés ; une ambiguïté est refusée. Les coordonnées sont traitées par paire et ne sont pas ajoutées à une adresse personnalisée différente. Une ancienne adresse déjà renseignée est conservée : sa correction demande un choix explicite dans le référentiel.

Le référentiel de chaque organisation est importé depuis ses fichiers privés. Aucun catalogue géographique personnel n’est fourni par le dépôt public.

## Livraison et contrôles

Les packs **PROD** et **DEV** contiennent chacun `GLPI-Pilot.exe`, `GLPI-Pilot-Admin.exe` et `wad/`. La version 3.9.0 possède ses propres archives ; les anciens exécutables ne sont pas remplacés sous leur numéro précédent.

La publication des packs dépend des suites Windows et PostgreSQL, des parcours WebView, du démarrage natif Standard/Admin, de la reconstruction des sources DEV et des tests de mise à jour et de retour arrière. Les exécutables déjà publiés sous un numéro donné ne sont pas remplacés par des exécutables différents.

Le [rapport embarqué](Rapport-verification.txt) consigne les résultats acquis avant CI. Le workflow du commit inscrit dans `wad/version.json`, la release et les fichiers SHA-256 constituent les preuves finales de la livraison correspondante.
