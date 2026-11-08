# Collecte des tickets et bibliothèque transférable

## Le parcours quotidien

Dans **Données**, ouvrir **Collecte et reprise**. Le look et les animations existants sont conservés. L'écran présente l'état, la dernière activité et l'action pertinente ; les réglages, les transferts et le journal se déplient uniquement à la demande.

**Mettre à jour** lit l'index des tickets autorisés de l'installation courante, tous statuts lorsque ce périmètre est effectivement confirmé. Le premier passage récupère les contenus manquants ; les passages suivants conservent les contenus complets et confirmés dont la date de modification fiable est inchangée. Une date absente, ambiguë, un contenu incomplet ou un changement de lecteur peuvent nécessiter une relecture. L'index peut être relu : « différentiel » concerne les contenus, pas une absence de requêtes pour découvrir les changements.

Configurer le fuseau et le format des dates source dans les réglages de bibliothèque. Un horodatage inexploitable ne permet pas de certifier qu'un ticket n'a pas changé. La confirmation finale contrôle à nouveau les dates et le périmètre ; un résultat partiel n'efface pas une ancienne copie complète et une absence ne devient jamais une suppression automatique.

Une copie dépassant l’âge maximal configuré dans la bibliothèque (sept jours par défaut) est relue même si le jeton source reste identique. Cette réconciliation périodique limite la réutilisation de vieux contenus ; elle ne prouve pas que toutes les modifications de sous-rubriques sont reflétées immédiatement par l’horodatage exposé par chaque portail.

## Pause, arrêt et reprise

**Pause** ou **Arrêter** annulent les nouvelles lectures et conservent les contenus déjà validés dans SQLite, ainsi que le journal de reprise. Une demande cible la session de collecte affichée : un ancien bouton ne peut pas arrêter une session différente.

**Reprendre** réutilise le même journal, vérifie le contexte et revalide l'index. Une fiche déjà enregistrée mais non encore confirmée peut être confirmée à partir de sa preuve exacte, sans télécharger son contenu à nouveau. Les tickets nouveaux ou modifiés rejoignent le travail nécessaire. Les erreurs peuvent être reprises de manière ciblée, sans élargir une sélection à toute la base.

Une pause ou un arrêt manuel suspend également le planificateur jusqu'à une reprise explicite. Après un arrêt inattendu du logiciel, le point de reprise devient interrompu et demande une décision manuelle. Fermer le panneau ou naviguer pendant sa requête peut interrompre la collecte : la progression enregistrée reste récupérable, mais ce fonctionnement n'est pas un service Windows autonome.

Ne pas copier le fichier SQLite ouvert pour transférer une bibliothèque. Utiliser le transfert dédié ci-dessous.

## Fréquence et charge

La programmation est désactivée par défaut. Pour l'activer, confirmer que ce poste est le collecteur désigné. GLPI Pilot doit rester ouvert, disponible et hors d'une édition bloquante. Un poste hors ligne, verrouillé, en simulation, sur un import ou un relevé historique ne déclenche pas cette collecte automatique.

Les réglages sont locaux et liés à la source, séparés de la synchronisation entre postes :

- Intervalle entre passages : 5 à 10 080 minutes ; valeur initiale 120 minutes.
- Délai entre navigations : 1 000 à 60 000 ms ; valeur initiale 3 000 ms, une lecture à la fois.
- Lots de 1 à 500 navigations, pause de 0 à 3 600 secondes ; valeurs initiales 25 et 30 secondes.
- Budget par passage de 1 à 1 440 minutes ; valeur initiale 60 minutes.
- Créneau automatique dans un fuseau explicite, y compris un créneau qui traverse minuit. 00:00 à 00:00 signifie toute la journée.
- Arrêt après 1 à 10 erreurs consécutives ; valeur initiale 3.

Le rythme s'applique aux navigations de liste, fiches, onglets et pagination détaillée. Il ne compte pas chaque sous-requête HTTP interne du navigateur. Ces limites réduisent la charge ; elles ne garantissent pas un niveau universel adapté à tout serveur et ne visent pas à dissimuler une activité. Respecter les droits, les règles d'accès et les limites du portail.

