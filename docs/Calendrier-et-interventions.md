# Calendrier et interventions

La rubrique **Interventions** réunit le planning, sa carte et les tournées. Elle sert à organiser les demandes, les incidents, les rendez-vous libres, les réunions, les maintenances et les indisponibilités. Une intervention peut concerner plusieurs tickets et plusieurs personnes, ou être créée sans ticket et avant toute importation de données.

L’application présente les mêmes fonctions à tous les utilisateurs. **Planning commun** et **Mes interventions** sont des filtres d’affichage. Le bouton **Choisir mon identité**, dans le bandeau supérieur ou sur l’Accueil, ouvre le panneau **Mon identité**. Rechercher son nom puis cliquer **C’est moi** mémorise le choix dans le profil local, sans quitter le planning ni changer sa date. Cette sélection ne crée aucun compte, n’accorde aucun rôle et ne prouve pas l’identité GLPI de la personne qui utilise le poste.

Si le nom n’est pas proposé, **Mon nom n’est pas dans la liste** permet de saisir **Nom tel qu’il apparaît dans les tickets**, puis **Enregistrer mon identité**. Les propositions utilisent les tickets chargés et les participants du planning, sans interrogation d’un annuaire. Changer ou **Retirer mon identité** conserve les interventions et les participants enregistrés. Les identités existantes gardent leurs clés ; les anciens participants manuels ne sont pas fusionnés automatiquement.

## Organiser son travail dans une seule rubrique

Les onglets **Planning**, **Carte** et **Tournées** conservent le contexte de travail. Le bouton **Actualiser** du bandeau recharge la vue affichée, avec sa date et ses filtres, sans rejouer l’ouverture d’un panneau déjà fermé. Une saisie d’intervention reste ouverte. Les champs de tournée non appliqués demandent une confirmation avant leur rechargement. L’actualisation utilise le relevé courant ; elle ne lance pas une collecte GLPI ni un import de tickets.

Le planning propose cinq présentations :

| Vue | Utilisation |
|---|---|
| **Jour** | Préparer les interventions d’une journée, voir les horaires et les chevauchements. |
| **Semaine** | Organiser la semaine et déplacer un rendez-vous vers un autre créneau. |
| **Équipe · semaine** | Comparer les réservations des intervenants dans une grille commune. |
| **Mois** | Repérer les journées chargées et les récurrences. |
| **Agenda** | Lire les interventions sous forme de liste, notamment sur un écran étroit. |

Les filtres de techniciens et d’équipes acceptent plusieurs sélections. Un filtre d’équipe s’appuie sur les appartenances des ressources ; un rendez-vous sans participant n’appartient pas artificiellement à une équipe. **Mes interventions** demande de choisir une identité si elle n’est pas encore connue. Il ne montre pas silencieusement le planning de tout le monde à la place.

Les préférences d’affichage restent propres au poste, à la simulation ou à la session habituelle et à l’identité de filtre choisie. Revenir dans la rubrique retrouve la présentation et la période mémorisées. Les mêmes documents alimentent les vues personnelles et communes.

## Créer et relier une intervention

1. Choisir **Nouvelle intervention**, un créneau dans la grille ou **Planifier** depuis un ticket.
2. Renseigner le titre, le type, le début, la fin et le fuseau horaire.
3. Choisir les intervenants et, si nécessaire, les tickets concernés. La recherche de tickets conserve leur installation GLPI ; deux tickets portant le même numéro sur des installations différentes restent distincts.
4. Choisir le site ou renseigner un lieu et une adresse. Une intervention à distance possède son propre indicateur.
5. Ajouter, selon le besoin, les notes, le contact, le matériel, les compétences nécessaires, les temps de déplacement et le rappel.
6. Examiner les avertissements, puis enregistrer.

Le panneau de préparation reste à côté du planning. Un déplacement ou un redimensionnement sans arbitrage nécessaire est enregistré après les contrôles. Un conflit ou un changement de fuseau ouvre la proposition dans le panneau pour examen. Les commandes de formulaire restent utilisables sans glisser-déposer.

**À planifier** affiche les tickets actifs du relevé courant qui ne possèdent pas d’intervention prévue sur la période affichée. Le contrôle des réservations couvre aussi les intervenants masqués par un filtre. Un ticket déjà prévu chez un collègue n’est donc pas considéré comme libre uniquement parce que ce collègue n’est pas affiché. Ce bloc dépend du relevé importé ou collecté ; les rendez-vous libres, eux, fonctionnent sans relevé.

Les états **Planifiée**, **Confirmée**, **En cours**, **Effectuée** et **Annulée** décrivent l’organisation dans GLPI Pilot. Ils ne changent pas automatiquement l’affectation ou le statut du ticket dans GLPI. Une référence de ticket reste conservée si elle disparaît d’un prochain extrait : le planning signale alors l’absence de vérification actuelle.

## Intervenants, présence et capacité

