# GLPI Pilot 3.10.0 — Diapos libres, corrections de sites et animations discrètes

## Préparer de vraies diapositives

**Pilotage → Présenter** ouvre maintenant un éditeur de pages au format **16:9**. Une première page regroupe quelques chiffres clés et analyses du tableau courant. Il est possible de créer, renommer, dupliquer, supprimer et réordonner les diapos ; la commande **Composer automatiquement** répartit les widgets visibles par rubrique sur plusieurs pages.

Les widgets s'ajoutent par clic ou glisser-déposer. Leur en-tête sert à les déplacer et leurs huit poignées à les redimensionner. La grille magnétique est facultative ; les champs de position et de taille permettent un réglage précis en pourcentage de la surface utile. Les flèches ajustent le placement au clavier. Les commandes de premier plan et d'arrière-plan règlent les superpositions, et **Annuler / Rétablir** permettent de reprendre une modification.

Plusieurs widgets peuvent occuper la même diapo. Un widget peut revenir sur plusieurs pages ; les pages ne sont plus imposées à raison d'un widget chacune. La composition est enregistrée avec le tableau, séparément de ses chiffres et filtres, et revient après redémarrage. Les tableaux natifs et personnalisés, les espaces personnels, l'export/import de configuration et la configuration par défaut conservent ces réglages. La démonstration reste isolée.

**Présenter** enregistre les modifications puis affiche les pages avec une navigation courte, au clavier ou par liste. La source et la date restent visibles ; les réserves et filtres détaillés se consultent dans un panneau repliable. Un widget supprimé ou masqué est signalé dans l'éditeur et doit être retiré de la diapo avant projection ou export.

Le **PDF 16:9** garde une page par diapo, avec les mêmes positions, tailles et widgets. La recherche, les filtres et les réserves restent imprimés ; un contexte trop long reçoit des pages de contexte après les diapos. Les tableaux y conservent leur aperçu visuel ; l'**HTML** ajoute les données détaillées après les pages, et les exports **CSV / Excel** reprennent les tableaux complets des widgets sélectionnés. Aucun autre widget n'est ajouté. Les valeurs, seuils météo et pourcentages proviennent du même calcul daté, y compris hors connexion. Un changement de source ou de composition rend les anciens aperçus caducs.

Voir le [guide de préparation des diapos](https://github.com/M4D135/GLPI-Pilot/blob/v3.10.0/docs/Presentation.md).

## Animations regroupées dans les réglages

Le choix des animations se trouve dans **Réglages → Confort et présentation**. Les boutons répétés sur l'Accueil, dans Pilotage et dans la présentation sont retirés. Les préférences existantes restent conservées.

Les anneaux décoratifs des widgets sont remplacés par un reflet discret sur leur bord et un léger relief au survol. Les effets ne recouvrent pas les nombres ni les commandes. Les scènes météo utiles — soleil, nuages, pluie — restent animées selon la préférence choisie. La réduction des mouvements, le contraste renforcé et la suspension des éléments hors écran restent respectés.

## Choisir le site d'un ticket ou d'une sélection

Dans **Interventions → Carte**, chaque ligne propose **Corriger le site**. Cette action est disponible directement dans les tableaux **Localisation ambiguë** et **Lieu non reconnu** : il n'est plus nécessaire de chercher de nouveau le ticket dans la file de révision.

Le choix du site utilise le référentiel enregistré et accepte les noms, alias et communes. La commune est déduite du site choisi. Les cases du tableau permettent d'appliquer le même site à plusieurs tickets ; le formulaire indique combien de tickets sont concernés avant l'enregistrement. Un motif est proposé et peut être précisé. Les corrections restent modifiables et peuvent être retirées.

L'enregistrement met à jour les localisations de la vue de travail, la carte et ses compteurs. La correction porte sur les tickets explicitement choisis ; elle ne crée pas d'alias général à partir de leur titre.

## Fonctionnement hors ligne et conservation du relevé

La correction est disponible sur une source courante et sur le dernier relevé restauré depuis le cache local, même lorsque le CSV/Excel original n'est plus présent. Les corrections sont mémorisées par source et identifiant unique de ticket. Un autre fichier ou une autre installation GLPI ne reçoit pas les corrections par erreur.

La vue de travail du cache peut afficher ces corrections tout en conservant les cellules importées et les fichiers du relevé capturé. Une correction explicite ne réinterprète pas tous les autres tickets avec les règles actuelles du référentiel. Les archives ouvertes comme observations historiques restent consultables avec leur interprétation d'origine.

Une correction peut être retirée pour retrouver la localisation automatique du relevé courant ou la localisation capturée du cache. Une ligne sans identifiant fiable, plusieurs occurrences du même identifiant, un site retiré ou une sélection devenue périmée produisent un refus explicite avant l'enregistrement du lot.

Les éditions Standard et Admin proposent le même parcours. Les corrections effectuées en démonstration restent dans la simulation.

## Actualiser et réimporter n'ont pas le même effet

**Actualiser**, dans la carte, recharge la vue depuis les données actuellement ouvertes. Dans le référentiel, ce bouton relit la liste des références. Il ne relance pas la détection automatique de tout un relevé conservé avec le nouveau dictionnaire.

Pour appliquer de nouveaux noms, alias ou rattachements à tous les tickets, enregistrer le référentiel puis réimporter le **CSV/Excel des tickets** dans **Données → Importer CSV / Excel**, ou lancer une nouvelle collecte GLPI. Importer le CSV des **sites** seul ne relit pas le fichier de tickets.

## Codes postaux et précision des localisations

Le champ CodePostal d’une commune accepte plusieurs codes séparés par des virgules. Le référentiel importé reste un fichier privé de l’organisation.

L’import conserve les identifiants existants. `Fusion=Completer` remplit les champs vides et préserve les valeurs personnalisées. Le code physique d’un site figure dans son adresse ; les codes INSEE et les destinations CEDEX restent distincts.

Le compteur **Localisation approximative** de la carte compte les tickets dont seule la commune est reconnue. Ils utilisent le repère de commune et peuvent être précisés par une correction de site. Cette catégorie est distincte de la précision des coordonnées d’un site.

## Livraison

La version 3.10.0 possède ses propres packs PROD et DEV, avec `GLPI-Pilot.exe`, `GLPI-Pilot-Admin.exe` et `wad/`. Les packs 3.9.0 publiés restent conservés sous leur numéro.

Les contrôles portent sur le moteur C#, la persistance et le placement des diapos, les exports PDF/HTML, les préférences d’animation, les corrections individuelles et groupées, la reprise du cache, les refus sans mutation partielle, l'isolation des sources, les parcours WebView Standard/Admin et la démonstration. Le workflow Windows du commit livré, les journaux contenus dans DEV et les empreintes des fichiers publiés constituent les preuves de qualification de cette version.