Quand le budget est atteint, une programmation déjà autorisée peut reprendre ultérieurement le même travail. Une pause manuelle, un arrêt, une interruption ou une erreur demandant attention restent prioritaires sur cette programmation. Une défaillance de stockage bloque la poursuite : le logiciel ne continue pas à lire des données qu'il ne peut conserver.

## Un collecteur et plusieurs lecteurs

Désigner un seul poste collecteur par périmètre. Les autres postes peuvent recevoir la bibliothèque initiale, puis utiliser la synchronisation facultative par dossier partagé ou PostgreSQL.

Pour une même installation GLPI dans un même espace partagé, un verrou exclut les collecteurs simultanés. La perte de ce verrou bloque la navigation suivante ; aucun repli silencieux vers une collecte locale non coordonnée n'est autorisé. Le mode purement local ne coordonne pas des postes indépendants. Ce mécanisme ne remplace pas la désignation d'un collecteur, et un ancien client ne connaît pas ces nouveaux verrous.

Avant de l'utiliser collectivement, sauvegarder les bases et harmoniser tous les clients collecteurs et échangeant les nouveaux paquets en **3.13.0 ou version ultérieure**. Les autorisations du dossier ou de PostgreSQL restent celles définies par l'organisation.

## Transférer une bibliothèque de départ

Dans **Transférer une bibliothèque préparée**, exporter un paquet **`.glpilibrary.zip`**. Il contient un manifeste versionné et les documents de tickets de l'installation sélectionnée, avec leurs contenus disponibles, dates, provenance et empreintes. Ce n'est pas une copie complète du profil : cookies, mots de passe, identité de synchronisation, programmation, notes personnelles, catalogue matériel et paramètres de connexion ne sont pas exportés par ce transfert.

Sur le poste destinataire, sélectionner l'installation correspondante puis importer le paquet. L'aperçu vérifie l'identité, le format, les chemins et les empreintes. L'application utilise une copie locale de préparation et un jeton à durée limitée. La confirmation effectue une fusion transactionnelle ; une corruption détectée tardivement ne laisse pas de premières lignes partiellement importées. Une réimportation identique est idempotente ; les versions concurrentes restent conservées.

Les contenus non confirmés ne deviennent pas confirmés du seul fait du transfert. Les données importées ne sont pas automatiquement publiées vers l'espace partagé. Aucune collecte ne se déclenche à l'import ; une mise à jour explicite peut ensuite récupérer les différences.

**Le paquet n'est pas chiffré et peut contenir des informations privées accessibles au collecteur.** Le diffuser exclusivement par un canal interne protégé à des destinataires habilités au même périmètre. Il ne doit jamais être joint à une issue, une PR, une release GitHub ou un pack PROD/DEV public. Les empreintes détectent une altération ; elles ne remplacent ni le chiffrement ni le contrôle des destinataires.

## Base du futur assistant

La bibliothèque conserve les descriptions et les rubriques réellement disponibles, notamment suivis, tâches et solutions, avec leurs références et leur provenance. Les références et métadonnées des pièces jointes sont conservées ; leurs fichiers binaires ne sont pas téléchargés automatiquement. Les contenus privés ou absents ne sont jamais inventés.

L'export de corpus JSONL existant reste distinct du paquet de transfert. Il peut servir de source à une future indexation et à des réponses citant leurs tickets d'origine. Cette version n'ajoute pas un chatbot distant ni une transmission automatique des tickets vers un modèle. Toute exploitation ultérieure doit maintenir les habilitations, la fraîcheur et les limites des documents.

## Vérifications et limites

La livraison exige les tests de persistance/reprise/transfert sur SQLite, les parcours du contrôleur et du lecteur de production sur portail fictif, le menu sous la CSP de production, les régressions ergonomiques Standard/Admin, les contrôles PostgreSQL et les parcours des véritables EXE. Le menu et la sauvegarde de son rythme sont exercés dans les hôtes Windows empaquetés sans déclencher de collecte privée.

La qualification ne teste pas le GLPI ou le SSO de l'organisation, ses autorisations SMB ou ses imprimantes. Vérifier le périmètre et la compatibilité du portail lors du premier passage réel. Le résultat complet de la chaîne CI et la concordance du commit dans les manifestes, tags et archives restent nécessaires avant d'annoncer une livraison.
