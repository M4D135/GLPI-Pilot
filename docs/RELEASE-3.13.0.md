# GLPI Pilot 3.13.0 · Ergonomie, tickets et collecte reprenable

## Une interface plus directe, sans changer le look validé

Un nouveau logo et une icône Windows associent le ticket et sa flèche sur un fond orange pastel, selon le dernier choix de David. Les deux EXE et l'interface utilisent les mêmes ressources locales ; les thèmes et animations restent conservés.

Les widgets proposent des aperçus de listes adaptés à leur taille, sans ascenseur vertical dans chaque carte. Le nombre d'éléments chargés supplémentaires reste annoncé. Agrandir déplace le contenu interactif existant dans une vue de lecture, puis restaure sa place et le focus. Les données, filtres et dénominateurs ne sont pas réduits pour obtenir un petit widget.

Les positions enregistrées, les hauteurs indépendantes, les cartes verrouillées et les trois tailles sont conservées. Une fenêtre étroite ne réécrit pas les coordonnées de bureau. Les déplacements au clavier ne sélectionnent pas les lignes masquées d'un aperçu. Le thème et les animations existants sont conservés.

La molette au-dessus d'une carte intégrée laisse défiler la page, y compris au-dessus des boutons de zoom. Les boutons +/− restent utilisables. Agrandir la carte autorise le zoom à la molette dans cette vue explicite ; Échap restitue le contexte de départ. La liste et les options sont repliables. Les pointillés représentent l'ordre des visites, pas un itinéraire routier calculé. Les autorisations réseau et les lieux sans coordonnées restent explicites.

## Matériel, tournées et fiches

Chaque ligne de matériel montre d'abord la référence, les quantités prévues/préparées et le reste à préparer. Précisions et préparateur se déplient à la demande. Recherche, choix d'une référence et Entrée permettent un ajout rapide ; catalogue et kits restent disponibles.

Ligne préparée agit sur une ligne ; la préparation groupée exige une confirmation et reste à enregistrer. Les états non renseigné, aucun matériel, à préparer, partiel et prêt restent distincts. Les révisions, conflits, unités et quantités décimales sont conservés. Une sauvegarde en cours bloque les modifications concurrentes du formulaire ; un refus restitue la saisie.

Les commandes de fiches apparaissent avec la sélection. Les filtres sont repliables, avec résumé et remise à zéro. Les compteurs manager ouvrent les filtres correspondants ; la date de fin est inclusive. Les sélections inchangées sont conservées ; les révisions périmées sont retirées avec un message, et les réponses tardives ne remplacent pas les décisions récentes.

Les étapes de tournée sont numérotées, avec leurs avertissements et verrouillages. Enregistrer, Carte, Matériel et Fiches sont les commandes principales. Ouvrir le matériel ou les fiches d'un brouillon propose son enregistrement ; annuler ne sauvegarde rien et ne change pas d'écran. Le retour conserve l'identité de la tournée. Les interventions sans tournée restent possibles. **Aucune gestion de stock, achat, valorisation ou réservation physique.**

## Pilotage assisté des tickets

Suivis, tâches et affectations sont regroupés au moyen des adaptateurs existants. Une action indisponible n'est pas remplacée par une autre. Planning, matériel, fiches, notes, checklist, rappels et compte rendu restent distincts des modifications GLPI.

Toute soumission conserve inspection explicite, aperçu, confirmation et reçu. Une saisie modifiée, un aperçu fermé ou périmé ne peut pas être réutilisé. Un résultat indéterminé demande une vérification manuelle, jamais un renvoi automatique. Une fermeture demandée pendant la relecture du résultat reste prise en compte.

**Statut, priorité et résolution ouvrent la fiche GLPI native : aucun nouvel adaptateur d'écriture automatique de ces champs.** Les droits restent ceux de la session GLPI ; imports et archives restent en lecture seule.

## Collecte reprenable et mises à jour différentielles

**Données → Collecte et reprise** rassemble Mettre à jour, Pause, Arrêter et Reprendre, avec progression, dernière activité, journal et réglages repliables. Le journal SQLite et les contenus déjà enregistrés survivent à l'interruption. La reprise revalide l'index et le périmètre sans retélécharger les contenus complets et confirmés toujours valides.

