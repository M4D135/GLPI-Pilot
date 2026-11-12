# GLPI Pilot 3.14.5 · Windows x64

**Réglages** reprend le menu de configuration de Domenia. L’écran ne déroule plus toutes ses cartes à la suite : un rail présente dix sections à gauche et une seule s’affiche à la droite du rail. Les sections sont **Apparence**, **Pilotage**, **Collecte**, **Tickets**, **Historique**, **Équipe et partage**, **Configuration à diffuser**, **Mises à jour et journal**, **Démonstration** et **À propos et aide**. Chaque section ouvre sur son titre et une phrase qui dit ce qu’elle règle. Un repère glisse sous l’entrée choisie ; les flèches, <kbd>Début</kbd> et <kbd>Fin</kbd> parcourent le rail.

En tête de l’écran, quatre tuiles résument l’état de l’installation : l’apparence (thème, animations et zoom), le relevé ouvert, l’historique local et la configuration du pack. Un clic sur une tuile ouvre la section correspondante. **Rechercher un réglage** (touche <kbd>/</kbd>) trouve un réglage par ses mots, sans tenir compte des accents. Choisir un résultat ouvre sa section, met le réglage en évidence et lui donne le focus.

Les réglages de collecte et d’export, les repères de l’installation GLPI, la lecture des tickets, l’historique local et la configuration d’équipe s’affichent directement dans leur section ; ils ne s’ouvrent plus dans un panneau. Les seuils et la météo, les mises à jour, le journal, les collectes programmées et l’aide gardent leur panneau. Les liens qui menaient à ces panneaux ouvrent d’abord la bonne section.

Changer de section ne perd rien : une valeur saisie et non appliquée reste en place, et le changement d’identité la signale toujours avant de l’abandonner. **Actualiser** reconstruit l’écran sur la section affichée, alors qu’une nouvelle visite ouvre **Apparence**. Enregistrer une carte de réglages ne reconstruit que cette carte et celles qui n’ont pas de saisie en cours.

Les documents Word/PDF de `DOC/` restent les instantanés 3.13.6.

Seul le pack **PROD-LIGHT** est destiné à la publication, sans runtime WebView2 embarqué. La qualification Windows du commit exact et l’inspection du ZIP original précèdent sa publication.