Le registre des intervenants combine les personnes observées dans les tickets, les réservations existantes, les préférences métier et les ressources enregistrées explicitement. Il reste disponible lorsqu’une personne n’a plus de ticket dans l’extrait courant. Il est possible de déclarer une ressource avant toute collecte.

Une ressource peut avoir des équipes, un fuseau horaire, des plages hebdomadaires de présence et des capacités particulières pour certaines dates. Les données déjà déclarées dans les outils de techniciens contribuent aux vérifications de compétences, de capacité et d’indisponibilité. Une capacité inconnue reste indiquée comme inconnue ; elle n’est ni convertie en zéro ni remplacée par une journée de travail inventée.

Les ressources et les rendez-vous sont des données communes lorsque le partage est activé. Le choix « c’est moi » reste un réglage personnel de filtre. Pour des noms homonymes, l’équipe doit conserver des identités de ressource distinctes et compréhensibles.

## Comprendre les chiffres

| Indicateur | Règle de calcul |
|---|---|
| Interventions | Nombre d’occurrences actives de rendez-vous, réunions ou maintenances sur la période ; une intervention commune est comptée une fois. |
| Tickets liés | Références de tickets distinctes, avec leur installation ; un ticket lié à plusieurs rendez-vous n’est pas multiplié dans ce total. |
| Heures d’intervention | Somme des durées des rendez-vous actifs ; les indisponibilités sont présentées séparément. |
| Heures-technicien | Durée multipliée par le nombre de participants. Une intervention de 90 minutes avec deux intervenants représente trois heures-technicien. |
| Sans technicien | Interventions actives sans participant déclaré. |
| Effectuées / annulées | États conservés dans le planning ; les annulations libèrent les créneaux de réservation. |
| Alertes | Contraintes détectées pour la sélection visible, calculées en tenant compte des réservations connues hors de cette sélection. |

Les heures et les personnes prévues décrivent une organisation déclarée. Elles ne mesurent pas la présence réelle ni le temps effectivement passé dans GLPI.

## Vérifier les contraintes et proposer un créneau

Avant l’enregistrement, le moteur contrôle les autres interventions et les visites des tournées enregistrées. Il examine les personnes communes, les chevauchements, les marges de déplacement déclarées, la présence, les capacités, les indisponibilités, les compétences connues et les horaires d’ouverture des sites. Une échéance de ticket n’est utilisée que si sa date peut être interprétée.

Un avertissement expose le motif concret et les réservations concernées. L’utilisateur peut maintenir une proposition conflictuelle après avoir examiné ces avertissements. L’accord est lié à cet aperçu : si une réservation nouvelle apparaît entre l’aperçu et l’enregistrement, l’application demande un nouvel examen. Les alertes restent visibles après un maintien volontaire.

Une heure impossible ou non résolue lors d’un changement d’heure ne peut pas être forcée. Le début, la fin et le fuseau doivent permettre de déterminer les instants réels. Pour une heure répétée au retour à l’heure d’hiver, le décalage UTC explicite permet de préciser la bonne occurrence.

Les suggestions recherchent des créneaux individuels sur la période demandée, au maximum 31 jours. Elles appliquent les contraintes connues avant de retenir les meilleures propositions. Une plage inconnue garde un avertissement. La suggestion ne valide pas toute une série future ni un trajet routier qui n’a pas été calculé.

## Récurrences et modifications groupées

Les récurrences peuvent être quotidiennes, hebdomadaires ou mensuelles, avec un intervalle, les jours de semaine concernés, une fin ou un nombre d’occurrences. Elles gardent l’heure locale dans le fuseau de la série. Une récurrence mensuelle sur un jour absent d’un mois saute ce mois ; elle ne déplace pas arbitrairement l’intervention au dernier jour.

Pour modifier ou annuler une série, choisir la portée :

- **Cette occurrence** conserve les autres dates et enregistre une exception.
- **Cette occurrence et les suivantes** préserve le passé et fait évoluer la suite de la série.
- **Toute la série** modifie son organisation globale en conservant les exceptions correspondantes.

Chaque occurrence possède une identité stable, y compris après un déplacement ou une séparation de série. Les exports de calendrier peuvent ainsi garder la même référence pour un rendez-vous qui change d’heure.

Le déplacement groupé sélectionne plusieurs interventions et prépare un décalage commun. L’aperçu expose les nouvelles dates et les conflits. La validation vérifie toutes les révisions et applique le lot entier. Une intervention modifiée entre-temps bloque le lot.

**Annuler la dernière modification** utilise la version enregistrée par cette action et conserve les changements d’autres utilisateurs. Les possibilités d’annulation sont locales à la session, limitées aux vingt dernières opérations pendant une heure. Elles ne constituent pas un historique illimité.

## Carte et tournées

La carte embarquée situe les interventions à partir des coordonnées des sites. Une étape de tournée connue uniquement par sa commune peut apparaître avec un repère de commune explicitement approximatif. Un lieu libre sans coordonnées et une intervention à distance restent dans la liste des interventions sans repère. Une adresse saisie ne devient pas automatiquement une position vérifiée.

