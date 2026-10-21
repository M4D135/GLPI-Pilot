# Lecture DOM des fiches ticket

`ticket-details.js` installe `window.__glpiPilotTicket`. Le script est réinstallable ; il ne clique pas, ne soumet aucun formulaire, ne fait aucune requête et ne lit ni cookies ni stockage d’authentification. L’hôte Playwright ne clique que sur un onglet de consultation validé par le script, puis le revalide juste avant le clic.

## Contrat

- `inspect({id, origin, selectors?, scopeSelector?})` renvoie `ready`, `authentication`, `busy`, `wrongId`, `identityConfirmed`, `title`, `sections[{name,selector,kind}]` et `reason`. Un panneau explicitement demandé absent, ambigu ou masqué ne revient jamais à une lecture globale de la page.
- `read({id, origin, selector, kind, maxRows})` renvoie `found`, `matches`, `reason`, `headers`, `rows`, `totalRows`, `warnings`. `kind` vaut `fields`, `table`, `text` ou `timeline`. Le sélecteur doit être unique dans le cadre ; l’hôte exige aussi son unicité entre cadres visibles autorisés.
- `tab({id, origin, ticketPath, selector})` renvoie `allowed`, `matches`, `reason`, `selector`, `targetSelector`, `alreadyActive`, `key` et `proof`. Les preuves admises sont `glpi-common-ticket-tab` et `local-ticket-tab-panel`.
- `tabs({id, origin, ticketPath})` découvre au maximum 60 onglets de consultation avec un panneau identifié. Les clés sont fondées sur le lecteur natif et `_glpi_tab`, ou le panneau local. La clé et le libellé doivent encore correspondre après tout rechargement ; un sélecteur CSS réutilisé n’est pas une identité d’onglet.

L’hôte confirme séparément l’origine, le chemin de fiche et l’identifiant exact de l’URL. Le seul champ caché consulté est `input[type=hidden][name=id]`, pour vérifier l’identité ; il n’est pas ajouté aux données. Les autres champs cachés, mots de passe, fichiers, valeurs de jetons, colonnes sensibles et groupes portant un libellé sensible sont exclus. La méthode `getAttribute('id')` évite le masquage de `HTMLFormElement.id` par le champ GLPI nommé `id`.

## Structures reconnues

Les formulaires GLPI 9 utilisent notamment `#tabsbody`, les tableaux `#mainformtable*` et des cellules de libellé `th` suivies de cellules de valeur `td`. Les formulaires GLPI 10 utilisent `#itil-form`, `.form-field > label` et `.field-container`. Les contrôles visibles sont lus sans les modifier : valeur courante des champs texte, état des cases, libellé du radio choisi, libellés des seules options sélectionnées. Les widgets Select2 reconnus permettent de lire la sélection du contrôle natif masqué associé.

Une description TinyMCE est lue dans le corps de son éditeur visible et associé au textarea exact, via `getElement`/`getContainer`/`getBody` ou l’iframe reconnue `<id>_ifr` contenue dans le widget. Aucune commande de sauvegarde, focus ou synchronisation de l’éditeur n’est appelée. L’accès DOM du navigateur conserve la restriction de même origine ; un iframe `about:blank` de l’éditeur n’est donc lisible que lorsqu’il hérite de cette origine. Les autres champs masqués restent exclus. Les zones de dépôt et de sélection de fichiers sont retirées des champs de la fiche.

Le traitement legacy GLPI est lu dans `.timeline_history`, avec ses `.h_item`, `.h_date`, `.h_user_name`, `.h_content` et `.rich_text_container`. Les colonnes sont Type, Date, Auteur, Contenu et Informations. Les suivis, tâches, solutions, documents, validations et description initiale restent distincts. Les états de tâche, confidentialité et informations complémentaires visibles sont lus sans action. Les commandes d’ajout, de modification, de validation, de changement d’état et de téléchargement ne sont pas déclenchées. Les liens de document ne produisent que leur texte. Les événements masqués par le portail et les limites de lignes sont signalés.

