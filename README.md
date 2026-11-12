# GLPI Pilot 3.14.7 · Windows x64

![Logo GLPI Pilot](web/brand/glpi-pilot-logo.png)

**Un espace commun pour les tickets, les interventions et le pilotage des recherches GLPI accessibles par navigateur et SSO.** Calendrier, carte, tournées, météo, bibliothèque et données d’équipe sont réunis dans une seule interface. Aucun accès API ni accès direct à la base de GLPI n’est nécessaire. Le partage entre machines est facultatif : dossier réseau commun ou serveur PostgreSQL indépendant.

La version 3 utilise un **hôte C# WinForms avec WebView2** et une interface locale HTML/CSS/JavaScript. Les calculs, la pagination, les validations, la persistance et les accès au navigateur restent en C#. Depuis la 3.14.0, seul le pack allégé est publié : il utilise le runtime WebView2 Evergreen de Windows, ou à défaut le runtime fixe publié une seule fois pour tous les outils dans le [Socle Windows](https://github.com/M4D135/socle-windows/releases). Les pages de l’application n’ont besoin d’aucun site hébergé. Le modèle de migration est documenté dans le [contrat WebView](docs/WebView-contract.md), à partir de Domenia 2.

## Télécharger le pack

Les packs publiés sont disponibles dans les [Releases](https://github.com/M4D135/GLPI-Pilot/releases). **La validation Windows de la version 3 dépend du résultat du workflow de son commit**, indiqué dans le [rapport de vérification](docs/Rapport-verification.txt) et les journaux de la livraison. La présence de sources, d’un EXE compilé ou d’un ZIP local ne signifie pas que cette validation a réussi.

| Pack | Pour qui | Contenu à la racine |
| --- | --- | --- |
| **PROD-LIGHT — Windows x64** | Utiliser le logiciel | `GLPI-Pilot.exe` + `GLPI-Pilot-Admin.exe` + `wad/` sans `wad/runtime/` |
| **WebView2 du Socle** (dépôt [socle-windows](https://github.com/M4D135/socle-windows/releases), commun aux outils) | Seulement sur un poste sans WebView2 Evergreen | Runtime Microsoft fixe 156.0.4314.42 x64 ; GLPI Pilot propose de le choisir au démarrage, ou l’installe dans Réglages · Composants |

**Depuis une version antérieure à 3.6 :** effectuer ce premier passage par extraction dans un nouveau dossier local ; l’ancien vérificateur refuse un pack à deux EXE. Le profil utilisateur reste conservé.

Extraire **tout le ZIP** dans un dossier local puis ouvrir **GLPI-Pilot.exe**. Garder `wad` à côté. Aucun Python, Node.js, .NET ou installateur à ajouter. Le pack utilise le runtime WebView2 Evergreen installé sur Windows ; s’il manque, GLPI Pilot propose au démarrage de choisir le ZIP WebView2 du [Socle Windows](https://github.com/M4D135/socle-windows/releases), vérifié par son empreinte puis installé hors du dossier de l’application, donc conservé par les mises à jour. Un poste installé avec un ancien pack complet (antérieur à 3.14.0) passe au pack allégé par l’assistant de mise à jour dès qu’Evergreen ou ce runtime est présent. **Edge ou Chrome reste nécessaire pour la connexion au portail GLPI et son SSO.** Pour les sources et les tests, télécharger **Source code (zip)** sur la page de la release ou cloner le dépôt au tag de la version pour reconstruire les exécutables.

| Emplacement dans le pack | Rôle |
| --- | --- |
| `GLPI-Pilot.exe` | Application C# autonome, bibliothèques .NET et chargeur natif WebView2 intégrés |
| `GLPI-Pilot-Admin.exe` | Même application, avec maintenance SQLite et préparation PostgreSQL |
| `wad/web/` | Pages locales, styles, modules JavaScript et ressources visuelles |
| `wad/.playwright/` | Pilote Windows de la connexion au navigateur GLPI |
| `wad/docs/`, `wad/support/`, `wad/licenses/` | Guides, diagnostics et licences des composants |
| `wad/version.json`, `wad/SHA256SUMS.txt` | Version, provenance, dépendances et inventaire d’intégrité |

## 3.14.7 : liens de documentation vérifiés

L’application est identique à la 3.14.5 et à la 3.14.6. Dans les documents du pack, les liens vers Domenia 2 visent désormais sa branche principale, et deux liens vers des commits effacés sont remplacés par un texte. Tous les liens relatifs et tous les liens GitHub de la documentation ont été vérifiés.

## 3.14.6 : rapport de vérification à jour dans le pack

L’application est identique à la 3.14.5. Le pack embarque désormais le rapport de vérification à jour (`wad/docs/Rapport-verification.txt`), qui s’arrêtait à la 3.10.2. Les liens vers des tags, commits et runs disparus des notes de version sont aussi corrigés. Seule la dernière version reste publiée dans les Releases.

## 3.14.5 : menu Réglages façon Domenia

**Réglages** reprend le menu de configuration de Domenia : un rail de dix sections à gauche (Apparence, Pilotage, Collecte, Tickets, Historique, Équipe et partage, Configuration à diffuser, Mises à jour et journal, Démonstration, À propos et aide), une seule section affichée à la fois, quatre tuiles d’état en tête et **Rechercher un réglage** (touche <kbd>/</kbd>). Les réglages de collecte, de lecture des tickets, d’historique et de configuration d’équipe s’affichent dans leur section au lieu d’un panneau. Une valeur saisie et non appliquée reste en place quand on change de section.

## 3.14.4 : carte des tickets à l’ouverture et fond de rues fiable

Dans **Interventions → Carte**, la carte des tickets s’ouvre directement dès qu’un relevé est chargé, avec le nombre de tickets sur chaque repère ; la carte des interventions prévues reste à un bouton. Le fond de rues se charge seul à l’ouverture, y compris sur un écran large et en vue très dézoomée, et une tuile manquante ne le retire plus. Une configuration complète qui change aussi de source applique son référentiel de sites au relevé importé déjà affiché. Voir les [notes de version 3.14.4](docs/RELEASE-3.14.4.md).

## 3.14.3 : pilotage, disposition et actualisation

Actualiser GLPI conserve la page courante, y compris l’Accueil. Les widgets gardent leur taille et leur alignement après validation de la disposition ; les cartes par défaut sont rangées sur une grille régulière. Un filtre de widget devenu incalculable affiche son indisponibilité sans bloquer les autres cartes ni inventer un zéro. Voir les [notes 3.14.3](docs/RELEASE-3.14.3.md).

## 3.14.2 : accès au Socle sans lien GitHub dans l’application

Les messages d’installation du runtime WebView2 du Socle orientent désormais vers l’administrateur ou le dossier de diffusion, y compris au démarrage et dans Réglages · Composants. Les contrôles d’intégrité et le choix du ZIP restent inchangés. La figure de mise à jour dans les sources du guide administrateur montre maintenant le dossier réseau et le ZIP local ; les documents Word/PDF de `DOC/` restent les instantanés 3.13.6. Voir les [notes 3.14.2](docs/RELEASE-3.14.2.md).

## 3.14.1 : mises à jour sans GitHub

L’assistant de mise à jour ne contacte plus GitHub : un pack s’installe depuis le dossier réseau partagé ou depuis un ZIP local, avec les mêmes contrôles, la même sauvegarde et le même retour arrière. Une configuration complète exportée par une version antérieure reste importable. Voir les [notes 3.14.1](docs/RELEASE-3.14.1.md).

## 3.14.0 : un seul pack publié, le runtime WebView2 dans le Socle

Seul le pack **PROD-LIGHT** est publié. Le runtime WebView2 fixe, identique pour Pesto, FILPRO, Domenia et GLPI Pilot, est publié une seule fois dans le [Socle Windows](https://github.com/M4D135/socle-windows/releases). Sur un poste sans WebView2 Evergreen, GLPI Pilot propose au démarrage de choisir ce ZIP ; il est vérifié par son empreinte et installé hors du dossier de l’application, donc conservé par les mises à jour. Un poste installé avec un ancien pack complet passe au pack allégé dès qu’Evergreen ou le runtime du Socle est présent. Voir les [notes 3.14.0](docs/RELEASE-3.14.0.md).

## 3.13.8 : chaîne de publication fiabilisée

L’application se comporte comme en 3.13.7. Les deux packs PROD et PROD-LIGHT qualifiés sur le runner Windows sont relayés vers le runner de publication avec contrôle de leur empreinte, et la vérification de l’écran « Utiliser ce tableau » ne se fige plus. Voir les [notes 3.13.8](docs/RELEASE-3.13.8.md).

## 3.13.7 : accueil disponible pendant le chargement

On peut ouvrir les réglages ou les autres pages dès que l’accueil s’affiche. Tant que le calendrier n’a pas répondu, son compteur affiche un tiret ; sa réponse ne peut pas rouvrir la page quittée. Les téléchargements Windows sont PROD et PROD-LIGHT ; les sources et les tests restent disponibles depuis le tag. Voir les [notes 3.13.7](docs/RELEASE-3.13.7.md).

## 3.13.6 : pack allégé PROD-LIGHT et qualification en avance rapide

Le pack **PROD-LIGHT** reprend le pack PROD sans le runtime WebView2 embarqué (environ 300 Mo de moins) et démarre sur le runtime Evergreen installé sur Windows ; l’assistant de mise à jour propose l’édition du poste et ne mélange jamais pack complet et pack allégé. Les figures des guides affichent la version réelle et sont recadrées par un outil reproductible ; deux jeux d’essai datés en dur sont désormais relatifs au jour du test ; une branche de version qualifiée puis poussée en avance rapide sur main se publie sans seconde qualification. Correction : l’import d’une configuration complète exportée par une version 3.12 ou 3.13 était refusé depuis la 3.12.0. Voir les [notes 3.13.6](docs/RELEASE-3.13.6.md).

## 3.13.5 : thème uniforme et démonstration dans Réglages

La barre du haut porte le même sélecteur de thème que Domenia, FILPRO et PESTO : quatre icônes pour « Thème clair », « Thème sombre », « Contraste élevé » et « Thème : suivre Windows », aussi proposées dans la palette Ctrl K et dans Réglages. Le contraste élevé (noir, blanc, cyan et jaune) reste propre à chaque poste. La démonstration s’ouvre, se réinitialise et se quitte uniquement depuis la carte **Démonstration** de **Réglages**, avec une confirmation. Voir les [notes 3.13.5](docs/RELEASE-3.13.5.md).

## 3.13.4 : fiches ouvertes directement sur leur onglet Ticket

La collecte demande chaque fiche directement sur son onglet « Ticket » et le lit sans clic ; elle ne le réactive plus à la fin de chaque fiche. Au banc GLPI 9.5, une collecte de 30 tickets passe de 13 min 30 s à 11 min 24 s. Les fiches lues par la 3.13.2 ou la 3.13.3 restent conservées tant que GLPI ne modifie pas le ticket. Voir les [notes 3.13.4](docs/RELEASE-3.13.4.md).

## 3.13.3 : collecte des tickets plus rapide

La lecture d'un ticket n'ouvre plus les onglets GLPI sans intérêt pour la bibliothèque (statistiques, coûts, projets, problèmes, changements, base de connaissances, vue « Tous ») ; suivis, tâches, solutions, validations, éléments, documents et historique restent lus. Au banc GLPI 9.5, une collecte de 30 tickets passe de 29 à 13 minutes. Une fiche que GLPI rouvre sur un onglet vide n'attend plus le délai maximal. Les fiches déjà conservées le restent tant que GLPI ne modifie pas le ticket ; passez d'abord le poste collecteur en 3.13.3 et synchronisez la bibliothèque partagée. Voir les [notes 3.13.3](docs/RELEASE-3.13.3.md).

## 3.13.2 : collecte des tickets sur GLPI 9.5

La collecte des tickets lit désormais aussi les pages de GLPI 9.5 ; elle restait auparavant sur la liste puis s'arrêtait sans ticket. Quand une liste n'est pas reconnue, la fenêtre de collecte en donne la raison, et la lecture de l'index affiche sa progression. Le menu de gauche se replie comme dans Domenia, Pesto et FILPRO. Voir les [notes 3.13.2](docs/RELEASE-3.13.2.md) et les guides dans `DOC/`.

## 3.13.1 : finition du partage et publication vérifiée

Le panneau de partage omet correctement la programmation désactivée et les messages facultatifs du bloc « Données communes », sans texte « null ». Les boutons restent utilisables pendant une actualisation. La qualification complète produit les packs sans les publier ; leur inspection précède désormais un déclenchement distinct qui vérifie le commit, le run réussi et les empreintes des deux originaux. Voir les [notes 3.13.1](docs/RELEASE-3.13.1.md).

## 3.13.0 : ergonomie, tickets et collecte reprenable

Le look et les animations sont conservés. Widgets agrandissables, carte qui laisse défiler la page, matériel compact, filtres manager et commandes de tournée simplifient les parcours existants. Suivi, tâche et affectation gardent inspection, aperçu et confirmation unique ; statut, priorité et résolution ouvrent la fiche GLPI native.

**Données → Collecte et reprise** commande le vrai moteur : mise à jour différentielle des contenus, pause, arrêt et reprise du journal SQLite, y compris après plusieurs interruptions. L’index est revalidé ; les dates ambiguës, contenus partiels et copies trop anciennes imposent une nouvelle lecture. Programmation désactivée par défaut, cadence bornée et verrou partagé séparent collecte GLPI et synchronisation d’équipe.

Le paquet interne **`.glpilibrary.zip`** permet une fusion contrôlée sur un autre poste, avec aperçu, empreintes et conservation des conflits. Non chiffré et potentiellement privé, il doit rester sur un canal interne protégé. Il n’est jamais inclus dans les packs logiciels publics.

Sauvegarder les bases et réglages, puis aligner les clients utilisant ces nouveaux verrous et paquets en **3.13.0 ou version ultérieure**. La disponibilité des exécutables dépend de la réussite du workflow du commit livré. Voir le [guide de collecte](docs/Collecte-des-tickets.md), les [notes 3.13.0](docs/RELEASE-3.13.0.md) et le [bilan de finalisation](docs/FINALISATION-3.13.0.md).

## 3.12.0 : préparer le matériel et les fiches terrain

**Interventions → Préparer le matériel** relie un catalogue personnalisable aux tickets, visites et tournées, sans gérer de stock. Les quantités prévues et préparées, les préparateurs et les consignes sont mémorisés dans la base locale et partagés lorsque la synchronisation d’équipe est activée. Les références se créent et s’archivent sans réécrire les anciennes fiches.

**Pilotage → Matériel prévu** propose des filtres de site, équipe, technicien, tournée, période et état. Les besoins sont rapprochés du planning connu du poste : une annulation ne reste pas artificiellement dans la liste de départ. Les fiches sans source vérifiable restent conservées et signalées, hors des totaux vérifiés. L’absence d’un ticket dans un relevé n’est jamais interprétée comme sa suppression.

Imprimer une sélection ou **la tournée entière**, y compris les visites sans matériel renseigné. Les préparations des tickets liés et les besoins complémentaires des visites restent distincts et comptés une fois. Des kits peuvent être proposés à partir des préparations locales antérieures, toujours avec confirmation. Les conflits se comparent et se résolvent explicitement, avec conservation des deux versions.

**Espace partagé : mettre à jour tous les postes participants vers 3.12.0 ou plus avant d’utiliser ces nouveaux documents.** Les anciens clients ne connaissent pas leur schéma. Les données opérationnelles ne sont pas intégrées au JSON de déploiement ni aux packs publics.

Voir le [guide de préparation](docs/Preparation-terrain.md) et les [notes 3.12.0](docs/RELEASE-3.12.0.md). Les exécutables publiés restent conditionnés aux contrôles du commit et de leurs packs Windows.

## 3.11.1 : restaurer le Live après consultation du cache

Le sélecteur permanent propose GLPI Live, cache local et CSV/Excel ; les écrans de collecte relancent GLPI à la demande et les autres vues gardent leur actualisation locale. La consultation de la copie datée n’efface ni les paramètres ni le dernier relevé en cas d’échec. Cette version conserve toutes les améliorations 3.11.0.

## 3.11.0 : placer les widgets et conserver sa configuration complète

Dans **Pilotage → Personnaliser la vue**, glisser la poignée **Déplacer** pour choisir une position, y compris dans une autre section. **Petit / Moyen / Grand** propose trois formats ; la poignée du coin ajuste largeur et hauteur. Chaque carte conserve sa propre hauteur : un grand graphique n’étire plus les petits indicateurs. Les flèches du clavier et Échap offrent les mêmes réglages et l’annulation du geste.

Les positions, tailles, sections, filtres, seuils et présentations préparées sont enregistrés et transférables. **Réglages → Définir comme configuration par défaut** écrit `wad/GLPI-Pilot.deployment.json`, avec les **communes, sites, bâtiments, alias et champs personnalisés** enregistrés. Les données de tickets et secrets restent exclus. Ce JSON et les catalogues de sites sont privés : ils ne sont pas publiés dans les packs GitHub.

Voir les [notes 3.11.0](docs/RELEASE-3.11.0.md) et le [guide de configuration complète](docs/Configuration-deploiement.txt). La publication des exécutables reste conditionnée à la validation Windows et PostgreSQL du commit livré.

## 3.10.2 : actualiser la vue affichée

Dans **Interventions**, le bouton **Actualiser** du bandeau recharge le planning, la carte ou les tournées affichés. Il conserve l’onglet, la date et les filtres courants. Sur la carte, la recherche, le lieu choisi, le cadrage et les tickets cochés sont conservés tant que le relevé et le périmètre restent compatibles. Un référentiel fermé ne se rouvre plus à l’actualisation ni après un aller-retour entre Carte et Planning.

L’actualisation utilise le relevé déjà chargé. Une nouvelle collecte GLPI ou un nouvel import de tickets reste une action explicite. Les référentiels propres à une organisation restent locaux : le dépôt et les nouvelles releases contiennent les outils d’import, sans pack de données Sites. Voir les [notes 3.10.2](docs/RELEASE-3.10.2.md).

## 3.10.1 : GLPI en premier, collectes groupées et défaut dans wad

**Données → GLPI en direct** affiche d’abord la recherche GLPI, le navigateur et la collecte. **Lancer la collecte** utilise la recherche affichée. L’import CSV / Excel et le dernier relevé conservé restent disponibles dans les parcours secondaires, avec leur provenance réelle et leur date ; la démonstration s’ouvre depuis **Réglages**.

**Collecter plusieurs recherches** prépare une collecte groupée : choisir les recherches mémorisées, puis lancer leur collecte successive. Sans recherche enregistrée, le panneau explique comment en ajouter dans **Mes recherches**. Un bilan récapitule les réussites, les résultats à vérifier et les échecs de chaque recherche. Les tickets de plusieurs recherches ne sont pas fusionnés. Les archives détaillées dépendent du réglage d’historique.

**Définir comme configuration par défaut** écrit maintenant **`wad/GLPI-Pilot.deployment.json`**. Le démarrage privilégie cet emplacement et reconnaît encore l’ancien fichier placé à côté des EXE lorsque celui de `wad` est absent. L’assistant de mise à jour et de retour arrière de 3.10.1 préserve le défaut local. Voir les [notes 3.10.1](docs/RELEASE-3.10.1.md) et le [guide de déploiement](docs/Configuration-deploiement.txt).

## 3.10.0 : composer ses diapos, corriger les sites et alléger les animations

**Pilotage → Présenter** propose un éditeur de diapos 16:9 : plusieurs widgets sur une page, déplacement libre, poignées de redimensionnement, grille facultative, position et taille précises, duplication et ordre des pages. La composition est mémorisée avec le tableau et transférable avec sa configuration. Le PDF reprend les pages ; HTML, Excel et CSV conservent aussi les tableaux détaillés sélectionnés. Voir le [guide de présentation](docs/Presentation.md).

Les animations se règlent uniquement dans **Réglages → Confort et présentation**. Les anneaux décoratifs des widgets laissent place à un reflet de bord discret et un léger relief au survol ; les scènes météo restent liées à leurs états.


Dans **Interventions → Carte**, **Corriger le site** permet de choisir le bon site depuis une ligne, notamment dans **Localisation ambiguë** ou **Lieu non reconnu**. Les cases du tableau permettent d'appliquer le même site à plusieurs tickets. La recherche accepte les noms et les alias du référentiel enregistré ; la commune suit automatiquement le site choisi. Une correction peut être modifiée ou retirée pour retrouver la détection automatique.

Les corrections sont mémorisées par source et identifiant de ticket. Elles sont utilisables depuis le dernier relevé en cache, sans le CSV d'origine, avec une actualisation immédiate de la vue de travail. Les cellules importées et les archives restent conservées. **Actualiser** recharge la vue ; pour appliquer de nouvelles règles automatiques à l'ensemble d'un relevé conservé, réimporter le CSV/Excel des **tickets** ou lancer une nouvelle collecte. Voir les [notes 3.10.0](docs/RELEASE-3.10.0.md).

Le complément des sites renseigne désormais tous les codes postaux des 13 communes, y compris les communes qui en possèdent plusieurs, sans remplacer les champs personnalisés déjà renseignés.

## 3.9.0 : seuils par sélection, pourcentages et sites vérifiés

Dans chaque carte météo de **Pilotage**, **Seuils pour cette sélection** permet de créer, modifier ou supprimer les seuils du périmètre affiché. Choisir des valeurs absolues ou, pour un décompte de tickets compatible, des **pourcentages avec une base explicite**. Une règle en pourcentage peut suivre toutes les sélections de ce widget ; une exception enregistrée pour une sélection précise reste prioritaire. La sélection et ses réglages sont conservés dans la configuration, retrouvés au redémarrage et inclus dans l’export/import ainsi que la configuration par défaut. Sans règle correspondante, le comportement prudent du périmètre filtré reste en place.

Les bases conseillées utilisent les colonnes disponibles : tickets actifs, tous les tickets, priorités reconnues, dates d’ouverture ou de modification exploitables, échéances réellement présentes. La carte conserve son nombre de tickets et affiche le calcul du ratio. Une base nulle, absente ou incompatible ne produit aucune météo favorable artificielle. Voir le [guide des seuils et des bases de calcul](docs/Seuils-meteo.txt) et les [notes 3.9.0](docs/RELEASE-3.9.0.md).

L’import **Fusion=Completer** conserve les champs déjà renseignés et ajoute les alias manquants. Les noms, rattachements et données de provenance restent dans le référentiel local de l’utilisateur. Les fichiers propres à une organisation se diffusent séparément et ne sont plus fournis sur GitHub.

## 3.8.1 : enregistrer les sites pendant la consultation hors connexion

L'import du référentiel peut maintenant être enregistré lorsqu'un cache local ou une archive est affiché. Le logiciel enregistre les sites dans la configuration de travail ; le relevé conservé garde ses données et son interprétation. L'aperçu et le message final précisent cette distinction. Les sites enregistrés sont ensuite inclus dans **Définir comme configuration par défaut**. Voir les [notes 3.8.1](docs/RELEASE-3.8.1.md).

## 3.8.0 : configuration complète à la diffusion

Configurer le logiciel dans le dossier PROD puis choisir **Définir comme configuration par défaut** dans Réglages : toutes les options sont écrites dans `wad/GLPI-Pilot.deployment.json` depuis la version 3.10.1, prêt à diffuser avec le dossier. Un profil neuf l’applique automatiquement ; un profil existant propose un aperçu avant remplacement, avec sauvegarde et choix mémorisé pour cette version et ce fichier. L’import manuel reste accessible. Les options publiques sont transférées, les données personnelles et secrets restent locaux. Voir le [guide de déploiement](docs/Configuration-deploiement.txt) et les [notes 3.8.0](docs/RELEASE-3.8.0.md).

## 3.7.0 : réunion, sélection et dernières données hors connexion

Dans **Pilotage → Présenter**, choisir les indicateurs, graphiques et tableaux, puis leur ordre. La vue de réunion conserve ce périmètre daté. Les exports de cette sélection reprennent les mêmes éléments, leurs filtres et leur provenance ; les données absentes restent inconnues. Quitter la présentation retrouve la composition de travail. Les exports de listes de tickets restent disponibles depuis **Tickets** avec les colonnes et filtres choisis.

Le dernier import réel CSV/Excel ou relevé GLPI réussi est conservé automatiquement sur ce poste. Le prochain démarrage le retrouve sans fichier original ni connexion GLPI, avec son interprétation et sa date d’observation. **Accueil** et **Données** affichent cette copie hors connexion ; une nouvelle collecte reste une action explicite. La démonstration possède son espace séparé. Voir les [notes 3.7.0](docs/RELEASE-3.7.0.md).

## 3.6.0 : sources, administration et diffusion réseau

L’import devient une source de travail réelle avec un accès limité aux champs de la liste. La démonstration dispose de sa propre base fictive complète, avec des interventions et tournées déjà planifiées ou terminées. Voir le parcours ci-dessous et les [notes 3.6.0](docs/RELEASE-3.6.0.md).

Le pack contient deux lanceurs. Retirer `GLPI-Pilot-Admin.exe` avant de distribuer l’application aux agents ; la Standard refuse les commandes Admin et la mise à jour ne réintroduit pas ce lanceur. L’Admin ajoute les sauvegardes et remises à zéro SQLite ainsi que la préparation d’une base PostgreSQL dédiée. Les patchs de sites se diffusent séparément, avec aperçu avant/après et identifiants conservés. Voir le [guide de diffusion et d’administration](docs/Diffusion-et-administration.txt).

## 3.5.1 : choisir son identité directement

Le bouton **Choisir mon identité**, dans le bandeau supérieur et sur l’**Accueil**, ouvre le panneau **Mon identité** depuis l’écran courant. Rechercher son nom ou son identifiant parmi les données chargées et les participants du planning, puis cliquer **C’est moi**. Le choix est mémorisé dans le profil local ; le bouton affiche ensuite le nom choisi.

Si le nom est absent, **Mon nom n’est pas dans la liste** propose un seul champ : **Nom tel qu’il apparaît dans les tickets**. Saisir le nom ou l’identifiant utilisé dans les affectations, puis **Enregistrer mon identité**. Une correspondance existante et non ambiguë est réutilisée ; les anciennes identités manuelles ne sont pas fusionnées automatiquement.

Changer ou retirer son identité conserve les interventions et les participants enregistrés. Le choix se fait sans quitter l’écran ni perdre la date du planning. Les valeurs non appliquées dans **Réglages** demandent une décision avant le changement ; annuler conserve la saisie. **Mes tickets** propose ce même choix si nécessaire, puis applique le filtre personnel. En simulation, le choix reste dans l’espace de test. Voir les [notes 3.5.1](docs/RELEASE-3.5.1.md).

### Retrouver le mouvement de Domenia

Sur l’**Accueil**, le bouton **Animations actives** ou **Animations en pause** ouvre un aperçu en direct. **Activer les animations Domenia** applique immédiatement le mode **Expressives** et mémorise ce choix. Le panneau indique clairement si Windows ou le mode de contraste suspend les effets. **Suivre Windows** et **Réduire les animations** restent disponibles au même endroit.

Le véritable en-tête de l’Accueil reçoit les orbes colorées, l’anneau lumineux et la parallaxe. Les cartes apparaissent en cascade avec du déplacement ; les reflets et le halo suivent la souris ; les courbes d’historique et de Pareto se dessinent à leur arrivée. Les décors se rétablissent après un recalcul de leur conteneur. Les chiffres restent des valeurs exactes et les surfaces contenant tableaux, cartes ou contrôles de déplacement gardent leur géométrie.

Dans **Réglages → Confort et présentation**, l’aperçu montre l’état réellement appliqué. Le choix **Animations → Expressives → Appliquer** permet également d’activer ces effets lorsque Windows demande de réduire les mouvements. Les préférences existantes restent conservées.

## Calendrier commun et partage entre postes

**Il n’y a pas de séparation Technicien / Chef d’équipe.** Toutes les fonctions restent accessibles dans la même interface. Le choix **C’est moi** filtre les tickets et interventions d’une identité déclarée ; il ne crée ni compte, ni mot de passe, ni permission GLPI Pilot.

Le menu propose quatre espaces principaux, avec les deux accès utilitaires en bas :

| Espace | Travail proposé |
| --- | --- |
| **Accueil** | Priorités du périmètre ou de sa file personnelle, alertes et interventions des sept prochains jours |
| **Tickets** | Liste et dossier réunis, filtres cumulables d’équipes et de techniciens, notes, rappels et planification d’une sélection |
| **Interventions** | Calendrier jour, semaine, ressources, mois ou agenda ; carte des rendez-vous et tickets ; tournées conservées |
| **Pilotage** | Météo, stock, attention, équipes, demandes, contribution et notes dans une seule composition |
| **Données** | Collecte GLPI, recherches mémorisées, import CSV/Excel, relevés partagés, qualité, bibliothèque et contributions |
| **Réglages** | Présentation, météo, automatismes, stockage, partage entre postes, mises à jour et diagnostic |

### Planifier les interventions

Créer une intervention libre ou la préparer depuis un ou plusieurs tickets. Les rendez-vous conservent leurs participants, dates, horaires, site ou adresse, consignes, matériel et rappels. Le calendrier fonctionne même sans relevé chargé. Les absences, réunions et maintenances peuvent y être ajoutées.

Les vues jour, semaine, ressources, mois et agenda lisent les mêmes événements. Déplacement, redimensionnement, annulation et décalage d’un ensemble de rendez-vous utilisent les révisions du moteur. Une récurrence se modifie sur une occurrence, à partir d’une occurrence ou sur la série. Les rendez-vous restent liés à leurs références même lorsqu’un ticket sort du relevé courant.

Les contrôles croisent les rendez-vous, les étapes de tournées, les trajets déclarés, les disponibilités, capacités et ouvertures des sites. Les informations absentes restent inconnues. Une décision de passer outre un conflit est explicite et garde les avertissements visibles. Les créneaux proposés tiennent compte du périmètre de contrôle annoncé ; ils ne constituent pas une réservation GLPI.

Une intervention à plusieurs participants compte une fois ; les heures-technicien comptent la durée de chaque participant. Terminer une intervention ne clôture pas automatiquement ses tickets dans GLPI. Les exports ICS et fiches HTML imprimables permettent de réutiliser le planning. Voir le [guide du calendrier](docs/Calendrier-et-interventions.md).

### Partager entre sessions et machines — facultatif

Dans **Réglages → Partage entre postes**, choisir :

- **Sur ce poste uniquement** pour conserver le fonctionnement autonome ;
- **Dossier réseau partagé** pour utiliser le même dossier accessible depuis les comptes Windows concernés ;
- **Serveur PostgreSQL** pour une base centrale administrée séparément.

Le dossier réseau contient un registre d’échanges. **Les bases SQLite et les index de lecture restent locaux à chaque poste.** Les droits du dossier sont ceux de Windows ; aucune gestion de comptes applicatifs supplémentaires n’est imposée.

Le partage transmet le calendrier, les ressources, les tournées avec leur identité conservée, les compétences et capacités, la répartition préparée, les sites et termes de détection, les corrections de localisation, les règles météo, tableaux de bord et requêtes. Les relevés recueillis conservent toutes leurs colonnes source ; ils sont disponibles dans **Données → Relevés partagés**. La bibliothèque conserve séparément les fiches et contenus effectivement recueillis.

Chaque poste garde une copie utilisable hors connexion. Les échanges automatiques ont lieu pendant que l’application est ouverte, toutes les cinq minutes par défaut. **Intervalle de synchronisation (minutes)** règle la fréquence de 1 à 1 440 minutes ; décocher **Synchroniser automatiquement** désactive les échanges périodiques. Une édition en cours peut les différer. **Synchroniser** reste disponible pour un passage manuel immédiat. L’état indique la dernière réussite, les éléments en attente et les erreurs. Un conflit conserve les deux versions pour comparaison et décision. Les opérations groupées du calendrier sont transmises comme un ensemble indivisible.

Les mots de passe, profils de connexion GLPI/SSO, identité personnelle, notes privées, rappels personnels et préférences d’affichage restent locaux. Le dossier partagé ne reproduit pas les droits de GLPI : les personnes autorisées à ce dossier peuvent lire ses données communes. Tous les clients d’un même espace doivent utiliser une version compatible **3.5.0 ou ultérieure**. Le [guide de partage](docs/Bibliotheque-et-partage.md) précise la mise en place, les sauvegardes et les limites.

### Carte, météo et lecture des tickets

**Pilotage partage un même périmètre** entre ses sept sections. Cumuler plusieurs équipes ne compte pas deux fois un ticket commun. Chaque mesure ouvre ses lignes exactes. **Personnaliser la vue** règle les cartes, leurs dimensions et leur ordre ; les compositions existantes sont conservées.

La carte intégrée affiche les tickets localisés et les visites prévues. Les rues OpenStreetMap se chargent par défaut, y compris en simulation ; le fond vectoriel local prend le relais hors connexion. Les lieux inconnus ou sans coordonnées restent visibles dans les listes. Une position de commune est explicitement approximative. Les affectations et visites prévues ne constituent pas une position GPS de la personne.

Les filtres d’équipes dédupliqués et la météo du périmètre introduits en [3.4.1](docs/RELEASE-3.4.1.md) restent en place. Le design inspiré de Domenia v2 renforce les scènes météo : soleil et rayons mobiles, nuages sur deux plans, pluie et ondulations visibles dans les petites cartes. Les widgets et le calendrier reçoivent des médaillons animés, des halos et des reflets, avec des entrées en cascade. Les chiffres et la longueur des barres restent fixes. Les réglages **Expressives / Suivre Windows / Réduites** et la pause hors écran restent disponibles. Assistant, Lectures ciblées et Effectifs des sites restent masqués par défaut.

La pagination réutilise un cache borné des index de lignes, les anciennes lectures de liste sont annulées lorsque le filtre change, et les vues mémorisent leur position. Les chiffres sont calculés depuis le relevé effectivement chargé ; aucune échéance, présence ou donnée absente n’est inventée.

Voir les [notes 3.5.0](docs/RELEASE-3.5.0.md), le [guide actuel](docs/Guide-GLPI-Pilot.html) et le [rapport de vérification](docs/Rapport-verification.txt).

## Premier réglage, puis un clic

1. Dans **Données**, renseigner l’adresse complète de la recherche GLPI, filtres compris, puis choisir Edge ou Chrome. Les profils de sources et leurs réglages sont accessibles dans le même espace.
2. Cliquer **Lancer la collecte**. Le navigateur dédié reste réduit ; il s’affiche temporairement lorsqu’une validation SSO visible demande une intervention, puis la collecte reprend discrètement.
3. Vérifier les contrôles de données, puis ouvrir **Tickets**, **Interventions** ou **Pilotage**. Les chiffres utilisent le même relevé.
4. Cliquer **Choisir mon identité** dans le bandeau supérieur ou sur **Accueil**, rechercher son nom et choisir **C’est moi**. Si nécessaire, utiliser **Mon nom n’est pas dans la liste** et recopier le nom présent dans les affectations des tickets. Le référentiel de sites se gère dans **Interventions → Carte → Carte des tickets → Gérer le référentiel**.
5. Réutiliser les sources et réglages enregistrés aux ouvertures suivantes. **Réglages → Seuils, équipes et règles météo** permet d’adapter le pilotage au service.

**Afficher le navigateur** redonne la main à tout moment ; cet affichage manuel reste visible après connexion. L’utilisateur actionne les formulaires, la case « Se souvenir de moi » et le bouton de connexion. Une interface GLPI personnalisée peut demander l’apprentissage initial du tableau ou des sections.

## Tester tout le logiciel avec un fichier local

Choisir **Importer en simulation** sur Accueil, **Importer CSV / Excel** dans Données, ou utiliser **Ctrl+K**. CSV, TSV, TXT, XLSX et XLSM sont acceptés. Pour Excel, choisir la feuille et la ligne d’en-têtes ; un profil compatible peut reprendre la météo, les équipes et les sites du classeur. Les valeurs enregistrées sont lues sans exécuter les macros ni les formules. L’ancien XLS doit être enregistré en XLSX ou CSV.

Tous les espaces et leurs outils utilisent l’extrait importé dans un contexte isolé : fiches, actions simulées, météo, bibliothèque, requêtes, sites, calendrier, tournées, partage simulé et mise à jour virtuelle. Les données absentes du fichier restent signalées comme absentes. Annuler le choix du fichier conserve le travail courant. **Quitter la simulation** retrouve le relevé et le profil habituels. Les rues demandent Internet ; le fond local conserve les repères hors ligne.

Les seuils météo initiaux sont **100/200** pour le stock, **50/100** pour incidents ou demandes et **10/20** par équipe ou catégorie. Le dépassement est strict. Les règles personnalisées, les cartes masquées et leur ordre de priorité restent conservés.

## Historique des versions précédentes

Les descriptions ci-dessous documentent leur version d’origine. Les parcours actuels de **3.10.2** sont ceux présentés ci-dessus ; les [notes 3.5.0](docs/RELEASE-3.5.0.md) détaillent l’introduction du calendrier commun et du partage entre postes. Les [notes 3.3.4](docs/RELEASE-3.3.4.md) conservent le détail de la première carte intégrée.

## Nouveautés de la version 3.3.3

**Le mode simulation permet de travailler dans tous les écrans à partir d’un CSV.** Les effectifs des sites peuvent être saisis et datés dans cet espace ; les ratios sont recalculés sans modifier le profil habituel. Le référentiel du profil est repris au début de la simulation, avec ses communes, sites et adresses. Les valeurs absentes du CSV restent indiquées comme indisponibles.

**Le menu regroupe les rubriques dans des sections repliables.** Assistant et Lectures ciblées sont masqués par défaut, puis réactivables dans la personnalisation. Effectifs devient **Effectifs des sites**, accessible depuis les outils de Sites et masqué du menu par défaut. Les épingles, les préférences de classement, le clavier et les animations réduites sont conservés.

**Les sites peuvent recevoir leur commune, adresse et coordonnées depuis un fichier local `wad/sites.csv`.** Pour un profil déjà initialisé, une nouvelle version géographique du fichier complète les champs vides sans écraser les valeurs locales ni recréer les sites supprimés. Ce fichier reste propre à l’utilisateur ; aucun organisme ni référentiel particulier n’est intégré au moteur ou aux nouveaux packs publics.

La première intégration cartographique de 3.3.3 est remplacée en 3.3.4 par les cartes directement intégrées décrites ci-dessus.

**Les aperçus de lignes sont plus lisibles.** Les tableaux ouverts depuis les dashboards, requêtes et analyses utilisent une fenêtre large, des largeurs de colonne adaptées et des cellules alignées en haut. Les textes longs occupent au maximum trois lignes dans l’aperçu ; cliquer une valeur ouvre son contenu complet et son interprétation. Les exports conservent les données complètes.

**Sites distingue correctement une localisation reconnue d’un lieu à vérifier.** Un site unique est reconnu même sans commune ou coordonnées renseignées. Les titres absents, les lieux inconnus et les ambiguïtés restent dans le compteur à vérifier. Lorsque le repérage est disponible, ces deux compteurs couvrent ensemble le stock actif du périmètre. Une commune reconnue sans site précis reste dans la ligne des sites non renseignés du tableau de planification.

**Le filtre existant permet de cumuler plusieurs équipes et groupes.** Dans **Groupes**, **Plus anciens**, **Suivi agents**, **Agents actifs** et **Sites**, ouvrir **Équipes et groupes**, cocher les choix puis cliquer **Appliquer**. Les sélections sont réunies par **OU** : une même ligne commune à plusieurs choix est retenue une seule fois. Les cartes, répartitions, préconisations, listes de tickets et exports utilisent ce même périmètre. **Toutes** retire cette restriction ; chaque rubrique conserve sa sélection pendant la session, séparément dans l’espace habituel et la simulation.

Les règles de détection dans les titres sont conservées : noms et variantes configurés, mots entiers, normalisation des accents et de la ponctuation, ambiguïtés explicites. La correction porte sur les compteurs et le périmètre présenté. Voir les [notes 3.3.3](docs/RELEASE-3.3.3.md) et le [rapport de vérification](docs/Rapport-verification.txt) pour l’état de validation du commit livré.

## Nouveautés de la version 3.3.2

**Lieux et sites affiche tous les termes de détection d’un site : son nom et ses alias.** Le formulaire précise que le nom est reconnu automatiquement ; les variantes saisies le complètent. Après l’application d’un profil de classeur, un brouillon du référentiel qui n’a pas été modifié reprend automatiquement les nouveaux réglages. Un brouillon déjà modifié est conservé et son conflit reste signalé.

Un distributeur peut ajouter le fichier facultatif **`wad/sites.csv`**, au format UTF-8 **`Site;Termes de détection`**. Au premier lancement où ce fichier est présent et valide, GLPI Pilot reprend discrètement les sites dans `%LOCALAPPDATA%\GLPI-Pilot\settings.json`. Ils sont ensuite visibles dans **Lieux et sites** et modifiables comme ceux saisis manuellement. L’initialisation est assurée directement par l’application.

**L’import automatique ne se fait qu’une fois par profil utilisateur.** Les modifications et suppressions réalisées ensuite dans l’application sont conservées aux démarrages suivants. En l’absence de `wad/sites.csv`, le démarrage est normal et l’initialisation reste disponible si le fichier est fourni plus tard.

Le logiciel et le pack public restent génériques. Le tableau de sites est un fichier local séparé, ajouté à la distribution privée selon les besoins. L’import CSV du référentiel dans l’interface reste disponible ; cette version n’ajoute pas de sélection Excel à cet écran. Le format initial est décrit dans les [notes 3.3.2](docs/RELEASE-3.3.2.md) et son enrichissement géographique dans les [notes 3.3.3](docs/RELEASE-3.3.3.md).

## Nouveautés de la version 3.3.1

La rubrique de travail personnel s’intitule désormais **Mon espace**. Ce libellé est repris dans la navigation, les actions, les notifications et les guides. Les fonctions et les données existantes sont conservées. Voir les [notes 3.3.1](docs/RELEASE-3.3.1.md).

## Nouveautés de la version 3.3.0

**Le pilotage inspiré des rapports Excel est intégré directement au menu.** Neuf rubriques sont disponibles dès l’ouverture, avec des modèles génériques et des calculs alimentés par le relevé courant. Les tableaux personnels existants restent accessibles dans **Mes tableaux**.

| Rubrique native | Informations et actions |
| --- | --- |
| **Météo** | Grand total, cartes par catégorie et par équipe, états soleil/nuage/pluie, seuils, fraîcheur et analyse en cours |
| **Pilotage** | Stock actif, attente, demandes/incidents, attribution, répartitions et matrices de statuts |
| **Groupes** | Volumes par groupe et statut, totaux, accès aux tickets et périmètres d’équipe |
| **Catégories** | Volumes, parts du stock et détail des catégories, avec filtres de branche explicites |
| **Plus anciens** | Dix plus anciens du périmètre et cinq par groupe, âges calendaires, dates indisponibles et fiches |
| **Suivi agents** | Comparaison des tickets rédigés et attribués, listes dynamiques ou participants configurés |
| **Agents actifs** | Suivis, tâches, intersection et union distincte par personne |
| **Sites** | Stock par site, identifiants, sites à vérifier et préparation des interventions |
| **Paramètres du pilotage** | Correspondances météo, ordre des équipes, seuils, visibilité, fraîcheur, notes et reprise d’un profil de classeur |

La météo de stock conserve les règles du rapport : dépassement **strict** des seuils, catégories et descendants délimités par `>`, première équipe correspondante dans l’ordre configuré, y compris lorsqu’une carte est masquée. Dans la vue Groupes, un ticket participe à chacun de ses groupes ; la météo d’équipe applique sa répartition exclusive. Le rappel de fraîcheur et le texte d’analyse restent distincts des couleurs de volume. Les valeurs inconnues ou une source insuffisamment fiable restent signalées.

### Deux sources réelles et une démonstration complète

**Importer CSV / Excel** charge une autre source de données dans l’espace habituel. Elle peut servir de solution de secours lorsque la collecte par navigateur est indisponible. Les listes, filtres, exports, indicateurs, cartes, tournées et interventions utilisent les lignes réellement présentes dans le fichier. Le planning créé dans ce mode reste du travail normal et se conserve après le changement de source.

CSV, TSV, TXT, XLSX et XLSM sont acceptés. Pour Excel, choisir la feuille et la ligne d’en-têtes ; un classeur compatible peut proposer **Tickets / ligne 4**. Le profil **Paramétrage** facultatif fournit météo, équipes et sites pour interpréter cette source, sans remplacer les réglages globaux. Seules les valeurs enregistrées sont lues : aucune formule, macro ou liaison externe n’est exécutée. Un ancien XLS demande une copie XLSX ou CSV.

**Un export de liste ne fournit pas les fiches détaillées.** Ouvrir une ligne affiche ses champs exportés. Les suivis, tâches, solutions, documents, actions GLPI et enrichissements de fiches sont indisponibles avec cette source, avec une explication dans l’interface et un refus également dans le moteur. La collecte GLPI par navigateur conserve ces fonctions. Une formule sans résultat, une lecture limitée ou un identifiant douteux reste signalé ; un fichier entièrement lu ne prouve pas l’exhaustivité de GLPI.

Le bandeau **Source CSV / Excel** distingue ce contexte. Dans **Données**, **Revenir à la source précédente** restaure le relevé qui précédait les imports ; à défaut, **Fermer cette source** revient à l’espace sans relevé. La récolte automatique est suspendue tant que le fichier est actif. Une sélection annulée ou un fichier changé avant validation conserve la source courante et la saisie en cours. Les sauvegardes explicitement engagées se terminent dans leur contexte avant la bascule.

Dans **Réglages**, la carte **Démonstration** propose **Ouvrir la démonstration**, qui crée un espace isolé avec une base entièrement fictive déjà remplie : 60 tickets complets, six techniciens, quatre sites, six interventions dont deux terminées et une série de quatre occurrences, une indisponibilité de formation, quatre tournées dont deux effectuées, une prête et une en préparation. Huit dossiers contiennent déjà notes, rappels, épingles et checklists. Les fiches, la bibliothèque, le calendrier et la carte sont immédiatement explorables.

Dans la démonstration, les actions, la connexion, la collecte, le partage et la mise à jour s’exercent avec des adaptateurs locaux. Les cartes peuvent charger le fond OpenStreetMap. **Quitter la démonstration** (même carte) ou **Quitter la simulation** (bandeau) restaure le profil et les données d’origine ; les essais restent isolés. **Réinitialiser les données fictives** recrée la base fictive. Choisir ensuite un CSV ou Excel quitte cet espace fictif et active une source réelle. Les exports explicitement enregistrés restent à l’emplacement choisi.

Voir les [notes 3.10.2](docs/RELEASE-3.10.2.md). Les modèles publics et les tests n’embarquent aucune donnée personnelle de ticket.

## Apports de la version 3.2.2

**La météo peut suivre le volume réel de tickets d’un service, d’une équipe ou d’une catégorie.** La présentation « Météo du stock » conserve le nombre cliquable et applique deux seuils configurables avec un dépassement strict : la valeur égale au seuil orange reste maîtrisée, celle égale au seuil rouge reste à surveiller. Les états soleil, nuage et pluie suivent les thèmes clair/sombre et la préférence de réduction des animations. Une réception partielle, un périmètre non confirmé ou un problème d’identité ne produit pas un faux état vert ; une simple différence de représentation HTML/CSV ne neutralise pas le comptage. Les indices de risque existants restent disponibles.

**Le modèle « Météo et pilotage du service »** ajoute un point de départ modifiable : stock actif, demandes et incidents, attente, absence de groupe, affectations multiples, rédacteurs renseignés, groupes × statuts, types × statuts, catégories, ancienneté et inactivité. Les matrices conservent les quatre statuts actifs, y compris leurs zéros connus, avec un total par ligne. Un ticket multigroupe compte une fois dans chacun de ses groupes ; ses affectations multiples ne multiplient pas le stock global. Les lignes portant un identifiant répété restent des occurrences signalées, sans déduplication silencieuse. Un filtre d’équipe peut réunir plusieurs groupes par **OU**, sans compter deux fois la même ligne commune.

**Les plus anciens ont deux vues : cinq tickets par groupe ou les dix premiers du périmètre.** Dans la seconde, une même ligne affectée à plusieurs groupes n’apparaît qu’une fois. Le classement utilise l’ouverture, puis l’identifiant numérique ; son échelle de couleur décrit l’âge relatif, sans seuil SLA. Les tickets sans date, avec une date illisible ou future restent dans le stock et sont signalés séparément, sans recevoir un âge inventé. Le détail retrouve les lignes du périmètre.

**Les participations distinguent rédaction et attribution.** Un widget compte, par personne, les tickets avec suivi, avec tâche, les deux et leur union ; un autre compare cette union aux tickets attribués au technicien. La liste est dynamique, avec une liste de personnes et des alias explicites facultatifs. Les homonymes ou identités incompatibles ne sont pas rapprochés arbitrairement. Ces comptes ne mesurent ni les actions effectuées ni la productivité.

**Sites · stock et planification** conserve les sites du référentiel même à zéro, ainsi que les tickets sans site reconnu. Les cinq premiers identifiants ouvrent leurs lignes ; **Copier les ID** reprend la liste complète du site dans le périmètre courant. Un site connu propose une intervention à partir de quatre tickets par défaut, seuil réglable ; un site inconnu demande une vérification dès le premier ticket. Aucune intervention n’est créée automatiquement.

Les filtres de catégorie peuvent inclure les **sous-catégories délimitées par `>`**. Un rappel de fraîcheur se règle par tableau ; le modèle de service le fixe à **48 heures**. Ce rappel demande une actualisation sans modifier les volumes ni les couleurs météo. Le parcours des précédents résolus restitue également le focus au contrôle de départ lorsque son dialogue se ferme.

**Les colonnes d’un rapport métier sont mieux reprises.** Suivis et tâches ont chacun leur rôle de rédacteur ; description, TTR avec progression, délai de prise en compte et compteurs déclarés sont reconnus séparément. Les âges et nombres déclarés restent distincts des mesures recalculées. Aucun pourcentage TTR n’est transformé en échéance et aucune unité de durée n’est inventée. Les widgets sans données utilisables restent masqués automatiquement, selon le réglage du tableau.

**Importer une disposition propose « Ajouter à mes tableaux ».** L’aperçu présente le nombre de tableaux et les éventuels renommages, préserve les tableaux existants et permet l’annulation. Un fichier de configuration partiel propose seulement les sections qu’il contient et conserve les réglages absents. Les sites peuvent être reconnus par leurs noms et variantes **sans commune renseignée**, puis rattachés ultérieurement sans changer d’identité ; aucun emplacement cartographique n’est inventé.

Les [notes 3.2.2](docs/RELEASE-3.2.2.md) précisent les règles de calcul et de reprise. Les configurations internes et les captures métier restent dans les fichiers d’import de leur propriétaire ; le dépôt contient uniquement le moteur, des modèles génériques et des jeux de vérification fictifs.

## Apports de la version 3.2.1

**Ctrl+K retrouve les tickets conservés**, en plus des écrans et actions. Les résultats précisent leur installation GLPI, leur état et la date de lecture. Un numéro exact est prioritaire ; le filtre d’installation distingue les tickets portant le même numéro. La lecture du contenu local garde la saisie, l’écran et le relevé en cours. Les flèches et Entrée permettent de choisir, Échap rend le focus au point de départ.

**Le lecteur recherche les précédents résolus du ticket.** Le moteur C# recherche dans la même installation, explique les termes et codes d’erreur communs et cite les passages exacts, avec priorité aux solutions recueillies. Les contenus partiels, périmés ou non confirmés sont exclus. Une catégorie identique seule ne suffit pas. La recherche peut être affinée manuellement et le retour garde la fiche de départ ainsi que le travail en cours. Jusqu’à huit résultats sont présentés avec les limites de la portion examinée ; aucun modèle distant n’est contacté.

**Les collectes conservent leur bilan sur le poste.** Après une annulation ou un redémarrage, **Dernière collecte → Détail de la collecte** retrouve les résultats par ticket, avec filtre et pagination. **Reprendre les tickets à relire** réactualise d’abord l’index et ne relit que les fiches encore accessibles dans le même contexte. Une lecture partielle reste comptée comme telle, même lorsqu’une copie complète antérieure est conservée.

La fraîcheur des nouveaux contenus reste à confirmer jusqu’au contrôle final de leur version GLPI et du périmètre. La confirmation est liée au contenu précis reçu, se synchronise avec les autres postes et ne lève pas un conflit historique. Un arrêt entre lecture et confirmation conserve les observations sans les présenter comme actuelles.

**Les conflits d’équipe se comparent champ par champ**, données structurées comprises : horaires, affectations, disponibilités ou configurations. Les paires archivées sont paginées. Les valeurs absentes, nulles et vides sont distinguées ; les textes conservent leur mise en ligne. L’aperçu annonce ses limites et **Exporter les deux versions** conserve les documents complets. Une contribution créée dans l’espace d’équipe est clairement indiquée comme partageable, même en brouillon ; les notes personnelles restent dans les fiches de l’écran Tickets.

Les [notes 3.2.1](docs/RELEASE-3.2.1.md) détaillent les parcours et les vérifications de cette mise à jour.

### Bibliothèque et interface introduites en 3.2.0

**Le style reprend directement Domenia 2** : palette bleu, violet, turquoise et rose, surfaces translucides, fond animé et transitions de navigation. Les thèmes clair/sombre, le contraste renforcé et la réduction des animations restent disponibles. **Personnaliser la navigation** permet de masquer ou épingler les pages, déplacer les entrées et réordonner leurs groupes ; Accueil et Réglages restent visibles, et la palette ouvre aussi les pages dont le raccourci est masqué. Les groupes suivent les tâches : collecter, consulter et résoudre, piloter l’équipe, travailler sur le terrain, puis administrer le logiciel.

**Bibliothèque de tickets** ajoute un cache SQLite local persistant et quatre vues : bibliothèque, résolution, équipe et connexion. **Aspirer tous les tickets** parcourt tous les statuts accessibles, y compris résolus et clos, hors corbeille et dans les entités actives de la session GLPI. Les descriptions, suivis, tâches, solutions, historiques et autres onglets consultables sont conservés avec leurs preuves de lecture ; les pièces jointes restent des références et métadonnées. Les limites du portail et les lectures partielles sont indiquées dans le bilan.

Les mises à jour réutilisent les contenus complets lorsque la date de modification GLPI, sa précision, la signature du lecteur et l’âge du cache le permettent. Renseigner le **fuseau d’affichage des dates GLPI** dans Connexion pour interpréter une date sans décalage explicite. Une date absente ou ambiguë entraîne une relecture ; elle ne permet pas de déclarer une version plus récente qu’une autre. **Relire tous les contenus** force une nouvelle lecture. Les versions divergentes sont conservées.

La vue **Résolution** recherche dans les tickets résolus ou clos dont le contenu est complet et encore actuel selon les observations conservées. Chaque résultat renvoie au ticket, à la section lue et à un extrait exact. **Exporter le corpus RAG** produit des documents complets et des fragments de texte sourcés en JSONL pour une indexation ultérieure. Cette recherche utilise l’index local de texte ; elle n’envoie pas les tickets à un modèle externe.

L’**espace d’équipe** conserve commentaires, articles de connaissance, notes, propositions d’affectation, configurations de pilotage et statistiques. La synchronisation PostgreSQL est bidirectionnelle : chaque client garde sa copie SQLite, envoie ses mutations et reprend le journal central. Les tickets sont ordonnés par leur version GLPI ; les contributions utilisent une révision centrale. Les conflits et les retraits circulent vers les autres postes. La file d’attente survit aux interruptions. La synchronisation peut être manuelle ou périodique tant que l’application est ouverte.

Les outils existants sont reliés à cet espace : **Enregistrer et partager la note** dans le lecteur, publication des **tournées**, des **répartitions** et des **compétences et capacités** des techniciens. **Examiner et reprendre** présente la comparaison avant application sur un autre poste. Les notes s’ajoutent au texte personnel, les tournées deviennent de nouveaux brouillons, et le scénario ou les capacités remplacés disposent d’une sauvegarde locale. Les favoris et notes privées des techniciens restent conservés. Ces échanges ne déclenchent pas d’affectation ou d’autre écriture GLPI.

**Utiliser le cache comme relevé** alimente les écrans de statistiques et de travail avec les métadonnées conservées. Le lecteur ouvre les détails déjà présents dans la bibliothèque, sans relancer automatiquement GLPI, en gardant l’installation d’origine et la date d’observation.

Le dossier du cache local est configurable ; un changement vers un dossier neuf utilise une copie cohérente et garde l’ancienne base. Un cache SQLite actif n’est pas partagé sur Samba. Les exports peuvent y être enregistrés ; la base commune est PostgreSQL. Le [guide bibliothèque et partage](docs/Bibliotheque-et-partage.md) décrit l’installation, les limites, le protocole de données et l’accès des autres outils. Les réglages personnels existants restent locaux tant qu’ils ne sont pas publiés explicitement dans l’espace d’équipe.

### Diagnostic et présentation introduits en 3.1.1

Le dashboard masque automatiquement les widgets sans données calculables, tout en conservant leur disposition. Le mode édition les réaffiche ; **Afficher les widgets indisponibles** mémorise le choix par tableau. Le widget Qualité devient un résumé compact et ses libellés sont en français. Les observations de présentation du CSV restent consultables dans **Détails des contrôles** et **Qualité du relevé**, sans bandeau permanent. Une réception réellement partielle, un problème d’identifiants ou de contexte reste signalé.

Les nouvelles collectes enregistrent les cellules divergentes dans un **diagnostic local dédié** : valeurs GLPI et CSV, ticket, colonnes, premier écart et codes Unicode. **Qualité du relevé → Voir les cellules divergentes / Exporter le diagnostic des écarts** permet de l’examiner ou de l’enregistrer en JSON. Ce fichier contient des données métier ; le journal général reste sans contenu de cellule. Les limites de capture et les contrôles conservés sont décrits dans les [notes 3.1.1](docs/RELEASE-3.1.1.md).

### Interface et sections introduites en 3.1.0

La version **3.1.0** réorganise l’interface suivant les principes de Domenia 2 : navigation dépliable ou réduite en rail, thèmes clair/sombre, surfaces mieux hiérarchisées, tableaux lisibles et animations désactivables. Les tickets n’affichent plus de `null` parasite et leurs identifiants restent entiers sur une ligne. Le dashboard dispose de **sections persistantes**, même vides, avec titre, description, couleur et visibilité ; widgets et sections se déplacent à la souris ou au clavier, avec redimensionnement visuel et annulation.

Les états de **réception**, d’**identité de l’échantillon** et de **concordance des valeurs** sont désormais séparés : toutes les lignes peuvent avoir été reçues alors que des valeurs restent à vérifier. Les équivalences HTML/CSV de texte riche exigent une version explicitement reconnue et une preuve issue de la cellule GLPI qui les explique exactement. Les données d’origine, les contrôles C1 et les réserves restantes sont conservés. Voir les [notes 3.1.0](docs/RELEASE-3.1.0.md) pour les conditions et la validation du commit livré.

### Migration et corrections précédentes

La version **3.0.2** corrige une régression d’usage par rapport à la version 2.0 : un CSV déjà téléchargé et lu correctement pouvait rester inutilisable dans l’application parce que certaines valeurs différaient de leur affichage HTML. Lorsque les identifiants de l’échantillon correspondent, les lignes sont désormais disponibles pour le relevé, Excel, les dashboards et les villes, avec l’état **À vérifier** si des écarts subsistent. Les contrôles de périmètre restent actifs et le CSV natif reste intact. La lecture des demandeurs multiples et des textes GLPI abrégés par `(...)` est également corrigée. Les [notes 3.0.2](docs/RELEASE-3.0.2.md) précisent les conditions et les vérifications restantes.

Les corrections de profils hérités de la version 2.0, de fermeture après panne WebView2 et de reconstruction depuis les sources DEV restent décrites dans les [notes historiques 3.0.1](docs/RELEASE-3.0.1.md).

- Interface locale dans WebView2 : navigation, thèmes, densité, zoom, formulaires, tableaux paginés et dialogues utilisent les modules web du dépôt. Les opérations autorisées sont exposées par un pont C# fermé, avec annulation et événements d’état.
- Paquet autonome avec runtime fixe Microsoft, contrôlé par une empreinte du CAB et les 257 empreintes de ses fichiers. La compilation peut utiliser exclusivement le SDK et les paquets NuGet déjà présents dans la VM hors ligne.
- Tests séparés pour les moteurs C#, leurs opérations exposées aux pages, les interactions web et le vrai hôte Windows. Après une mise à jour, le démarrage de l’application n’est confirmé qu’après l’initialisation et le premier affichage de l’interface.

Les fonctions métier de la version précédente sont raccordées aux nouvelles pages ; leur couverture et leurs limites sont détaillées dans le suivi des 200 points :

- Profils complets par installation GLPI, collectes groupées avec bilan autonome par recherche, délais séparés, contrôle d’espace libre et reprise de pagination après vérification des pages conservées. La reconnexion SSO ou réseau est bornée et refuse un périmètre différent.
- Assistant de correspondance avec aperçu brut/interprété, conventions de nombres et de dates, fuseau, séparateurs multiples et dictionnaires propres à la source. Changements de colonnes détectés, propositions de renommage soumises à validation, panier persistant et lecture universelle des cellules par F2.
- Analyses longitudinales : périodes sans observation, mouvements, transferts, cohortes, incertitude des durées par statut, scénarios de stock et anomalies. Cartes locales reliées aux widgets, taux par effectifs datés et mini-historiques. Exports PNG/PDF avec cartouche et rapport HTML complet autonome.
- Tâches, attentes, validations, relations et documents structurés à partir des seules informations lues. Lecture restaurée par ticket, briefing de session, enrichissement ciblé et brouillons de suivis. Les affectations, suivis et tâches peuvent être préparés puis confirmés individuellement dans les formulaires GLPI reconnus, avec reçu et relecture.
- Calendriers de présence et capacités par demi-journée, estimations par catégorie, hiérarchie jusqu’au bâtiment, horaires des sites, états des tournées et répartition sur plusieurs jours. Adaptateurs facultatifs OSRM et Open-Meteo, avec choix du service et consentement explicite à l’envoi des coordonnées.
- Horaires fixes multiples, recettes conditionnelles, alertes de variation et de fraîcheur, résolution motivée, import avec différences détaillées et arbitrage des conflits. Assistant de requêtes et de synthèse local ou raccordé à un fournisseur choisi, avec aperçu et références contrôlées.
- Concentration F11, navigation personnalisable, palettes de couleurs adaptées, placement sur un moniteur disponible, diagnostic relisible et mise à jour vérifiée avec sauvegarde et retour arrière.

Le [suivi des 200 points](docs/Suivi-200-ameliorations.md) indique le code livré et les prérequis restants. La validation du portail/SSO et du matériel de l’entreprise nécessite cet environnement ; la signature de confiance de l’EXE nécessite un certificat de l’éditeur. Aucun de ces deux prérequis n’est présenté comme accompli par les tests synthétiques.

## Fonctions disponibles dans la version actuelle

### Interface et relevé

- Thèmes clair, sombre et contraste élevé complets, sélecteur à quatre icônes dans la barre du haut (soleil, lune, contraste, écran « suivre Windows ») et choix « Thème » de Réglages : Suivre Windows, Clair, Sombre ou Contraste élevé. Cinq couleurs d’accent, contraste renforcé et réduction des animations.
- Zoom **75–150 %**, boutons − / 100 % / + et raccourcis ; densité compacte, équilibrée ou confortable, colonnes figées et mémorisation de la fenêtre.
- Navigation par groupes repliables ou réduite en rail, tiroir sur fenêtre étroite, journal repliable, palette **Ctrl+K**, éditeurs de colonnes et de configuration intégrés à la fenêtre. Les commandes restent accessibles au clavier et respectent la réduction des animations de l’application ou du système.
- Recherche, filtres par colonne et tri multiple calculés en C# sur **toutes les lignes chargées**, avec pages d’affichage bornées. Vues nommées mémorisant filtres, recherche, tri et présentation pour la structure du relevé.
- Export complet ou export explicite de la vue filtrée ; import local CSV, TSV, TXT, XLSX et XLSM en simulation complète. Le fichier natif reçu reste intact.

### Dashboard global et espace personnel

- Dispositions indépendantes, modèles de départ, widgets déplaçables, redimensionnables, duplicables, masquables et verrouillables ; sections persistantes et repliables avec titre, description, couleur et visibilité, y compris lorsqu’elles sont vides.
- Poignées de déplacement et de redimensionnement à la souris ou au clavier, dimensions précises dans le formulaire, édition groupée et annulation/rétablissement. Les dispositions existantes au format 1 sont reprises sans supprimer leurs widgets ni leurs sections repliées.
- Catalogue avec favoris persistants, disponibilité réelle des sondes, filtres communs et par widget, filtres croisés, contextes nommés et comparaison A/B.
- Volumes, actifs, attentes, priorités, échéances, affectations, ancienneté, dates, lieux et couverture des données ; méthodes et effectifs accessibles.
- Cinq plus anciens par groupe ou dix du périmètre, participations des rédacteurs et comparaison avec les attributions ; listes dynamiques ou regroupements par alias explicites.
- Anneaux, jauges, histogrammes, matrices colorées, barres empilées, Pareto, boîtes statistiques, médiane/P90 et nuage âge × échéance.
- Météos de santé du relevé configurables : synthèse, échéances, qualité, localisation. Elles ne représentent pas la météo climatique.
- Courbes et mini-historiques sur archives comparables : identité personnelle, filtres communs et filtres de widget rejoués sur chaque archive, avec ruptures explicites. Export PNG/PDF avec date, filtres et complétude ; bilan CSV/Excel et rapport HTML autonome complet.

### Techniciens et travail personnel

- Identité personnelle mémorisée, tableaux de bord par technicien, gestion de l’équipe, favoris et objectifs locaux.
- Compétences, territoires, relais, absences, capacité déclarée et estimations de durée. Scénarios de répartition avec explications et comparaison avant/après.
- **Accueil et Tickets** : file ordonnée, sélection depuis le relevé, notes, checklists, modèles, rappels locaux, report et passation, dans des espaces intégrés.
- Jusqu’à 12 onglets de tickets, épingles et sections préférées ; chronologie du traitement, filtres et recherche, changements observés entre deux lectures.
- Cache de lecture facultatif, explicitement daté ; rapprochements locaux entre tickets et solutions réellement lues, avec leurs références.

### Villes, sites et tournées

- Plusieurs termes pour une ville : `NV ; Nord ; Nordville = Nordville`. Reconnaissance des mots entiers, accents et noms composés ; une ambiguïté reste visible.
- Régions, départements, secteurs, hiérarchie libre, sites et bâtiments ; adresses et coordonnées facultatives, priorités des sources, conditions et exclusions des règles, simulateur avec termes surlignés et preuves.
- File des lieux à vérifier, corrections locales par installation et identifiant unique ; import CSV du référentiel avec comparatif avant application.
- Colonnes enrichies, indicateurs de couverture et répartitions par ville/site/territoire.
- Initialisation facultative par `wad/sites.csv`, avec termes de détection, communes, adresses et coordonnées. Enrichissement des champs manquants sans écraser les corrections locales.
- **Terrain** : carte intégrée Leaflet des tickets, sites et visites prévues, sélection de tickets et création explicite de tournées. Rues OpenStreetMap par défaut, y compris en simulation ; repli local hors ligne, sans navigateur externe.
- Tableau des sites avec stock actif, zéros, inconnus, préconisations configurables et copie de tous les identifiants du périmètre.
- Tournées datées à partir d’un panier explicite, états et historique motivé, départ/retour, durées, calendriers de sites et étapes verrouillées. Répartition sur plusieurs jours et techniciens selon compétences, absences, plages de présence et budgets du matin et de l’après-midi ; les capacités inconnues restent exclues de la proposition.
- Rapprochement avec un nouveau relevé, reconstruction explicite des étapes, briefing HTML imprimable et calendrier ICS. Les trajets sont saisis ou indicatifs par défaut. Un service OSRM compatible peut fournir distances et durées routières après configuration et consentement ; sa provenance et sa date restent visibles.

### Atelier de requêtes

**80 modèles** disponibles, jusqu’à 128 définitions mémorisées. Les modèles manquants peuvent être ajoutés au brouillon sans remplacer les requêtes existantes.

Le constructeur propose expressions imbriquées **ET / OU / NON**, comparaison entre champs, listes et intervalles, paramètres typés, champs calculés d’un catalogue borné, trois niveaux de regroupement, périodes, sommes/moyennes/médianes/percentiles et explication du résultat par ticket. Les résultats alimentent les widgets. Aucune requête SQL ni aucun script arbitraire n’est exécuté.

### Historique, qualité, alertes et automatisation

- Relevés locaux datés avec provenance, contrôle d’intégrité, règles d’interprétation conservées, protection et rétention paramétrable.
- Comparaison de relevés compatibles : apparitions, absences, champs modifiés, résolutions observées, réouvertures et réaffectations. **Une absence ne signifie pas une résolution.**
- Diagnostic de qualité par colonne : couverture, valeurs illisibles, identifiants répétés, dates incohérentes et accès aux lignes à examiner.
- Alertes locales sur indicateurs et requêtes : seuil, effectif minimal, exigence de complétude, prise en compte et report. Une donnée devenue indisponible ne résout pas une alerte.
- Plusieurs recherches mémorisées et collectes groupées successives. Programmation facultative **pendant que l’application reste ouverte**, plages silencieuses et suspension lorsque la session Windows est verrouillée.
- Journal d’opérations, détection des opérations interrompues, sauvegardes versionnées des réglages et restauration avec aperçu.
- Import de configuration par sections, comparatif et fusion facultative. Identité, SSO, dossiers, zoom, notes personnelles et planification locale restent sur le poste.

## Fiabilité de l’extraction

Le mode automatique privilégie le CSV **toutes les pages** proposé par GLPI. S’il est indisponible, il peut utiliser une option d’affichage complet existante puis, si autorisé, parcourir la pagination depuis la première page. **Un fichier corrompu ou un export ambigu n’entraîne pas de repli silencieux.**

Le moteur vérifie la recherche, le contexte observable, les totaux, les colonnes et les doublons de pages. Un échantillon de 25 identifiants au maximum et les valeurs des colonnes comparables sont rapprochés du CSV. Un identifiant attendu absent lors du rapprochement initial fait refuser l’import. Lorsque les identifiants correspondent mais que des valeurs diffèrent, le CSV valide est chargé avec `Complete=false` ; les réserves de concordance restent consultables dans **Qualité du relevé**, sans empêcher d’utiliser les données, les exports et les analyses. Aucun écart ni caractère de contrôle C1 n’est effacé pour obtenir une concordance.

Les textes complets présents dans les popups GLPI sont lus seulement lorsque leur structure et leur association au texte visible sont prouvées. Les abréviations non développées ne sont exclues du contrôle de valeur que dans les cas reconnus au préfixe compatible. Le contrôle final du contexte, du total et de l’échantillon reste actif ; une variation ou une vérification devenue indisponible empêche de certifier un relevé complet. Le classeur automatique d’un résultat à contrôler porte `a-verifier` dans son nom ; le CSV natif conserve son nom et ses octets. Un fichier local importé n’est pas certifié comme extraction exhaustive du portail.

La **réception complète** décrit la récupération du fichier au total annoncé, lorsque cette observation existe. Elle ne certifie ni toutes les valeurs ni l’ensemble de la recherche. Les rapprochements d’identifiants et de valeurs portent sur un échantillon. Les anciennes archives dépourvues de ces observations ne reçoivent pas de nouvelle preuve par interprétation de leurs avertissements.

La limite de lignes de l’application peut réduire le volume chargé ; elle ne tronque pas le CSV natif téléchargé. Une annulation ne déclenche pas d’annonce de réussite. Le diagnostic technique distingue les étapes sans inclure les cellules, cookies ou jetons de la session.

## Données et partage

Les données de travail résident par défaut dans `%LOCALAPPDATA%\GLPI-Pilot`, indépendamment du dossier de l’EXE : profil du navigateur, préférences, archives, notes et tournées. La bibliothèque SQLite réside dans le sous-dossier `Library`, ou dans le dossier local choisi dans **Réglages → Partage entre postes**. Les exports vont dans le dossier choisi. Le partage facultatif transmet les données communes par dossier réseau ou PostgreSQL ; les bases et index SQLite restent locaux. La configuration partageable contient les définitions d’équipe et les adresses autorisées, pas la session SSO. La connexion PostgreSQL et son mot de passe restent propres au poste ; leur mémorisation facultative utilise la protection du compte Windows.

Une base centrale est réservée à une équipe autorisée à consulter l’ensemble du corpus qu’elle reçoit. Elle ne reproduit pas automatiquement les permissions GLPI de chaque utilisateur. L’espace d’équipe partage ses propres commentaires et affectations ; publier une contribution ne modifie pas le ticket dans GLPI.

Dans **Mise à jour et retour arrière**, examiner la release officielle, sélectionner un dossier de diffusion réseau contenant le ZIP PROD et `SHA256SUMS.txt`, ou choisir un ZIP local, vérifier le pack puis confirmer son installation. L’assistant contrôle l’archive, son commit et chaque fichier, conserve une sauvegarde complète et exige un démarrage confirmé de l’interface. Après un échec, il restaure l’ancien pack ; si un fichier reste verrouillé, il conserve la sauvegarde et signale la récupération nécessaire. Aucun contrôle de release n’est lancé automatiquement au démarrage.

Le remplacement manuel reste possible : fermer l’application et extraire le nouveau ZIP dans un nouveau dossier. Ne pas mélanger ses fichiers avec un ancien `wad`. Une sauvegarde des préférences existantes précède la première ouverture d’une nouvelle version. Un second lancement rappelle la fenêtre déjà ouverte. Si aucune fenêtre n’apparaît, utiliser `wad/support/DIAGNOSTIC_DEMARRAGE.cmd` ; le dernier journal est `Logs/demarrage-dernier.log` dans les données locales.

## Documentation

- [Guide technicien 3.13.6 (PDF)](DOC/GLPI-Pilot-3.13.6-Guide-technicien.pdf) · [Word](DOC/GLPI-Pilot-3.13.6-Guide-technicien.docx) : suivre ses tickets et préparer ses interventions au quotidien.
- [Guide administrateur 3.13.6 (PDF)](DOC/GLPI-Pilot-3.13.6-Guide-administrateur.pdf) · [Word](DOC/GLPI-Pilot-3.13.6-Guide-administrateur.docx) : installation, configuration d’équipe, collecte, partage, sécurité, mises à jour et diagnostic. Sources et méthode dans `guides/`.
- [Guide utilisateur](docs/Guide-GLPI-Pilot.html)
- [Diffusion réseau, patchs de sites et administration](docs/Diffusion-et-administration.txt)
- [Calendrier et interventions](docs/Calendrier-et-interventions.md)
- [Bibliothèque, résolution, base locale et partage central](docs/Bibliotheque-et-partage.md)
- [Notes de version 3.5.0](docs/RELEASE-3.5.0.md)
- [Notes de version 3.4.1](docs/RELEASE-3.4.1.md)
- [Notes de version 3.4.0](docs/RELEASE-3.4.0.md)
- [Notes de version 3.3.4](docs/RELEASE-3.3.4.md)
- [Notes de version 3.3.3](docs/RELEASE-3.3.3.md)
- [Notes de version 3.3.2](docs/RELEASE-3.3.2.md)
- [Notes de version 3.3.1](docs/RELEASE-3.3.1.md)
- [Notes de version 3.3.0](docs/RELEASE-3.3.0.md)
- [Notes de version 3.2.2](docs/RELEASE-3.2.2.md)
- [Notes de version 3.2.1](docs/RELEASE-3.2.1.md)
- [Notes de version 3.2.0](docs/RELEASE-3.2.0.md)
- [Notes de version 3.1.1](docs/RELEASE-3.1.1.md)
- [Notes de version 3.1.0](docs/RELEASE-3.1.0.md)
- [Historique — version 3.0.2](docs/RELEASE-3.0.2.md)
- [Historique — version 3.0.1](docs/RELEASE-3.0.1.md)
- [Migration initiale vers WebView2 — version 3.0.0](docs/RELEASE-3.0.0.md)
- [Architecture et contrat des opérations WebView](docs/WebView-contract.md)
- [Compilation, caches hors ligne et chaîne Windows](docs/GitHub-et-compilation.txt)
- [Dashboards, indicateurs et extraction](docs/Dashboards-et-extraction.md)
- [Travail personnel, tickets et techniciens](docs/Travail-personnel-et-techniciens.md)
- [Requêtes, villes, sites et tournées](docs/Requetes-villes-tournees.md)
- [Historique, qualité et automatisation](docs/Historique-qualite-automatisation.md)
- [Protocole et résultats de vérification](docs/Rapport-verification.txt)
- [Suivi des 200 améliorations : couverture et reste à réaliser](docs/Suivi-200-ameliorations.md)

## Développer et vérifier

Projet **C# .NET 10, WinForms et WebView2**. Les sources d’interface se trouvent dans `web/` ; aucune chaîne npm n’est nécessaire pour construire le produit. Le SDK WebView2 est épinglé à `1.0.4258.31`, le runtime fixe à `154.0.4258.62` et Playwright à `1.63.0`.

### VM Windows hors ligne

Avec PowerShell 7.2 ou ultérieur, depuis la racine des sources, utiliser les chemins du laboratoire décrits dans [REPRISE.md de Domenia 2](https://github.com/M4D135/domenia-d120/blob/master/v2/REPRISE.md) :

```powershell
$env:DOTNET_CLI_HOME = 'C:\DomeniaDev\dotnet-home'
$env:NUGET_PACKAGES = 'C:\DomeniaDev\nuget-packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

.\build.ps1 `
  -DotNetPath 'C:\DomeniaDev\dotnet\dotnet.exe' `
  -NuGetOfflineSource 'C:\DomeniaDev\cache\nuget' `
  -RuntimePath 'C:\DomeniaDev\cache\webview2\runtime' `
  -OutputDirectory 'C:\DomeniaDev\builds\glpi-pilot-3.3.2'
```

Le runtime doit être déjà extrait, soit directement dans `RuntimePath`, soit dans son unique sous-dossier. Tous les paquets nécessaires doivent être présents dans le cache NuGet. Une dépendance absente arrête la compilation ; aucun téléchargement de secours n’est tenté. Le script refuse d’écraser une distribution existante : choisir un nouveau dossier de sortie pour une nouvelle construction.

La VM de référence dispose de Windows 11, 4 vCPU et 8 Go. Conserver la session console ouverte et réserver les essais interactifs en VM à la fin du chantier. Cette documentation décrit les commandes à y exécuter ; elle n’atteste aucune connexion à cette VM depuis un autre atelier.

### Préparation connectée et CI

Sur une machine Windows autorisée à télécharger les dépendances :

```powershell
$runtime = .\build-tools\Get-WebView2Runtime.ps1 `
  -CacheDirectory 'C:\GLPI-BuildCache\webview2'

.\build.ps1 -RuntimePath $runtime -AllowOnlineRestore `
  -OutputDirectory '.\artifacts\release-3.3.2'
```

`Get-WebView2Runtime.ps1` utilise uniquement l’URL Microsoft épinglée, vérifie le CAB et chaque fichier extrait, puis réutilise le cache contrôlé. `-CabPath` permet de fournir le CAB par transfert local ; `-Offline` interdit son téléchargement. `build.ps1` ne télécharge jamais le runtime. L’accès NuGet en ligne nécessite `-AllowOnlineRestore` ; il est incompatible avec `-NuGetOfflineSource`.

Le [workflow Windows et PostgreSQL](.github/workflows/windows.yml) prévoit les moteurs C#, les pages branchées aux vrais services, les parcours navigateur synthétiques, puis les packs et le vrai hôte WinForms/WebView2. Les contrôles natifs vérifient thèmes, zoom, navigation, fermeture et mise à jour/rollback sur des copies temporaires. Un second job vérifie la synchronisation contre un vrai serveur PostgreSQL 17 temporaire. La promotion en Release n’utilise que les archives de ce run après réussite des deux jobs. Le détail des commandes, des caches et des prérequis figure dans [GitHub et compilation](docs/GitHub-et-compilation.txt).

Une compilation croisée ne prouve pas le fonctionnement de l’EXE sous Windows. Les résultats définitifs sont les journaux du [workflow associé au commit livré](https://github.com/M4D135/GLPI-Pilot/actions/workflows/windows.yml). Le portail d’entreprise et son SSO réel restent à confirmer dans cet environnement.

## Limites explicites

Les données absentes restent indisponibles. Les dates sont calendaires ; un stock ou une estimation ne mesure pas la productivité d’un technicien. Les analyses historiques décrivent des observations et leurs incertitudes : une disparition n’est pas une résolution, une projection n’est pas un engagement de résultat.

Les notes personnelles, tournées, corrections et scénarios existants restent locaux. Les contributions créées dans l’espace d’équipe et les configurations ou statistiques qui y sont publiées se synchronisent lorsque le central est activé. Seule une action GLPI préparée dans l’assistant dédié puis confirmée explicitement peut écrire dans le portail, selon les droits du compte. Un résultat d’envoi ambigu est signalé et n’est jamais relancé automatiquement.

Les services de routage, météo et génération externe sont désactivés tant qu’aucun fournisseur n’est configuré et autorisé. Le mode local de l’assistant repose sur des règles explicites et une synthèse extractive ; il n’est pas un modèle généraliste exécuté hors ligne. Une réponse externe doit respecter le schéma borné et citer des références présentes dans le contexte ; sa pertinence doit être relue avant usage.

Les packs conservent commit et empreintes. La chaîne Authenticode est prête, mais aucun certificat de confiance n’a été fourni pour cette livraison : les empreintes ne constituent pas une signature d’éditeur. Le comportement du portail privé, de son SSO et des configurations physiques de plusieurs écrans reste à vérifier sur les postes concernés. Outil indépendant, sans affiliation au projet GLPI.