Le premier remplissage collecte les contenus manquants des tickets accessibles. Ensuite, les nouveaux tickets, dates modifiées, contenus incomplets ou non confirmés sont examinés. Les dates non fiables ne certifient pas une absence de changement. Une absence dans un index ne supprime pas automatiquement le ticket et une lecture partielle n'efface pas une ancienne copie complète. L'index reste relu pour découvrir les changements : le mode différentiel ne signifie pas zéro requête réseau.

Rythme initial : une lecture à la fois, 3 secondes entre navigations, pause de 30 secondes après 25 navigations, budget de 60 minutes. Ces limites sont configurables, avec créneau horaire et seuil d'erreurs. Le rythme porte sur les navigations, non chaque sous-requête HTTP du navigateur. Il limite la charge sans chercher à dissimuler l'activité et doit être adapté aux règles du portail.

La programmation est désactivée par défaut. Son activation demande de désigner explicitement ce poste comme collecteur ; elle nécessite GLPI Pilot ouvert. Pause et arrêt manuel restent prioritaires sur le planificateur. Le partage par dossier ou PostgreSQL utilise un verrou de collecteur ; sa perte bloque la navigation suivante, sans repli local silencieux. Des postes totalement indépendants ne sont pas coordonnés par ce verrou.

## Distribuer une bibliothèque initiale sans recommencer la collecte

Le paquet interne **`.glpilibrary.zip`** contient les documents de tickets, leur provenance et un manifeste versionné vérifié par SHA-256. L'import présente un aperçu, vérifie l'installation, puis fusionne dans une transaction. Une corruption tardive ne laisse pas d'import partiel ; une réimportation identique est idempotente et les versions concurrentes restent conservées.

Cookies, mots de passe, identité de synchronisation, programmation et paramètres personnels ne sont pas transférés. Aucun démarrage de collecte ni partage automatique des données importées. Les exports de configuration et de corpus JSONL restent distincts.

**Ces paquets ne sont pas chiffrés et peuvent contenir des informations privées. Ils sont réservés à des destinataires habilités, via un canal interne protégé, jamais aux releases publiques.** Le vérificateur des packs génériques refuse ces archives et les dossiers de préparation d'import.

Les descriptions, suivis, tâches et solutions réellement disponibles conservent leur provenance pour une future aide à la résolution. Les pièces jointes restent des références et métadonnées : leurs fichiers binaires ne sont pas téléchargés automatiquement. Aucun chatbot distant ni envoi automatique vers un modèle n'est ajouté.

## Qualification et intégrité

La publication exige les prévalidations Standard/Admin, les contrôles PostgreSQL de synchronisation et de verrou collecteur, les tests de pause/reprise et de transfert, les modules sous la CSP de production, les vrais handlers C#/SQLite, puis toute la chaîne Windows : construction, véritables EXE Standard/Admin, menu et rythme de collecte, parcours terrain, mise à jour et retour arrière.

Les manifestes internes, sources DEV, ressources web source/runtime, identités des exécutables et empreintes des ZIP originaux sont contrôlés. Les fichiers publics sont retéléchargés après publication et comparés aux archives originales CI. La reconstruction DEV n'est pas substituée aux fichiers publiés. Les preuves sont conservées séparément et correspondent au commit de `wad/version.json`, pas à un ancien succès.

## Installation et limites

Sauvegarder les bases et réglages privés avant remplacement. Extraire tout le pack : `GLPI-Pilot.exe`, `GLPI-Pilot-Admin.exe` et `wad` restent ensemble. PROD contient l'application autonome ; DEV ajoute les sources et outils. Les exécutables génériques sont non signés : les empreintes établissent l'intégrité, pas une signature d'éditeur.

Harmoniser les clients collecteurs et les postes échangeant les nouveaux paquets en **3.13.0 ou version ultérieure**. Un ancien client ne connaît pas les nouveaux verrous de collecte. Aucun site, pack Sites, JSON privé de déploiement, bibliothèque interne ou base opérationnelle n'est fourni dans les packs génériques.

Fermer le panneau pendant sa requête peut interrompre la collecte, sans perdre sa reprise. Le logiciel n'est pas un service Windows autonome. Le GLPI/SSO privé, les autorisations SMB et les imprimantes de l'organisation ne sont pas qualifiés par la CI. Les pages et données de test sont fictives ; aucun ticket privé n'a été modifié.

Guide détaillé : `wad/docs/Collecte-des-tickets.md`. Les guides d'ergonomie, de pilotage ticket et de préparation terrain restent inclus.
