# 1 Prendre en main GLPI Pilot

GLPI Pilot rassemble vos tickets GLPI, vos interventions et la préparation de vos visites dans une seule fenêtre. Ce chapitre présente la fenêtre, votre identité et la recherche.

## La fenêtre

- **Le menu de gauche.** Quatre espaces de travail : « Accueil », « Tickets », « Interventions » et « Pilotage ». En dessous : « Données » et « Réglages ». « Réglages » range ses réglages en sections, dans un rail à gauche ; « Rechercher un réglage » (touche /) retrouve un réglage par ses mots.
- **La barre du haut.** Le choix de la source affichée, votre identité, « Rechercher » (Ctrl K) et quatre icônes de thème : clair, sombre, contraste élevé (texte blanc sur fond noir) et « suivre Windows ». L'icône allumée indique votre choix ; il se retrouve dans « Réglages ».
- **La barre du bas.** L'état du travail en cours, « Journal », et le zoom : « − », « 100 % », « + ».
- **Le bouton « Actualiser ».** En haut à droite de chaque espace, il recharge ce que vous voyez sans changer d'écran.

![Accueil : le menu de gauche, la barre du haut et la carte « À faire et à prévoir ».](fig:accueil)

## Replier ou déplier le menu

Quand : vous voulez plus de place pour un tableau, le planning ou la carte.

1. **Replier.** Tout en bas du menu, cliquez sur « Replier le menu », ou appuyez sur Ctrl B.
2. **Se repérer.** Le menu ne garde que les icônes. Passez la souris sur une icône : son nom s'affiche à côté.
3. **Déplier.** Cliquez sur l'icône tout en bas du menu, ou appuyez de nouveau sur Ctrl B.

