# Préparation terrain · guide 3.13.0

## Préparer une intervention, sans gérer un stock

Ce module décrit ce qu’il faut prévoir, préparer et emporter. Il ne calcule ni stock disponible, ni entrée/sortie, réservation d’inventaire, commande ou valorisation. Les modifications restent dans GLPI Pilot : préparer une fiche ne change pas l’affectation, le statut ou la description du ticket GLPI.

Dans **Interventions → Préparer le matériel**, choisir **Préparer un ticket** ou **Préparer une visite**. Une intervention créée manuellement dans le planning fonctionne même sans relevé GLPI chargé. Une tournée n’est jamais obligatoire. Les fiches de ticket et les cartes de visite proposent aussi un accès contextuel au matériel.

Ajouter des références, renseigner les quantités prévues et préparées, le préparateur et les consignes, puis enregistrer. Une erreur conserve les champs saisis. Fermer une saisie modifiée demande confirmation. Les brouillons ouverts sont bornés et leur autorisation d’enregistrement expire après 30 minutes : enregistrer régulièrement avant de quitter l’application.

Les états distinguent **Matériel non renseigné**, **Aucun matériel nécessaire**, **À préparer**, **Préparation partielle** et **Prête**. Une liste vide ne signifie jamais qu’aucun matériel n’est nécessaire. La quantité préparée ne peut pas dépasser le besoin. Les pièces et lots sont entiers ; les mètres acceptent trois décimales.

## Catalogue personnalisable

**Catalogue** permet de créer et modifier catégories, références, codes ou modèles, unités et précisions de compatibilité. Une nouvelle référence ou catégorie peut aussi être créée depuis l’éditeur de préparation sans quitter la saisie. Sa création enregistre le catalogue ; l’ajout de ses quantités à la fiche reste une action distincte.

Archiver les références devenues inutiles plutôt que les supprimer. Les catégories archivées empêchent l’ajout de leurs références à de nouveaux besoins. Chaque ancienne ligne conserve son identité, son nom, sa catégorie et son unité d’origine, même après un renommage du catalogue. Les références génériques livrées ne constituent pas une garantie de compatibilité avec les appareils d’une organisation.

Le catalogue accepte jusqu’à 100 catégories et 2 000 références. Une préparation accepte jusqu’à 100 lignes. Ces limites sont contrôlées avant l’écriture, sans enregistrement partiel.

## Pilotage et préparation du départ

**Pilotage → Matériel prévu** ouvre le même espace de préparation. Les filtres portent sur le site, le technicien, l’équipe, la tournée, la période et l’état du matériel, avec une portée personnelle ou commune. Le choix personnel exige une identité ; son absence n’élargit pas silencieusement la vue à tout le service. Les filtres détaillés sont mémorisés dans les préférences de l’espace.

La date de fin choisie dans l’interface est incluse. Les préparations sans créneau restent incluses dans une recherche par période. Les fiches effectuées ou annulées sont masquées par défaut et peuvent être réaffichées. **Actualiser** relit les données connues de l’application ; ce bouton ne déclenche pas de collecte GLPI.

Les références sont agrégées par identité et unité, pas seulement par nom. Une même préparation liée à plusieurs participants ou sélectionnée depuis plusieurs contextes est comptée une seule fois. Les mètres ne sont pas additionnés aux pièces.

### Ticket et visite : deux besoins distincts

Le matériel prévu sur un ticket accompagne ce ticket. Les lignes ajoutées directement à une visite représentent ses besoins complémentaires ou communs. Lorsque ces deux types de fiche coexistent, ils sont conservés et cumulés, avec un avertissement invitant à vérifier les lignes éventuellement saisies deux fois. Le logiciel ne supprime pas arbitrairement l’un des besoins et ne devine pas une répartition entre visites.

Le filtre d’une tournée inclut ses préparations de visite et les préparations enregistrées des tickets liés. Un ticket commun à plusieurs arrêts n’est pas compté plusieurs fois. Définir les besoins communs sur la visite et les besoins propres à chaque ticket sur sa fiche évite les doublons de saisie.

## Fraîcheur des fiches

À la lecture, le moteur rapproche la préparation du planning et du relevé actuellement connus sur le poste. Un déplacement, y compris de plusieurs mois ou après découpage d’une série répétée, conserve la clé de visite et le matériel enregistré. Une annulation ou une clôture reconnue retire les quantités des besoins de départ. Les lectures ne réécrivent pas silencieusement l’historique des lignes.

Une intervention supprimée reste consultable comme fiche conservée, hors des totaux actuels. Un ticket absent d’un relevé filtré n’est **pas** déclaré supprimé ou clos : sa source reste non vérifiée. Un conflit dans le planning empêche également de présenter les besoins comme confirmés. Le message de chaque fiche indique ce qui a été rapproché ou n’a pas pu être confirmé.

Rouvrir une fiche par son ID actualise son contexte lorsqu’il peut être retrouvé. Enregistrer vérifie que ce contexte et la révision de la préparation n’ont pas changé pendant la saisie. Les descriptions déjà observées restent conservées lorsque le nouveau contexte ne fournit pas de description ; elles gardent leur date d’observation.

Les compteurs couvrent les préparations **enregistrées**. Ils ne prétendent pas recenser toutes les interventions encore sans fiche. Ils ne prouvent pas non plus une collecte GLPI ou une synchronisation réseau à l’instant présent. Vérifier la date du relevé et le dernier succès de synchronisation avant le départ.

