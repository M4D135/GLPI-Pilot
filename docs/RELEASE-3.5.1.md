# GLPI Pilot 3.5.1 — Identité simple et animations Domenia

Cette correction rend le choix de son identité accessible depuis chaque écran et renforce le mouvement de l’interface à partir de la référence Domenia v2.

## Un nom, puis « C’est moi »

Le bouton **Choisir mon identité** du bandeau supérieur et la carte de l’**Accueil** ouvrent **Mon identité**. Rechercher son nom ou son identifiant, puis cliquer **C’est moi** sur la bonne personne. Les propositions viennent des tickets chargés et des participants déjà connus du planning ; aucune recherche dans un annuaire n’est lancée.

Si le nom manque, **Mon nom n’est pas dans la liste** ouvre le champ **Nom tel qu’il apparaît dans les tickets**. Saisir le nom ou l’identifiant utilisé dans les affectations, puis cliquer **Enregistrer mon identité**. Ce parcours reste disponible avant toute collecte.

Le choix est mémorisé dans le profil local et le bouton affiche le nom choisi. **Retirer mon identité** efface seulement cette sélection personnelle. Changer ou retirer son choix conserve les interventions et les participants. En simulation, la sélection reste isolée du profil habituel.

## Rester dans son travail

Choisir son identité conserve l’écran courant et la date du planning. **Mes tickets** ouvre le choix lorsqu’il manque, puis active le filtre attendu. **Mes interventions** utilise la même identité. Le bouton supérieur permet de la changer sans retourner dans un menu de configuration.

Dans **Réglages**, les valeurs modifiées mais non appliquées demandent une décision avant le changement d’identité. Annuler conserve la saisie ; confirmer permet de l’abandonner. Après **Appliquer**, aucun avertissement de brouillon n’est ajouté pour ces valeurs enregistrées.

Une identité déjà connue conserve sa clé exacte. La saisie simple réutilise une correspondance unique malgré les différences de casse ou d’espaces ; plusieurs correspondances demandent un choix explicite. Les anciennes identités manuelles et leurs interventions restent conservées, sans fusion implicite.

Le choix d’une identité sert aux filtres personnels. Il ne crée pas d’authentification, de mot de passe ou de rôle et ne reproduit pas la connexion GLPI ou Windows. Les participants du planning restent des données communes lorsque le partage est activé ; la sélection « c’est moi » reste personnelle.

## Un mouvement visible et facile à activer

Le bouton **Animations actives** ou **Animations en pause**, sur l’**Accueil**, ouvre **Animations Domenia** avec un aperçu en direct. **Activer les animations Domenia** applique immédiatement le mode **Expressives** et le mémorise. Les choix **Suivre Windows** et **Réduire les animations** restent accessibles. L’état affiché explique notamment une pause demandée par Windows ou liée au contraste renforcé.

Les orbes, l’anneau tournant et le titre animé sont raccordés au nouvel en-tête de l’Accueil. Les cartes apparaissent en cascade avec une translation et un léger changement d’échelle ; les reflets, la parallaxe et le halo réagissent au pointeur. Les courbes d’historique et de Pareto se dessinent une fois à leur arrivée. Un recalcul qui remplace le contenu d’une carte ne supprime plus définitivement ses décors.

Les nombres et les données restent exacts. Les formulaires, tableaux, cartes géographiques et contrôles de déplacement gardent leur géométrie. Une case à cocher répond dès le premier clic, même pendant l’apparition de sa carte. Les effets hors écran, les modes réduits, les contrastes renforcés et l’édition continuent de suspendre les animations concernées. Les préférences existantes sont conservées ; **Expressives** reste un choix explicite. L’aperçu de **Réglages → Confort et présentation** montre aussi le mode réellement appliqué.

## Installation et validation

Les packs **PROD** et **DEV** conservent `GLPI-Pilot.exe` et `wad` à la racine. La mise à jour conserve les profils existants. Le calendrier, le partage facultatif, ses intervalles réglables et les animations de 3.5.0 restent disponibles.

Le [rapport de vérification](Rapport-verification.txt) distingue les contrôles locaux du patch et la validation native Windows. La publication exige la réussite du workflow du commit livré, avec démarrage du pack, mise à jour, retour arrière, intégrité des archives et reconstruction DEV.
