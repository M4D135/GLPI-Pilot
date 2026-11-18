# Collecter les contenus d’un périmètre de tickets

La liste GLPI chargée dans l’application sert d’inventaire. La collecte de bibliothèque utilise ses identifiants et ses colonnes pour sélectionner les fiches à lire. Un relevé de 180 000 lignes peut ainsi fournir une sélection de quelques dizaines ou centaines de tickets, sans ouvrir les autres fiches.

## Préparer la sélection

1. Charger un relevé actuel de la liste GLPI. Il peut déjà correspondre à une recherche filtrée dans GLPI. Sa date et son caractère complet ou partiel restent visibles ; la collecte de contenus ne découvre pas les tickets absents de cet inventaire.
2. Ouvrir **Données → Collecte** puis **Périmètre de collecte**.
3. Choisir la **Date utilisée** et renseigner **Historique à partir du**. La borne de début et la borne de fin éventuelle sont incluses. Les dates disponibles dépendent des colonnes reconnues : ouverture, modification, résolution, clôture ou échéance.
4. Ajouter les critères utiles : numéros de ticket, sujet, groupe attribué, demandeur, technicien, statut, catégorie ou toute autre colonne proposée. Une équipe configurée sélectionne ses groupes. Les valeurs d’un même filtre sont reliées par OU ; les filtres différents sont reliés par ET.
5. Cliquer sur **Prévisualiser le périmètre**, vérifier le volume et les états des contenus, puis sur **Mettre à jour**.

Par exemple, une borne au 1er janvier, l’équipe composée des groupes « Support N2 » et « Réseau », et le sujet « imprimante » retiennent les tickets de la période appartenant à l’un de ces groupes et dont le titre contient ce texte. Le nombre de tickets sélectionnés est connu avant toute lecture de leur contenu.

La recherche des valeurs de filtre parcourt les métadonnées du relevé local. L’affichage initial est limité aux valeurs les plus fréquentes ; la recherche retrouve aussi les valeurs rares. Une colonne absente ou ambiguë ne devient jamais un filtre ignoré. Avec un filtre de date, une date absente ou non interprétable est exclue et signalée dans l’aperçu.

**Sujet proche** est une comparaison de mots du titre. Elle ignore la casse, les accents et certains mots courants ; elle exige au moins 60 % des mots significatifs saisis, avec au moins deux mots communs lorsque plusieurs mots significatifs sont saisis. Un seul mot significatif exige ce mot. Il s’agit d’une proximité lexicale locale : les synonymes ou le sens des phrases ne sont pas analysés par un modèle IA.

## Lire le taux de collecte

Le taux est **contenus complets correspondant à la version connue ÷ tickets distincts sélectionnés**. Il ne porte ni sur les 180 000 tickets de l’installation ni sur le nombre de requêtes du passage.

| État | Signification |
| --- | --- |
| Complets disponibles | Contenu complet, confirmé, compatible avec le lecteur et le jeton de modification connu. |
| Sans contenu | Aucune fiche détaillée n’est encore enregistrée. |
| Modifiés | Une version différente est observée dans les métadonnées. |
| Partiels | Un contenu existe, mais certaines sections lisibles n’ont pas été obtenues. |
| Version à confirmer | Le contenu ne dispose pas d’une preuve suffisante pour être compté comme complet pour la version attendue. |

Les catégories sont disjointes. Les erreurs du passage restent dans le journal et dans **Fiches à reprendre**. Un ticket téléchargé n’est compté comme complet que lorsque les contrôles de version et de complétude le permettent. Une sélection vide affiche zéro ticket et aucun pourcentage de réussite.

L’aperçu compare d’abord les contenus à la version connue dans le relevé. Le passage lit ensuite les métadonnées des seuls tickets sélectionnés dans GLPI et recalcule la couverture. La date du relevé initial reste distincte de la dernière vérification dans GLPI. Un relevé partiel permet un taux pour sa sélection connue ; il ne permet pas de certifier la couverture de toute l’installation.

## Éviter les relectures inutiles

La sélection est dédupliquée par identité GLPI. Des lignes contradictoires portant le même identifiant sont exclues et signalées. La vérification distante utilise des recherches natives par identifiants, par lots bornés, sans parcourir les pages des autres tickets.

Chaque lot contient au maximum 25 références. Ses métadonnées sont vérifiées, les contenus nécessaires sont lus, puis leurs versions sont confirmées avant le lot suivant. Le point de reprise est enregistré après cette confirmation. Une sélection volumineuse peut ainsi avancer sur plusieurs budgets de temps sans recommencer la vérification de tous ses identifiants avant la première fiche.

Une fiche complète et confirmée est conservée si le jeton de modification fiable de GLPI est inchangé. Son ancienneté seule ne déclenche plus une nouvelle lecture dans le mode ciblé. Les versions nouvelles ou modifiées, les contenus partiels et les preuves insuffisantes restent à lire ou vérifier. Les marges de précision des dates et la compatibilité des lecteurs sont conservées ; les dates GLPI doivent être correctement interprétées avec le fuseau et l’ordre de date configurés.

La confirmation finale porte également sur la sélection. Un ticket devenu inaccessible est signalé ; son contenu déjà enregistré est conservé. Si le champ d’identifiant natif, le filtre réellement appliqué ou le périmètre de session ne peuvent pas être vérifiés, la collecte s’arrête avec une cause explicite. Elle ne bascule pas sur un parcours global.

## Pause, reprise et programmation

**Pause** et **Arrêter** conservent les contenus, le journal et la liste exacte des références sélectionnées. **Reprendre** utilise ce même périmètre, même après fermeture de l’application. Les contenus enregistrés avant l’interruption sont revalidés avant une éventuelle nouvelle lecture.

Les lots terminés ne sont pas reparcourus pendant la reprise du même passage. Le lot interrompu est revalidé, puis le travail continue sur les références restantes. Une nouvelle mise à jour commence un nouveau passage et vérifie de nouveau les versions de toute sa sélection. Les confirmations sont datées par ticket et par lot ; elles ne représentent pas une photographie simultanée de l’installation.

**Préparer un autre périmètre** ouvre un nouveau brouillon. La nouvelle sélection n’altère pas le passage suspendu. Toute modification des filtres ou du relevé rend l’ancien aperçu inutilisable pour un nouveau démarrage.

L’activation automatique nécessite un premier passage ciblé enregistré. Les passages suivants réutilisent sa sélection ; une reprise limitée aux erreurs ne remplace pas le périmètre de la programmation. L’application doit rester ouverte et les fenêtres horaires, le délai entre requêtes, les pauses de lots, le budget de temps et le verrou du poste collecteur restent appliqués. Une ancienne programmation globale doit être remplacée par un périmètre ciblé avant de redémarrer automatiquement.

Pour inclure des tickets arrivés depuis le relevé initial, actualiser le relevé puis prévisualiser et lancer le nouveau périmètre. La programmation ne peut pas deviner les références absentes de son inventaire enregistré.

## Mise à jour et données existantes

Les contenus déjà collectés restent dans la bibliothèque SQLite et dans les échanges configurés avec PostgreSQL ou le dossier commun. Les nouveaux plans de collecte sont des données de suivi locales. Ils ne modifient pas le protocole de partage des tickets ni le contrat des vues SQL du corpus.

Les fichiers joints ne sont pas téléchargés automatiquement. La collecte conserve leurs références disponibles, comme dans les versions précédentes.