Les tableaux de données visibles sont distingués des tableaux de formulaire, des paginations et des barres de navigation. Le contenu d’un tableau imbriqué n’est pas ajouté une deuxième fois au tableau parent. Les pieds de tableau sont exclus. Les cellules fusionnées sont développées et les limites de lignes sont signalées.

Les onglets GLPI doivent appartenir à une barre d’onglets reconnue, porter un rôle d’onglet, et pointer vers le lecteur existant de la même installation `ajax/common.tabs.php` avec `_itemtype=Ticket` et le même `id`. Les paramètres sont limités à ceux de la consultation GLPI ; aucune URL n’est fabriquée. Une cible secondaire `_target` doit elle aussi désigner la même installation et ne peut porter d’identifiants dupliqués. Un onglet de panneau local doit cibler un panneau `tabpanel` ou une classe de panneau standard. Les contrôles de soumission, actions métier, destinations étrangères, autres identifiants, attributs de formulaire et gestionnaires inline sont refusés. Le lecteur exige que l’onglet soit activé, son panneau visible et les indicateurs de chargement reconnus terminés, dont `aria-busy`.

## Périmètre

La lecture initiale inclut automatiquement le traitement lorsque cette préférence est activée et que l’onglet existe. Les autres onglets découverts sont proposés dans le menu et lus à la demande dans la même page dédiée. La lecture ne prétend pas parcourir toutes les pages de sous-tableaux. `ExtractionResult.Complete` décrit uniquement les lignes de la section DOM effectivement visible, dans la limite configurée. Le résultat global rappelle explicitement que les onglets et contenus masqués non lus ne sont pas déclarés complets. Les sections configurées absentes, ambiguës, masquées ou refusées produisent un avertissement.

Les cadres de contenu visibles de la même origine sont pris en compte. Les cadres étrangers, masqués ou sans origine HTTP(S) vérifiable ne sont pas parcourus comme des fiches. La seule exception d’accès au contenu d’un cadre sans URL HTTP(S) concerne l’éditeur visible associé décrit ci-dessus. Une section rendue uniquement dans un canvas ou un composant personnalisé sans structure DOM reconnue peut nécessiter un sélecteur de lecture différent ; le lecteur ne déduit pas les données manquantes.

## Sources primaires consultées

- GLPI 9.5.13, formulaire ticket et identité : https://github.com/glpi-project/glpi/blob/9.5.13/inc/ticket.class.php
- GLPI 9.5.13, structure de traitement `showTimeline` : https://github.com/glpi-project/glpi/blob/9.5.13/inc/commonitilobject.class.php
- GLPI 9.5.13, `showFormHeader` / `#tabsbody` : https://github.com/glpi-project/glpi/blob/9.5.13/inc/commondbtm.class.php
- GLPI 9.5.13, construction des onglets : https://github.com/glpi-project/glpi/blob/9.5.13/inc/commonglpi.class.php et https://github.com/glpi-project/glpi/blob/9.5.13/inc/ajax.class.php
- GLPI 9.5.13, lecteur d’onglet et contrôle des droits de lecture : https://github.com/glpi-project/glpi/blob/9.5.13/ajax/common.tabs.php
- GLPI 10, formulaire `#itil-form` : https://github.com/glpi-project/glpi/blob/10.0/bugfixes/templates/components/itilobject/mainform_open.html.twig
- GLPI 10, structure des champs : https://github.com/glpi-project/glpi/blob/10.0/bugfixes/templates/components/form/fields_macros.html.twig
- GLPI 10, layout et formulaires d’action séparés : https://github.com/glpi-project/glpi/blob/10.0/bugfixes/templates/components/itilobject/layout.html.twig
- GLPI 10, onglets Bootstrap et chargement du lien existant : https://github.com/glpi-project/glpi/blob/10.0/bugfixes/src/Ajax.php
- TinyMCE, lecture du corps et identification de l’éditeur : https://www.tiny.cloud/docs/tinymce/latest/apis/tinymce.editor/

Ces structures ont été consultées le 21 octobre 2026. Les fixtures reproduisent leurs conventions HTML avec des données synthétiques ; elles ne constituent pas une capture d’une instance utilisateur.
