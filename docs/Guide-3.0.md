# GLPI Pilot 3.0 — interface WebView, parcours et maintenance

L’application utilise un hôte C# WinForms minimal et WebView2. Les écrans sont des modules HTML/CSS/JavaScript locaux ; les règles, calculs, archives et échanges GLPI restent dans le moteur C#. Le modèle d’architecture vient de Domenia v2. Le pack embarque .NET et un runtime WebView2 fixe : extraire tout le ZIP, conserver `wad` à côté de `GLPI-Pilot.exe`, puis lancer l’EXE. Edge ou Chrome reste nécessaire pour la session de collecte GLPI et son SSO. Aucun serveur web, installation Node.js ou runtime .NET séparé n’est requis pour utiliser le pack. Les guides spécialisés détaillent les calculs et la lecture des tickets. La couverture exacte des 200 propositions figure dans [le registre de suivi](Suivi-200-ameliorations.md).

## Préparer ses sources

Dans **Mes sources**, enregistrer une recherche complète par profil. Un profil conserve URL, navigateur, méthode, repères, délais, limites, dossier de sortie local et règles d’interprétation. **Actualiser avec les réglages courants** demande confirmation avant de remplacer la recette d’un profil existant. La capture d’une nouvelle source reprend aussi les réglages courants ; son éditeur permet de modifier le nom, l’adresse, le navigateur, la méthode, l’inclusion en campagne et la restauration de son espace de travail.

L’option de restauration de l’espace de travail permet aussi de retrouver les requêtes et dispositions du profil. Sans cette option, les tableaux de bord actuels sont conservés. L’identité personnelle et la préférence d’ouverture personnelle restent celles du poste.

Cocher les profils d’une campagne, puis lancer celle-ci. Chaque source a son propre résultat. Une erreur propre à un portail n’empêche pas les autres sources de passer ; l’annulation arrête l’enchaînement. Le **Bilan de campagne** et les fichiers JSON/CSV distinguent succès, résultat partiel, échec, annulation et source non lancée.

## Comprendre les reprises de collecte

**Configuration → Collecte et exports** permet de régler séparément les délais de liste et de fiche, les tentatives de reconnexion, l’espace libre minimal et la validité des points de reprise. Les délais SSO et téléchargement restent dans les réglages correspondants.

Un point conserve uniquement des pages déjà contrôlées. Lors d’une relance, le moteur vérifie première page, dernière page, structure, total et périmètre avant de continuer. Une recherche incompatible redémarre depuis le début. Une pagination AJAX dont la dernière page ne peut pas être retrouvée de manière fiable redémarre également ; aucune ancienne page n’est réutilisée par supposition.

En cas d’expiration SSO, utiliser **Afficher le navigateur** pour effectuer la validation demandée. Les tentatives sont bornées et les pages déjà lues restent conservées si la connexion échoue. Les critères ou l’entité changés interdisent de joindre les nouvelles pages à l’ancienne collecte. Une recherche vivante peut évoluer pendant la lecture : les contrôles ne créent pas un instantané transactionnel du serveur.

## Ajuster les données sans perdre l’original

Dans le relevé, **Correspondances** ouvre trois étapes : choix des rôles, conventions de source et aperçu des cellules. Les formats comprennent cultures de dates/nombres, fuseau facultatif, séparateurs multiples et dictionnaires de valeurs. L’application demande de lire l’aperçu avant d’appliquer.

Une colonne renommée donne lieu à une proposition. Cette proposition ne change pas seule la configuration. Les archives conservent les règles de leur date ; l’éditeur évite d’appliquer les colonnes d’une source à un autre profil. Les réglages d’un import local restent séparés des profils GLPI.

**F2** ouvre la cellule courante, complète, avec sa valeur brute et son interprétation. La copie laisse les cellules source intactes. Les champs d’en-tête filtrent tout le relevé avant la pagination. Un clic sur le titre trie la colonne ; Maj + clic ajoute un critère. **Filtres et tri** permet de choisir les opérateurs et les types texte, nombre ou date. L’affichage est paginé à 50, 100, 250 ou 500 lignes ; un export conserve tout le périmètre filtré. Le sélecteur de colonnes propose des familles — identité, affectation, dates, localisation — que l’on peut afficher ou replier ensemble.

Le **panier** garde des identités composées de l’installation, du type d’objet et de l’identifiant. Il résiste au changement de filtre et au redémarrage. L’affichage du panier dans un relevé ne montre que les identités retrouvées sans ambiguïté ; les absents restent conservés. Des numéros identiques dans deux installations ou types ne sont pas fusionnés.

## Lire les analyses et produire un rapport

Dans **Historique et analyses**, sélectionner une série de relevés comparables et la cadence attendue. Les lacunes, incompatibilités et incertitudes sont visibles. Les cohortes, transferts, mouvements et durées de statut décrivent les observations réellement disponibles. Les scénarios futurs et anomalies sont des aides à l’examen, avec hypothèses et effectifs.