![Menu déplié : « Replier le menu » est la dernière ligne ; l'indice Ctrl B apparaît au survol.](fig:menu-deplie)

![Menu replié : il ne reste que les icônes ; celle du bas déplie le menu.](fig:menu-replie)

> [!RESULTAT] GLPI Pilot retient votre choix et le retrouve au prochain démarrage.

> [!NOTE] Dans une fenêtre étroite, le menu s'ouvre par le bouton « Navigation » en haut à gauche et se ferme avec Échap.

## Choisir son identité

Quand : à la première utilisation, ou si vous changez de poste. Votre identité affiche vos tickets et vos interventions.

1. **Ouvrir.** Cliquez sur « Choisir mon identité » dans la barre du haut ou sur l'Accueil. La fenêtre « Mon identité » s'ouvre.
2. **Chercher.** Tapez votre nom dans « Rechercher votre nom ».
3. **Choisir.** Sur votre ligne, cliquez sur « C’est moi ».
4. **Si votre nom manque.** Cliquez sur « Mon nom n’est pas dans la liste », saisissez votre nom tel qu'il apparaît dans les tickets, puis cliquez sur « Enregistrer mon identité ».

![Fenêtre « Mon identité » : chaque nom trouvé indique son nombre de tickets ; « C’est moi » le retient.](fig:identite)

> [!RESULTAT] Votre nom remplace « Choisir mon identité » dans la barre du haut. Pour en changer, cliquez dessus.

## Tout retrouver avec Ctrl K

Quand : vous cherchez un écran, une action ou un ticket déjà conservé.

1. **Ouvrir la recherche.** Appuyez sur Ctrl K, ou cliquez sur « Rechercher » dans la barre du haut.
2. **Taper.** Saisissez un numéro de ticket, un mot du titre, un écran ou une action.
3. **Choisir.** Flèches haut et bas pour choisir, Entrée pour ouvrir, Échap pour fermer.
4. **Voir la suite.** Si tous les tickets ne tiennent pas dans la liste, cliquez sur « Ouvrir la bibliothèque filtrée ».

![Recherche Ctrl K : les tickets conservés trouvés pour « 42 », avec leur installation et leur état.](fig:palette)

> [!ASTUCE] « Installation des tickets conservés » limite la recherche à une seule installation GLPI quand vous en suivez plusieurs.

# 2 Commencer sa journée sur l'Accueil

Quand : chaque matin, ou en revenant sur le poste.

L'Accueil réunit ce qui demande votre attention : priorités, indicateurs et agenda des sept prochains jours.

1. **Ouvrir l'Accueil.** Cliquez sur « Accueil » dans le menu.
2. **Choisir le périmètre.** Cliquez sur « Tickets du périmètre » pour tout le service, « Ma file personnelle » pour vos tickets, ou « Équipes » pour choisir des équipes.
3. **Lire les indicateurs.** « Tickets actifs », « Sans technicien », « Échéance dépassée » et « Interventions · 7 jours » donnent l'état du moment.
4. **Traiter les priorités.** La liste « Priorités » commence par les échéances dépassées et les priorités hautes. Sur une ligne, cliquez sur « Planifier » pour prévoir un passage.
5. **Consulter l'agenda.** Dans « Agenda · 7 prochains jours », choisissez « Toutes les interventions » ou « Mes interventions ». Cliquez sur « Ouvrir » sur une intervention, ou sur « Ouvrir le planning ».

> [!RESULTAT] Vous savez ce qui est en retard, ce qui n'a pas de technicien et ce qui est prévu cette semaine.

> [!NOTE] Si l'Accueil affiche « Les priorités apparaîtront avec vos tickets », aucun ticket n'est encore chargé : suivez le chapitre 7.

## Raccourcis de la carte « À faire et à prévoir »

- « Planifier » ouvre la planification d'une intervention.
- « Carte des tickets » ouvre la carte des tickets du périmètre.
- « Collecter depuis GLPI » ouvre l’espace « Données » pour mettre à jour la liste de vos tickets.

## Si ça ne marche pas

| Vous voyez | Que faire |
|---|---|
| « Ma file personnelle » ne montre rien | Choisissez votre identité (chapitre 1). |
| « Choisissez votre identité pour afficher uniquement vos interventions. » | Cliquez sur « Choisir mon identité ». |
| Des chiffres anciens | Cliquez sur « Actualiser », puis, si besoin, mettez les tickets à jour (chapitre 7). |

# 3 Lire et piloter un ticket

## Trouver un ticket

Quand : vous cherchez un ticket précis ou une série de tickets.

1. **Ouvrir la liste.** Cliquez sur « Tickets » dans le menu.
2. **Choisir l'onglet.** « Tout le périmètre », « Mes tickets », « Équipes » ou « Techniciens ».
3. **Filtrer.** Tapez un numéro, un titre, une personne ou une catégorie dans le champ de recherche. Choisissez un état dans « Tous les statuts ». « Filtres et tri » propose les autres critères.
4. **Ouvrir.** Sur la ligne du ticket, cliquez sur « Ouvrir ».

> [!ASTUCE] Cochez plusieurs lignes pour agir en une fois : « Planifier », « Ajouter à ma file » ou « Retirer les sélections ».

## Lire la fiche

1. **Parcourir la fiche.** Les informations du ticket s'affichent par rubriques. Sur un texte coupé, cliquez sur « Lire le texte complet ».
2. **Mettre à jour la fiche.** Cliquez sur « Actualiser la fiche » pour relire ce ticket.
3. **Noter.** Cliquez sur « Notes et rappels » pour vos notes personnelles et vos rappels, puis sur « Enregistrer mon travail ».
4. **Passer au suivant.** « Ticket précédent » et « Ticket suivant » parcourent la liste ; « Retour à la liste » la retrouve à la même place.

## Préparer une action dans GLPI

Quand : vous devez ajouter un suivi, une tâche ou affecter le ticket.

1. **Ouvrir les actions.** Dans la fiche, cliquez sur « Actions assistées ».
2. **Choisir l'action.** « Ajouter un suivi », « Ajouter une tâche », « Affecter » ou « Toutes les actions ».
3. **Relire.** GLPI Pilot montre ce qui sera envoyé. Relisez avant de confirmer.
4. **Confirmer une seule fois.** L'envoi se fait dans votre session GLPI, avec vos droits.

![« Actions assistées » : les actions GLPI en haut, puis « Organiser l’intervention » pour le planning, le matériel et les rappels.](fig:actions-assistees)

> [!ATTENTION] Ne confirmez jamais deux fois la même action. Si le résultat vous semble incertain, ouvrez le ticket dans GLPI pour le vérifier avant toute nouvelle tentative.

> [!NOTE] Le statut, la priorité et la résolution se changent dans GLPI : « Statut, priorité, résolution dans GLPI » ouvre la fiche GLPI.

Le bloc « Organiser l’intervention : planning, matériel et rappels » reste dans GLPI Pilot et ne change rien dans GLPI : « Planifier une intervention », « Matériel et fiches terrain », « Notes, checklist et rappels », « Préparer un compte rendu ».

## Retrouver une solution déjà appliquée

Quand : le problème ressemble à un ticket déjà résolu.

1. **Chercher.** Dans la fiche, cliquez sur « Chercher des solutions similaires ». La fenêtre « Précédents résolus » s'ouvre.
2. **Affiner.** Modifiez les mots dans « Affiner la recherche dans les précédents », puis cliquez sur « Rechercher ». « Reprendre le titre conservé » remet le titre du ticket.
3. **Lire.** Chaque précédent montre pourquoi il ressort et la solution recueillie. Cliquez sur « Lire ce précédent » ou sur « Ouvrir ce précédent dans GLPI ».
4. **Revenir.** Cliquez sur « Retour à la fiche ».

![« Précédents résolus » : les tickets résolus proches, avec les termes communs et la solution recueillie.](fig:precedents)

> [!ATTENTION] Vérifiez toujours le contexte avant de reprendre une solution : un précédent est une piste, pas une consigne.

# 4 Planifier une intervention

Quand : un ticket demande un passage, ou vous organisez une visite.

## Créer l'intervention

1. **Ouvrir le planning.** Cliquez sur « Interventions », puis sur l'onglet « Planning ».
2. **Se placer.** « Aujourd’hui » revient au jour courant ; les flèches changent de période. Choisissez la vue « Jour », « Semaine » ou « Mois », et « Planning commun » ou « Mes interventions ».
3. **Créer.** Cliquez sur « Nouvelle intervention ». Le formulaire s'ouvre à droite.
4. **Remplir.** Renseignez l'« Objet », la « Nature », l'« État », le « Début » et la « Fin ». Cochez « Toute la journée (fin exclusive à minuit) » si besoin. Choisissez les techniciens.
5. **Contrôler.** Cliquez sur « Vérifier » pour voir les chevauchements et les points de vigilance. « Proposer un créneau » cherche une plage libre.
6. **Enregistrer.** Cliquez sur « Enregistrer ». « Annuler » ferme sans rien garder.

![Formulaire « Nouvelle intervention » : objet, nature, état, horaires, techniciens, puis « Enregistrer ».](fig:intervention)

![Planning de la semaine : les indicateurs du haut, puis les interventions dans la grille.](fig:planning)

> [!RESULTAT] L'intervention apparaît dans la grille et dans l'agenda de l'Accueil.

> [!ASTUCE] « À planifier » liste les tickets actifs qui n'ont encore aucune intervention.

## Si ça ne marche pas

| Vous voyez | Que faire |
|---|---|
| Un avertissement de chevauchement | Changez l'horaire ou les techniciens, ou cliquez sur « Proposer un créneau ». |
| « Le planning a évolué. Votre saisie est conservée ; la version sera vérifiée à l’enregistrement. » | Un collègue a modifié le planning. Relisez, puis cliquez sur « Enregistrer ». |

# 5 Carte et tournées

La carte montre où se trouvent les tickets de votre périmètre. Une tournée enchaîne plusieurs visites sur une journée, avec leur ordre, leur matériel et leurs fiches.

## Ouvrir la carte

1. **Ouvrir.** Sur l'Accueil, cliquez sur « Carte des tickets ». Ou, dans « Interventions », cliquez sur l'onglet « Carte » : avec un relevé chargé, il ouvre la carte des tickets. « Carte des interventions prévues » montre à la place les interventions planifiées ; « Explorer les tickets sur la carte » revient aux tickets.
2. **Choisir les tickets.** Cliquez sur « Tous les techniciens », « Mes tickets », « Équipes » ou « Techniciens ». « Visites prévues » affiche les visites d'une date.
3. **Lire les compteurs.** « Stock du périmètre », « Sur la carte », « Localisation approximative » et « Sans coordonnées » disent combien de tickets sont placés, et avec quelle précision.

![Carte : les compteurs du périmètre, les raccourcis du terrain, puis la carte et ses commandes.](fig:carte)

## Se déplacer sur la carte

- La molette fait défiler la page. Les boutons « + » et « − » zooment sur la carte.
- « Agrandir la carte » lui donne toute la place. Dans la carte agrandie, la molette zoome.
- « Tout afficher » recadre sur tous les repères. « Afficher la liste » montre les tickets sous la carte. Le plan des rues s'affiche à l'ouverture ; « Fond hors ligne » le retire et « Afficher les rues » le remet.

## Voir les sites

Cliquez sur « Explorer les sites ». La fenêtre « Sites du périmètre » donne, pour chaque site, son « Stock actif » et une « Préconisation » : « À suivre », « Intervention à planifier » ou « Aucun ticket ». Sur une ligne, « Voir sur la carte » centre la carte sur le site et « Copier les IDs » copie les numéros de ses tickets.

## Agir sur des tickets de la carte

1. **Sélectionner.** Cliquez sur un repère, puis sur « Sélectionner les tickets de ce lieu ». « Sélectionner tout le périmètre » prend toute la liste ; « Vider la sélection » repart de zéro.
2. **Agir.** Cliquez sur « Planifier une intervention », « Préparer une tournée » ou « Corriger le site des tickets sélectionnés ».

## Corriger un lieu

Quand : un ticket est mal placé sur la carte, ou n'y figure pas.

1. **Ouvrir la correction.** Cliquez sur « Corriger une localisation », ou sur « Corriger le site des tickets sélectionnés ». La fenêtre « Corriger le site du ticket » s'ouvre.
2. **Trouver le site.** Tapez son nom dans « Rechercher un site », cliquez sur « Rechercher », puis choisissez le site dans la liste.
3. **Enregistrer.** Choisissez le « Motif de la correction », puis cliquez sur « Enregistrer la correction ».

> [!RESULTAT] Le ticket est rattaché au site choisi et se place sur la carte. La correction ne modifie pas le ticket dans GLPI.

## Préparer une tournée

Quand : vous enchaînez plusieurs visites dans la même journée.

1. **Ouvrir les tournées.** Dans « Interventions », cliquez sur l'onglet « Tournées ».
2. **Choisir les tickets.** Ajoutez des tickets au « Panier de tickets » avec « Ajouter la page au panier », puis cliquez sur « Créer une tournée avec ce panier ». Depuis la carte, « Préparer une tournée » part directement de votre sélection.
3. **Ordonner les étapes.** « Proposer un ordre de proximité » range les étapes. « Monter l’étape » et « Descendre l’étape » ajustent l'ordre.
4. **Régler la journée.** Renseignez le « Nom », le « Premier jour », le « Début de journée » et la « Fin de journée », puis cliquez sur « Calculer le planning ».
5. **Enregistrer.** Cliquez sur « Enregistrer la tournée ». « Carte de la tournée », « Matériel » et « Fiches » ouvrent la carte, la préparation et les fiches de cette tournée.

![Une tournée : son état, ses tickets et ses étapes, puis « Enregistrer la tournée », la carte, le matériel et les fiches.](fig:tournee)

> [!RESULTAT] La tournée est enregistrée. « Exporter le briefing HTML… » et « Exporter le calendrier ICS… » la transmettent à l'équipe.

> [!ATTENTION] Tant que « Brouillon à enregistrer » s'affiche, la tournée n'est pas enregistrée : cliquez sur « Enregistrer la tournée » avant de quitter l'écran.

## Si ça ne marche pas

| Vous voyez | Que faire |
|---|---|
| Un ticket n'apparaît pas sur la carte | Son lieu est inconnu ou approximatif : corrigez son site. Si le site n'existe pas, prévenez l'administrateur. |
| « Localisation approximative » | Le ticket est placé sur sa commune, pas sur le site exact. Corrigez son site si besoin. |
| « Brouillon à enregistrer » | Cliquez sur « Enregistrer la tournée ». |

# 6 Préparer le matériel et les fiches terrain

Quand : avant une intervention, pour prévoir le matériel à emporter et imprimer les fiches. GLPI Pilot ne gère pas de stock : il note ce qui est prévu et ce qui est préparé.

## Préparer une intervention

1. **Ouvrir la préparation.** Dans « Interventions », cliquez sur « Matériel ». Depuis un ticket, « Matériel et fiches terrain » mène au même endroit.
2. **Choisir quoi préparer.** Cliquez sur « Préparer un ticket » ou sur « Préparer une visite ».
3. **Ajouter le matériel.** Cherchez une référence du catalogue, saisissez la quantité prévue, puis la quantité préparée.
4. **Enregistrer.** Cliquez sur « Enregistrer ».

> [!NOTE] « Catalogue » liste les références disponibles. Si une référence manque, prévenez l'administrateur.

## Suivre les préparations

1. **Filtrer.** Choisissez le « Périmètre », tapez dans « Rechercher », fixez les dates « Du » et « Au, inclus », puis cliquez sur « Appliquer ».
2. **Lire les compteurs.** « À renseigner », « À préparer », « Partiellement préparées », « Prêtes » et « Sans matériel nécessaire ».
3. **Ouvrir une préparation.** Cliquez sur « Ouvrir » sur sa carte.

![Préparations : les compteurs par état, la sélection et les fiches à imprimer.](fig:preparation)

## Imprimer les fiches

1. **Sélectionner.** Cochez les préparations, ou cliquez sur « Sélectionner tous les résultats ».
2. **Prévisualiser.** Cliquez sur « Aperçu et impression ».
3. **Imprimer ou exporter.** Imprimez depuis l'aperçu, ou cliquez sur « Exporter les fiches ».

> [!RESULTAT] Chaque fiche reprend l'intervention et son matériel prévu et préparé.

| Vous voyez | Que faire |
|---|---|
| « Préparation partielle » | Une partie du matériel reste à préparer. Complétez les quantités préparées. |
| « Prête » | Tout le matériel prévu est préparé. |
| Une préparation absente de la liste | Vérifiez le périmètre et les dates, puis cliquez sur « Effacer les filtres ». |

# 7 Avoir des tickets à jour

Le contenu complet de tous les tickets (suivis, tâches, solutions) est relu dans GLPI par le poste collecteur de l'équipe, puis partagé avec les autres postes. Vous n'avez pas à le lancer : vous mettez seulement à jour votre liste, ou un ticket.

## Mettre à jour la liste

Quand : les tickets affichés sont anciens, ou au début de la journée.

1. **Ouvrir les données.** Cliquez sur « Données » dans le menu. Depuis l'Accueil, « Collecter depuis GLPI » mène au même écran.
2. **Se connecter si besoin.** Dans « GLPI en direct », cliquez sur « Se connecter » et terminez votre connexion GLPI dans la fenêtre qui s'ouvre.
3. **Mettre à jour.** Cliquez sur « Lancer la collecte ». Seule la liste de votre recherche GLPI est relue : c'est rapide.

> [!RESULTAT] La liste, les indicateurs de l'Accueil et le Pilotage reprennent l'état actuel de GLPI.

## Mettre à jour un ticket

Dans la fiche du ticket, cliquez sur « Actualiser la fiche ». Seul ce ticket est relu dans GLPI.

## Le contenu des tickets de l'équipe

Quand le partage entre postes est en place, le contenu relu par le poste collecteur arrive sur votre poste automatiquement, quelques minutes après. Si l'administrateur vous le demande, forcez l'échange : « Réglages », « Partage entre postes », puis « Synchroniser ». Sans partage, l'administrateur vous dit s'il faut collecter sur votre poste.

> [!ATTENTION] Ne lancez pas « Collecte et reprise » ni « Actualiser le cache » vous-même : ces commandes relisent tous les tickets dans GLPI, une page après l'autre, et peuvent durer longtemps. C'est le rôle du poste collecteur. Si le contenu des tickets vous semble ancien, prévenez l'administrateur.

## Si ça ne marche pas

| Vous voyez | Que faire |
|---|---|
| La fenêtre de connexion GLPI reste ouverte | Terminez la connexion GLPI dans cette fenêtre, puis cliquez de nouveau sur « Lancer la collecte ». |
| La liste est à jour, mais pas le contenu d'un ticket | Cliquez sur « Actualiser la fiche » dans ce ticket. |
| Le contenu de tous les tickets semble ancien | Prévenez l'administrateur : le poste collecteur ou le partage est peut-être arrêté. |

# 8 Suivre le Pilotage

Quand : vous voulez la situation du service ou d'une équipe, par exemple avant une réunion.

1. **Ouvrir.** Cliquez sur « Pilotage » dans le menu.
2. **Choisir ce qui est affiché.** Choisissez le « Tableau affiché ». Cliquez sur « Équipes » pour choisir une ou plusieurs équipes, ou choisissez un technicien.
3. **Lire la météo.** Chaque carte donne le volume de tickets et son état par rapport aux seuils. « Détail » et « Agrandir » montrent le contenu.
4. **Passer d'une rubrique à l'autre.** « Météo », « Stock », « À traiter », « Équipes », « Demandes », « Contributions », « Notes ».
5. **Présenter.** Cliquez sur « Présenter » pour afficher le tableau en plein écran pendant une réunion.

![« Choisir plusieurs équipes » : chaque ticket reste compté une seule fois dans le périmètre.](fig:pilotage-equipes)

> [!NOTE] Les seuils de la météo sont définis par l'administrateur ou le responsable d'équipe.

# 9 En cas de problème

## Messages fréquents

| Vous voyez | Que faire |
|---|---|
| « GLPI Pilot est déjà ouvert. Retrouvez sa fenêtre dans la barre des tâches ; son démarrage peut encore être en cours. » | Cliquez sur l'icône de GLPI Pilot dans la barre des tâches. Patientez si le démarrage est en cours. |
| L'écran ne se met pas à jour | Cliquez sur « Actualiser ». |
| Les tickets sont anciens | Mettez-les à jour (chapitre 7). |
| Une action GLPI au résultat incertain | Ne relancez pas. Ouvrez le ticket dans GLPI et vérifiez. |
| Un message que vous ne comprenez pas | Cliquez sur « Journal » en bas de la fenêtre et notez le message. |

## Quoi transmettre à l'administrateur

- Ce que vous faisiez, l'écran et l'heure.
- Le message exact, ou une capture de la fenêtre.
- Le numéro du ticket concerné.
- Le contenu du « Journal » au moment du problème.

> [!ATTENTION] N'effacez pas les données locales et ne copiez pas le dossier de GLPI Pilot d'un poste à l'autre : prévenez l'administrateur.

# 10 Repères

## Raccourcis clavier

| Touche | Effet |
|---|---|
| Ctrl K | Rechercher un écran, une action ou un ticket conservé |
| Ctrl B | Replier ou déplier le menu |
| Ctrl + / Ctrl − | Ajuster le zoom de 75 à 150 % |
| Ctrl 0 | Revenir au zoom de 100 % |
| F11 | Activer ou quitter la concentration |
| Tab / Maj Tab | Parcourir les commandes et les champs |
| Échap | Fermer une fenêtre de dialogue |
| Flèches / Entrée | Parcourir et sélectionner les lignes d’un tableau |

## Avant de partir en intervention

- Les tickets sont à jour (chapitre 7).
- L'intervention est enregistrée dans le planning, avec ses techniciens.
- Le matériel est préparé et la préparation est « Prête ».
- Les fiches terrain sont imprimées ou exportées.
- Vos notes sont enregistrées dans « Notes et rappels ».

# 11 Mots à connaître

| Terme | En clair |
|---|---|
| Espace | Une des grandes rubriques du menu : Accueil, Tickets, Interventions, Pilotage. |
| Périmètre | Les tickets que vous suivez : tout le service, vos équipes ou vos tickets. |
| Ma file personnelle | Votre liste de tickets à traiter, dans l'ordre que vous choisissez. |
| Identité | Votre nom tel qu'il apparaît dans les tickets ; il filtre vos tickets et vos interventions. |
| Collecte | La mise à jour de la liste des tickets depuis votre recherche GLPI. |
| Poste collecteur | Le poste de l'équipe qui relit régulièrement le contenu de tous les tickets dans GLPI. |
| Ticket conservé | Un ticket gardé sur le poste, consultable sans connexion. |
| Précédent | Un ticket déjà résolu, proche de celui que vous traitez. |
| Intervention | Un passage prévu dans le planning, lié ou non à un ticket. |
| Planning commun | Le planning partagé par l'équipe. |
| Tournée | Plusieurs visites ordonnées sur une journée, avec leur matériel et leurs fiches. |
| Site | Un lieu connu de l'équipe, qui sert à placer les tickets sur la carte. |
| Préparation | La liste du matériel prévu et préparé pour une intervention. |
| Fiche terrain | Le document imprimable d'une intervention et de son matériel. |
| Action assistée | Une action GLPI (suivi, tâche, affectation) relue puis confirmée une seule fois. |
| Météo | L'état d'un volume de tickets par rapport à ses seuils. |
| Concentration | Un affichage sans menu ni panneaux, activé par F11. |
