# GLPI Pilot 3.14.4 · Windows x64

Dans **Interventions → Carte**, la carte des tickets s’ouvre directement dès qu’un relevé est chargé : chaque repère porte son nombre de tickets. Auparavant, l’onglet ouvrait la carte des interventions prévues, où les sites du référentiel apparaissent en simples pastilles « • » ; il fallait cliquer sur **Explorer les tickets sur la carte**, ou passer par **Réglages → Sites et communes**, pour voir les tickets. Sans relevé, l’onglet montre toujours les interventions prévues. **Carte des interventions prévues** et **Explorer les tickets sur la carte** passent d’une carte à l’autre ; seul ce choix explicite est mémorisé.

Le fond de rues OpenStreetMap se charge seul à l’ouverture dans tous les cas. Sur un écran large, une vue très dézoomée demandait des tuiles situées hors de la grille mondiale ; l’application les refusait et retirait tout le fond de rues, qu’il fallait remettre avec **Afficher les rues**. Ces tuiles ne sont plus demandées. Une tuile manquante laisse désormais le fond en place. Si aucune tuile n’arrive, la carte réessaie une fois avant de revenir au fond local, et **Afficher les rues** reste le fond préféré. Une vue mémorisée alors que la carte n’avait rien à placer n’est plus restaurée : la carte se cadre sur les lieux.

Une configuration complète appliquée depuis **Réglages** installe aussi son référentiel de sites sur un relevé importé d’un fichier ou lu depuis la base locale, même lorsqu’elle change en même temps la source active. Il n’est plus nécessaire d’enregistrer de nouveau le référentiel. Ce relevé garde sa correspondance de colonnes. Un relevé conservé du cache local ou une archive gardent, comme avant, le référentiel de leur capture.

Les documents Word/PDF de `DOC/` restent les instantanés 3.13.6.

Seul le pack **PROD-LIGHT** est destiné à la publication, sans runtime WebView2 embarqué. La qualification Windows du commit exact et l’inspection du ZIP original précèdent sa publication.