Le référentiel d’effectifs conserve, pour chaque site, une période de validité, une unité et une source. Un taux pour cent personnes ou postes n’apparaît qu’avec un dénominateur positif compatible à la date du relevé. La carte locale utilise les coordonnées disponibles et filtre les autres widgets ; elle ne télécharge pas de fond cartographique.

Le dashboard propose **Exporter**, puis **PNG** et **PDF** avec cartouche : date, source, périmètre, filtres et complétude. Le rapport HTML autonome réunit aussi critères, qualité et toutes les lignes du périmètre exporté. Examiner le périmètre avant partage : ces rapports contiennent des données métier, contrairement au diagnostic technique nettoyé.

## Organiser les tickets et les interventions

Les rubriques structurées montrent tâches, validations, attentes, échanges, relations et documents observés. L’absence d’une date ou d’un auteur ne devient pas une valeur supposée. Le téléchargement d’un document reste une action demandée par l’utilisateur.

Une file d’enrichissement permet de choisir des tickets du relevé ou d’une requête, leur priorité et un plafond de lecture. Elle conserve dates, succès et erreurs et peut être annulée. La relecture d’un absent historique fournit une observation actuelle séparée ; elle ne réécrit pas l’archive et ne transforme pas l’absence en résolution.

Les actions GLPI assistées distinguent préparation et publication. Examiner la cible, les champs, l’état observé et le contenu complet, puis confirmer une seule fois. Le formulaire doit appartenir au ticket et à l’installation attendus. Après envoi, un reçu et une relecture indiquent le résultat. Une réponse ambiguë impose un examen du portail : l’application ne répète pas la soumission automatiquement.

Pour les tournées, définir les horaires récurrents des sites, fermetures, contacts et matériel ; compléter présences, absences, compétences et capacités des techniciens. La proposition sur plusieurs jours respecte les contraintes connues et expose les visites non affectables. Elle ne garantit pas un optimum. Une modification invalide l’état « prête » et les résultats routiers devenus périmés.

## Choisir les services facultatifs

| Fonction | Réglage et déclenchement | Données utilisées |
| --- | --- | --- |
| Routage | Endpoint compatible OSRM, profil, consentement puis calcul demandé | Coordonnées ordonnées du parcours ; distances/durées et source reçues |
| Météo terrain | Endpoint compatible Open-Meteo, consentement puis consultation demandée | Coordonnées et dates des sites ; prévisions, source et date de consultation |
| Assistant local | Sélection du moteur local et préparation de la demande | Schéma et observations chargées, traités sur le poste par des règles explicites |
| Assistant externe | Endpoint compatible chat/completions, modèle, clé et consentement sur le contenu préparé | Uniquement la sélection et les champs affichés dans l’aperçu de transmission |

Les endpoints réseau sont désactivés par défaut. Les tests livrés utilisent des réponses simulées et ne prouvent pas la disponibilité d’un fournisseur donné. Une consultation périmée après changement de date, site ou parcours ne reste pas présentée comme actuelle.

L’assistant possède trois étapes : **Demande**, **Contenu à utiliser**, **Proposition et vérification**. Générer une proposition ne crée ni n’exécute automatiquement une requête. Le schéma borné, les références et le contexte sont contrôlés ; prévisualiser le résultat, puis demander l’ajout au brouillon de l’atelier. Une synthèse cite des observations fournies. Ces contrôles techniques ne remplacent pas la relecture du sens de la proposition.

## Programmer et suivre

Dans **Automatisations**, renseigner plusieurs heures locales et jours actifs, ou conserver le mode intervalle. L’application doit rester ouverte. Les heures inexistantes au changement d’heure ne sont pas inventées. Les périodes silencieuses et le verrouillage Windows retardent les lancements et notifications ; les rappels restent disponibles.

Une recette commence par une extraction inconditionnelle, puis peut contrôler la qualité, enrichir les lieux, archiver, exporter CSV/XLSX et évaluer les alertes. Les étapes possèdent des conditions de complétude et de nombre minimal de lignes. Un reçu brut de l’acquisition reste conservé.

Dans **Alertes**, les règles peuvent suivre une valeur, un écart ou une variation relative sur une référence comparable. L’absence de référence ou un dénominateur nul ne devient pas une variation valide. Les nouveaux contributeurs, répétitions, prises en compte, reports et résolutions motivées sont historisés. **Santé des collectes** distingue absence de collecte, ancienneté et échecs répétés.

## Importer et adapter l’interface

Le comparatif d’import présente sections, conflits et changements champ par champ. Pour un conflit, choisir l’actuel, l’importé ou les deux variantes. L’aperçu utilise les mêmes décisions que l’application. Les références des variantes sont réécrites ensemble ; les limites restent vérifiées avant enregistrement. Les chemins locaux, l’identité, le SSO et les secrets ne sont pas repris d’une configuration d’équipe.