La carte permet de retrouver les tickets et les interventions d’un site et de revenir à leur préparation. Elle montre les lieux prévus ; elle ne suit pas les techniciens en temps réel. Le fond avec rues est utilisé lorsqu’il est disponible, avec le repli vectoriel embarqué prévu par le composant de carte.

Les visites des tournées sont projetées dans le calendrier depuis les heures effectives de passage calculées par le moteur de tournée. La durée globale de la tournée n’est pas réservée une seconde fois comme un rendez-vous indépendant. Ces visites se modifient dans **Tournées**, leur outil propriétaire.

Une nouvelle intervention vérifie aussi les visites de tournée. Le calcul d’une tournée et son passage à l’état prêt examinent les réservations du calendrier commun. Une visite dont l’heure est impossible reste signalée ; elle ne fait pas croire que le créneau est libre. Les durées de trajet et les horaires dépendent des données effectivement déclarées ou calculées.

## Rappels, impression et échange de calendrier

Les rappels à préparer sont affichés pendant l’utilisation du calendrier. Tant que l’application est ouverte, elle contrôle aussi les rappels de l’identité choisie, au maximum une fois par minute, et peut afficher une notification locale. Les périodes calmes définies dans les réglages, une session Windows verrouillée et une édition active suspendent ce contrôle. Sans identité choisie, aucun rappel du planning commun n’est envoyé par défaut. Une même version d’un rendez-vous n’est pas notifiée plusieurs fois pendant la session.

Ce mécanisme n’envoie pas de message à un collègue, ne crée pas d’invitation dans sa messagerie et ne fonctionne pas comme un service permanent lorsque l’application est fermée. En simulation, les rappels restent consultables dans l’interface et aucune notification système réelle n’est envoyée.

L’export **ICS** contient les occurrences de la période et des filtres choisis, leurs horaires, leurs identités stables et les rappels définis. Les annulations conservent leur identité. Le comportement de mise à jour après import dépend du logiciel de calendrier qui reçoit le fichier : GLPI Pilot ne maintient pas une connexion permanente à Outlook ou Google Calendar.

La fiche **HTML imprimable** rassemble les informations nécessaires aux interventions, avec les noms des participants et les détails conservés. L’export ne modifie pas les clés de ressource enregistrées.

## Enregistrer localement ou partager entre postes

Le planning est enregistré dans la bibliothèque SQLite locale du profil utilisateur. Sans partage, toutes ses fonctions restent utilisables sur le poste. Le partage facultatif peut utiliser un dossier réseau commun ou PostgreSQL ; voir [Bibliothèque et partage](Bibliotheque-et-partage.md).

Le calendrier conserve une révision et un historique pour les interventions. Une saisie ouverte avant la modification d’un autre utilisateur ne peut pas écraser silencieusement la nouvelle version. Les séparations de série, déplacements groupés et annulations de ces opérations se synchronisent sous forme de lots indivisibles. Un lot refusé conserve toutes ses propositions pour comparaison, y compris un nouveau fragment de série qui n’a pas été accepté.

**Versions à examiner** présente la version courante et la proposition de chaque membre du lot. Il est possible d’exporter les deux versions, de conserver le lot courant ou d’appliquer le lot proposé après vérification. Une nouvelle modification rend l’ancien aperçu caduc. L’application n’arbitre pas l’organisation de l’équipe à partir de l’heure des ordinateurs.

Le partage est périodique, avec une file locale et des compteurs d’envoi/réception. Deux postes hors connexion peuvent préparer des versions différentes ; ils les réconcilient au prochain échange. Utiliser la même version 3.5 de GLPI Pilot sur les postes d’un même espace.

## Simulation, portée et contrôles

L’import Excel ou CSV ouvre une simulation isolée. Les tickets importés alimentent les liens et les listes à planifier ; les rendez-vous libres, les ressources, les récurrences, les vues, les conflits, la carte et les tournées utilisent les mêmes mécanismes. Les données absentes de l’extrait restent absentes. Le partage simulé passe par des répliques en mémoire : un chemin réseau ou un serveur réel saisi dans les réglages ne sont pas contactés.

Une vue du calendrier couvre au maximum 93 jours. L’enregistrement examine jusqu’à 366 jours de récurrence et annonce la période contrôlée. Les opérations groupées sont limitées à cinquante sélections. Une série ouverte très loin dans le futur peut donc rencontrer une contrainte qui n’existait pas dans la fenêtre examinée. Les propositions restent une aide à la préparation, fondée sur les déclarations disponibles.

Les suites [CalendarChecks](../tests/CalendarChecks/Program.cs) et [CalendarBridgeChecks](../tests/CalendarBridgeChecks/Program.cs) vérifient les fuseaux, changements d’heure, identités stables, séries, calculs, conflits, versions, visites réelles de tournée et l’isolation de la simulation. Les contrôles de partage couvrent les révisions concurrentes, les lots, la reprise et les échanges entre processus. Les parcours WebView exercent l’interface réelle. Ces contrôles automatisés n’établissent pas les disponibilités réelles des intervenants ni la qualité des données d’un organisme.
