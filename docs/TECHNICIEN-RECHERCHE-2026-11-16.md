# Recherche centrale et collecte SSO — 16 novembre 2026

Évolution locale depuis 3.14.7, sans changement de version ni publication de pack.

## Parcours technicien

Rechercher, Carte, Planning, Tournées et Matériel partagent une barre de navigation et le même contexte de recherche. Les critères s’appliquent à tous les résultats, indépendamment de la pagination. Une sélection explicite restreint les outils aux tickets sélectionnés. Un changement de source ou de révision invalide ce contexte ; les indices de lignes ne sont pas conservés dans le stockage du navigateur.

La fiche ticket occupe toute la page. Retour à la liste conserve la recherche, la sélection, la position et le travail en cours. Précédent/Suivant traverse les pages de résultats. Journal et suivi personnel disposent de zones distinctes dans la fiche.

Le planning conserve son moteur de rendez-vous et ajoute un mini-calendrier, les agendas latéraux et la liaison avec la recherche. L’option « Liés à la recherche » peut être désactivée pour voir tous les rendez-vous ; la recherche reste disponible pour ajouter des tickets. Les conflits sont calculés même avec les interventions masquées par le filtre.

La carte et la création de tournée reprennent les résultats de la recherche. Les listes de matériel et de tournées affichent les éléments liés aux mêmes tickets. Les nouveaux tableaux de supervision utilisent une densité compacte ; les dispositions déjà enregistrées sont conservées.

## Collecte

Le formulaire SSO reconnu « Se souvenir de mon choix » + « Se connecter » est validé automatiquement. Une case déjà cochée reste cochée. La préférence est conservée dans le profil de navigateur dédié existant. Les formulaires ambigus, mots de passe et MFA restent disponibles pour une intervention humaine.

Une arrivée sur l’accueil déclenche une récupération bornée : profil Superviseur accessible, menu Assistance, lien Tickets observé sur la même installation, puis adresse complète de recherche. Les paramètres répétés sont conservés. Une racine de portail se résout vers la liste de tickets, sans collecter son tableau d’accueil. L’adresse de provenance initiale reste distincte de la destination résolue utilisée pour le repli paginé.

## Validation

- Compilation de l’application Windows ciblée depuis le Spark : aucune erreur ni avertissement.
- Ergonomie des modules réels dans Chromium sur Mac : 161 contrôles réussis.
- Runtime et isolation de session : 152 contrôles réussis ; les scénarios Chromium optionnels de cette suite sont ignorés explicitement.
- Planning via le pont C# réel : 33 contrôles réussis ; navigation : 42 ; espace ticket : 61.
- Automatisation de collecte : 47 contrôles réussis, complétés par 5 scénarios de collecte SSO persistée, racine filtrée, page racine explicitement mémorisée et formulaires/liens refusés.

Les données et portails sont synthétiques, les requêtes interceptées. La compilation croisée et Chromium ne constituent pas une validation native WinForms/WebView2 sur Windows, ni une connexion au SSO d’entreprise. Aucun pack n’a été publié.