**Configuration → Apparence et accessibilité** donne accès aux palettes adaptées à la vision des couleurs, au suivi des animations Windows, au moniteur de démarrage, à l’ordre, au masquage et à l’épinglage des rubriques. Le zoom natif de WebView2 se règle entre 75 et 150 %, indépendamment de la densité. Les boutons − / pourcentage / + et Ctrl + / Ctrl − / Ctrl 0 / Ctrl + molette sont disponibles. **F11** masque la navigation pour travailler dans la vue ; **Ctrl+K** reste disponible. Les positions de lecture et présentations de tableaux sont conservées dans leur contexte, sans transférer une sélection entre installations.

## Mettre à jour et revenir à la version précédente

1. Ouvrir **Mise à jour et retour arrière** depuis la navigation ou Ctrl + K.
2. Consulter la release du dépôt officiel. Examiner version, commit, édition et taille, puis demander le téléchargement et la vérification. Un ZIP local peut aussi être utilisé avec des références externes de confiance.
3. Lire le résultat : empreinte de l’archive, inventaire exact des fichiers, provenance et éventuelle signature. Choisir **Installer** seulement après vérification ; le programme ferme sa fenêtre et l’auxiliaire attend sa sortie réelle.
4. Le nouveau programme doit confirmer son démarrage. Si la copie ou le démarrage échoue, les fichiers précédents sont restaurés. Une copie encore verrouillée empêche la restauration automatique et laisse la sauvegarde intacte avec un état de récupération à traiter. Ne supprimer ni la sauvegarde ni le journal de transaction avant résolution.

La procédure travaille sur le volume de l’installation et peut donc séparer les données du poste de l’emplacement des binaires. Le retour manuel utilise la sauvegarde vérifiée. Aucun contrôle GitHub ni téléchargement ne se lance simplement parce que l’application démarre.

### Signature d’éditeur

La chaîne de compilation accepte `GLPI_SIGN_CERT_THUMBPRINT` et `GLPI_SIGN_TIMESTAMP_URL`. Le certificat doit être destiné à la signature de code, disposer de sa clé privée, être valide et produire une signature Authenticode Windows `Valid` avec horodatage. `build-tools/Sign-Release.ps1 -Required` refuse une configuration absente.

La livraison présente n’utilise aucun certificat de confiance fourni par l’éditeur. L’EXE est donc annoncé non signé. Commit et SHA-256 contrôlent la provenance/integrité attendue ; ils ne remplacent pas l’identité cryptographique de l’éditeur. L’assistant permet d’exiger cette signature et de fixer l’empreinte de l’éditeur lorsque l’organisation dispose de ce certificat.

## Preuves et validation sur poste

Le workflow exécute les moteurs, le navigateur synthétique, les pages locales branchées aux vrais services C#, le vrai hôte WebView2 Windows, les boutons et fenêtres du pack, puis quatre scénarios Windows de remplacement/récupération. Consulter [le rapport de vérification](Rapport-verification.txt), les journaux et les captures du commit livré.

La session SSO privée, les droits effectifs GLPI, les extensions du portail, les transitions de veille propres au poste et les configurations physiques de plusieurs moniteurs exigent des essais dans l’environnement concerné. Aucun succès synthétique n’est présenté comme leur validation.


## Révisions, sauvegarde et fermeture

Changer de recherche ou de règles invalide les calculs issus de l’ancien contexte. Un formulaire ouvert sur un relevé remplacé demande de le rouvrir ; il ne modifie pas la nouvelle source avec des indices anciens. Les archives restituent leurs règles enregistrées. Un import conserve son interprétation locale même si un autre profil GLPI est choisi pour une future collecte.

Les brouillons de tickets sont conservés localement. En changeant d’écran ou en fermant la fenêtre, l’interface attend la fin de leur sauvegarde. Si cette sauvegarde échoue, la fenêtre reste ouverte avec l’erreur. **Annuler** arrête aussi une collecte programmée, puis laisse les écritures atomiques se terminer ou s’abandonner proprement.

## Infrastructure de développement

Le document [REPRISE.md de Domenia](https://github.com/M4D135/domenia-d120/blob/v2/v2/REPRISE.md) décrit une VM Windows 11 de laboratoire à 4 vCPU / 8 Go, avec SDK et cache NuGet locaux et session de bureau à conserver. GLPI Pilot dispose des paramètres de build correspondants ; les commandes exactes figurent dans [GitHub-et-compilation.txt](GitHub-et-compilation.txt). Le dépôt contient les scripts et tests à exécuter en fin de chantier dans cette VM. Un succès GitHub Actions Windows n’est pas présenté comme une exécution dans la VM privée Domenia.