## Sélection, tournée entière et impression

Cocher une ou plusieurs préparations puis ouvrir **Aperçu et impression**. **Sélectionner tous les résultats** prend les identifiants et révisions du périmètre complet, pas uniquement la page visible. Une sélection au-delà de 200 fiches est refusée explicitement ; affiner les filtres au lieu d’obtenir un document tronqué.

**Fiches de la tournée entière** inclut tous les arrêts, y compris ceux sans préparation de matériel enregistrée. Ces visites sont signalées « à renseigner » ; les lire ou les imprimer ne crée pas de fausse préparation dans la base. Les préparations enregistrées des tickets liés figurent séparément, une fois chacune, avec leurs détails disponibles et leur matériel. Les besoins communs aux visites restent complémentaires.

L’aperçu fournit une liste de départ, les fiches, les quantités, les consignes et des espaces de compte rendu. **Exporter en HTML** utilise le sélecteur de fichiers de l’hôte. **Imprimer ces fiches** ouvre l’impression ; utiliser l’imprimante physique ou PDF configurée sur le poste. Seules les fiches sélectionnées sont placées dans le document d’impression, pas le tableau de bord en arrière-plan.

Une source non vérifiée bloque l’impression comme document de départ actuel. Le choix explicite **Autoriser les archives non vérifiées** permet une impression datée et signalée ; les quantités concernées sont exclues de la liste de départ. Leur état actuel reste inconnu, sans inventer une annulation.

Une fiche modifiée depuis la sélection ou en conflit exige une nouvelle lecture. Les limites de taille et de nombre de fiches provoquent un refus explicite, jamais une impression partielle silencieuse. Aucune collecte de détails GLPI n’est cachée derrière l’impression : seuls les détails disponibles sont reproduits, les autres sont indiqués comme indisponibles.

## Partage facultatif et conflits

Les documents utilisent la base SQLite locale de l’espace d’équipe et sa file de synchronisation existante. Le partage reste facultatif, par dossier partagé ou PostgreSQL selon la configuration. L’application ne transforme pas le fichier SQLite local en base à ouvrir directement depuis plusieurs postes sur un partage réseau.

**Avant d’utiliser la préparation terrain dans un espace partagé, mettre à jour tous les postes participants vers 3.12.0 ou plus.** Les clients 3.11.x ne connaissent pas les nouveaux types de documents et ne doivent pas être mélangés avec les clients qui les publient. Sauvegarder les bases et conserver les réglages privés avant le déploiement.

Une modification concurrente conserve les versions reçues et bloque les écritures non examinées. **Voir les versions** affiche le contenu actuellement enregistré et celui reçu. Choisir explicitement la version à conserver puis confirmer. La révision est contrôlée de nouveau : un autre changement invalide le choix au lieu d’être écrasé.

Pour un catalogue, les références et catégories présentes uniquement dans la version courante sont conservées archivées quand la version reçue est choisie. La résolution conserve les deux documents dans un journal local et propage les identifiants des conflits résolus. Rejouer un ancien échange ne rouvre pas automatiquement le même conflit. Les conflits supplémentaires restent à examiner individuellement.

## Kits suggérés

L’éditeur peut proposer du matériel déjà prévu ensemble dans les préparations locales récentes. Le motif est affiché, les références archivées ou dont l’unité a changé sont exclues. L’ajout exige une confirmation, conserve les lignes existantes et remet les quantités préparées à zéro. Ces suggestions ne contactent aucun modèle distant et ne garantissent pas la compatibilité du matériel.

## Données privées et capacité

Le catalogue et les préparations sont des données opérationnelles de la base locale ou de l’espace partagé. Ils ne sont pas incorporés au JSON de configuration par défaut ni aux packs publics. Les fichiers de sites, le JSON privé de déploiement, les tickets réels et les profils SSO ne doivent pas être ajoutés au dépôt ou aux archives publiques.

La vue complète accepte jusqu’à 5 000 préparations et refuse un total partiel au-delà de sa capacité. Exporter et sauvegarder l’espace d’équipe avant toute opération de maintenance. Les opérations génériques de suppression ne peuvent pas effacer une préparation ou un catalogue en contournant leur parcours métier.

## Vérification de livraison

Les suites `FieldPreparationChecks`, `FieldPreparationWorkspaceChecks`, `FieldPreparationBridgeChecks`, `FieldPreparationGuardChecks`, `FieldCompletionChecks`, `FieldTourChecks` et `FieldPreparationUiChecks` couvrent les règles, la persistance, les clés stables, les changements de source, les conflits, la sélection et les formulaires. `FieldIntegratedUiChecks` fait passer les vrais modules d’interface par le vrai `WebBridge` et `PilotApplication` vers SQLite. `WindowsStartupChecks` comprend un parcours terrain dans les deux EXE réellement extraits du pack Windows, avec leur runtime WebView2 fixe et le sélecteur de fichiers natif.

Le résultat à retenir est celui du **commit exact du pack**, indiqué dans `wad/version.json`, les manifestes et les journaux CI. Un test Chromium isolé n’est pas présenté comme un démarrage Windows. Les transports de test utilisent des dossiers temporaires et un serveur PostgreSQL éphémère ; ils ne qualifient pas le serveur SMB privé, les droits réseau, les imprimantes ou le portail GLPI/SSO de l’organisation.
