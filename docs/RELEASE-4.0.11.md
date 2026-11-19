# GLPI Pilot 4.0.11

## Toutes les colonnes par défaut

Le choix **Toutes les colonnes proposées par GLPI** devient le défaut des nouveaux profils et des réglages sans valeur de préparation des colonnes. La collecte conserve les colonnes présentes et ajoute les champs manquants proposés dans la vue personnelle. Le périmètre reste celui de la recherche GLPI filtrée.

Lors du premier chargement d’un ancien profil local sans marqueur de migration, le réglage courant **Colonnes de supervision** passe à **Toutes les colonnes**. Les anciennes versions enregistraient la même valeur pour le défaut et pour un choix explicite : ce passage unique concerne donc l’ancien réglage local courant dans les deux cas. La migration est mémorisée dans le profil afin de respecter une sélection ultérieure de **Colonnes de supervision**. Le mode **Vue actuelle**, les choix des recherches mémorisées et les modes explicitement importés ou restaurés depuis une sauvegarde sont conservés.

## Contrôles natifs capturés et revalidés

GLPI peut conserver des copies masquées de ses contrôles ou renouveler une partie du formulaire pendant la préparation des colonnes. Une recherche répétée à partir du seul sélecteur pouvait alors retrouver un autre élément ou plusieurs copies, même si l’inspection native avait identifié un contrôle utilisable.

La préparation garde désormais les références des éléments DOM exacts reconnus par cette inspection : tableau, ouverture et fermeture des préférences, iframe, onglet personnel, menu des colonnes et commande d’ajout. Elle les revalide avant les actions en contrôlant le contexte natif et la vue personnelle. Les copies masquées et les éléments de composants étrangers, notamment les racines shadow, ne sont pas substitués aux éléments inspectés. Un contrôle renouvelé peut être relu tant qu’aucune commande n’a été envoyée ; un ajout déjà émis n’est pas renvoyé automatiquement.

Une ambiguïté entre plusieurs contrôles natifs concurrents dans le même périmètre reste bloquante. Le diagnostic identifie le rôle du contrôle et le nombre de candidats ; pendant la préparation, il indique aussi la phase concernée. Les erreurs d’attente précisent la phase dont la commande n’a pas pu être confirmée. Les contrôles d’origine, de profil, d’entités, de critères de recherche et de vue personnelle restent appliqués.

## Nouvelle tentative dans Données

Au lancement d’une nouvelle collecte, l’ancien bandeau d’erreur est effacé uniquement dans la carte de la source GLPI. Les autres alertes, le journal et l’historique des opérations sont conservés. Le dernier relevé reste consultable pendant la tentative ; sa présence n’indique pas la réussite de celle-ci. Une nouvelle erreur de validation ou de collecte est affichée normalement.

## Qualification requise

Onze nouveaux parcours de navigateur couvrent les copies masquées créées après capture, les contrôles renouvelés, les doublons dans des racines shadow étrangères, les ambiguïtés actives, le remplacement par une vue globale et deux collectes successives sans ajout répété. Le parcours Données vérifie aussi l’effacement de l’ancien bandeau pendant une nouvelle tentative, la conservation des autres alertes et l’affichage d’un nouvel échec.

| Parcours obligatoire | Seuil requis |
| --- | --- |
| Automatisation des colonnes GLPI | 55/55 |
| Données et sources · Standard | 42/42 |
| Données et sources · Admin | 42/42 |

Le budget du préflight des colonnes et exports natifs passe de 7 à 10 minutes pour accueillir les onze nouveaux cas. Le préflight réussi a exécuté les 55 cas de colonnes en 397 secondes ; leur groupe dans le harnais complet dispose donc de 600 secondes au lieu de 360. Les autres groupes conservent 360 secondes, chaque cas garde sa propre limite et les délais de l’application restent inchangés. Les contrôles de configuration portent sur le nouveau défaut, la migration unique et la préservation des choix ultérieurs, des recherches mémorisées et des imports.

Ces seuils sont les critères de qualification ; leur réussite se vérifie dans le run associé au commit publié. Les parcours utilisent des données synthétiques ; le portail privé et le SSO de l’organisation ne sont pas testés directement. La publication exige une qualification Windows et PostgreSQL complète sur le commit exact, puis l’inspection de l’archive originale PROD-LIGHT.

## Mise à jour

Fermer GLPI Pilot, puis remplacer les deux exécutables et le dossier `wad/` par ceux du pack 4.0.11 lorsqu’il est publié. Les données et les contenus déjà recueillis sont conservés. Les filtres de collecte ciblée, les dates d’historique, la reprise des lots et le taux de couverture introduits en 4.0.9 restent disponibles.
