# GLPI Pilot 3.13.6 · Windows PROD et PROD-LIGHT

GLPI Pilot ajoute un pack allégé sans runtime WebView2 embarqué et consolide sa chaîne de qualification.

## Pack PROD-LIGHT

Le pack allégé, `GLPI-Pilot_3.13.6_PROD-LIGHT_Windows-x64.zip`, est identique au pack PROD sans le dossier `wad/runtime/` : environ 300 Mo de moins à télécharger. Il démarre sur le runtime WebView2 Evergreen déjà installé sur Windows (Windows 11, postes avec Edge récent) et, si ce runtime manque, l'indique au démarrage au lieu d'échouer silencieusement. Son `wad/version.json` déclare `webview2_loader = evergreen-installed`.

Un poste installé avec un pack complet se met à jour avec des packs complets, un poste allégé avec des packs allégés : l'assistant de mise à jour refuse de mélanger les deux et propose par défaut l'édition du poste, y compris depuis un dossier de diffusion réseau. Le pack PROD conserve son contenu et sa disposition.

## Correction

- **Import de configuration.** Depuis la 3.12.0, l'import d'une configuration complète exportée par l'application refusait toute configuration produite par une version 3.12 ou 3.13 (« Cette configuration provient d'une version de l'application non prise en charge »). Les versions 3.x jusqu'à la version installée sont désormais acceptées ; les versions ultérieures restent refusées. Le défaut était masqué par les bancs de test, qui exportaient sous une version figée « 3.0.0 ».

## Qualification

- Les captures et figures des guides affichent la version réelle de l'application : les bancs de test suivent désormais la version du projet au lieu d'un numéro figé « 3.0.0 ».
- Les figures des guides sont recadrées depuis le coin haut gauche par `guides/crop_figures.js` d'après `guides/figures.json` ; le fabricant des guides refuse une figure aux dimensions inattendues. Le workflow des captures claires joint les figures prêtes.
- Deux jeux d'essai qui comparaient une date figée à l'horloge réelle (calendrier : nuit de changement d'heure ; historiques : rétention des relevés) sont datés par rapport au jour du test.
- La publication se lie au commit qualifié et non à la branche : une branche de version qualifiée puis poussée en avance rapide sur main ne demande plus une seconde qualification de 90 minutes. Le run déclenché par ce push constate que le commit est déjà qualifié et passe ses jobs ; en cas d'API indisponible, la qualification complète est lancée normalement.
- La qualification vérifie les trois archives et démarre le pack allégé sur le runtime Evergreen du runner lorsqu'il est installé ; sinon le journal `windows-startup-checks-light.log` le dit explicitement.

## Qualification et distribution

La qualification initiale a inspecté les trois ZIP. Depuis le 2 novembre 2026, les téléchargements Windows de cette release sont PROD et PROD-LIGHT. `SHA256SUMS.txt` contient leurs deux empreintes. Les sources et les tests sont accessibles dans [le ZIP du tag v3.13.6](https://github.com/M4D135/GLPI-Pilot/archive/refs/tags/v3.13.6.zip). Voir `docs/GitHub-et-compilation.txt` pour la publication en avance rapide.

## Avant la mise à jour et limites

- Le pack PROD-LIGHT exige le runtime WebView2 Evergreen de Microsoft sur le poste ; en son absence, utiliser le pack complet.
- Les EXE ne sont pas signés. Sauvegarder les bases locales et les réglages avant la mise à jour.
- La programmation nécessite l'application ouverte. Fermer le panneau ou naviguer pendant sa requête peut interrompre la collecte ; les transactions validées et la reprise restent conservées.
- Le transfert `.glpilibrary.zip` n'est pas chiffré et peut contenir des suivis privés : canal interne protégé et destinataires habilités.
- Aucun portail GLPI/SSO privé, droit SMB ou périphérique d'impression de l'organisation n'a été testé. Les contrôles CI utilisent des profils isolés, des données fictives et des services de test.
