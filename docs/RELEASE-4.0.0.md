# GLPI Pilot 4.0.0 — HyperMotion (prépublication)

Le numéro 4.0.0 désigne la nouvelle interface intégrée au code. Il ne constitue pas une attestation de diffusion PROD. La validation de la dernière révision, puis le contrôle du SSO dans l'environnement métier, précèdent toute publication.

## Interface

- Direction artistique inspirée de Domenia v2 : surfaces marine, cyan, violet et menthe, contrastes soignés, cartes et éléments actifs harmonisés.
- Accueil « Votre journée en un regard » : action prioritaire qui s'adapte à la présence ou non d'un relevé, accès sans détour aux tickets, au planning, à la carte et au pilotage.
- Conservation de la grille et du dimensionnement des widgets, des filtres métier, du cache et des données réellement observées. Aucun chiffre d'exemple n'est injecté dans l'interface de production.
- Écrans 1280 × 800, 1366 × 768 et largeur étroite ; l'identité passe en deux lignes sur les petits écrans sans comprimer le texte.
- Modes clair/sombre, fort contraste, réduction des animations et affichage forcé des couleurs conservés.

## Tickets

- Une fiche ouverte montre immédiatement l'observation locale, puis attend 500 ms avant de lancer une lecture GLPI autorisée.
- Pas de lecture à la simple traversée ou au survol d'une ligne. La sélection suivante annule la demande précédente ; aucune observation obsolète ne remplace la fiche ouverte.
- Les observations fraîches sont réutilisées pendant 30 secondes ; le bouton « Actualiser la fiche » passe outre ce délai.
- La lecture automatique est suspendue pendant l'ouverture d'un outil de rédaction. Elle ne se déclenche pas dans une simulation, hors ligne ou sans accès distant.
- Le liseré de lecture K2000 est discret, explicite l'état et respecte les préférences de mouvement réduit.

## Qualification

- Huit tests unitaires ciblés du lecteur différé, couvrant délai, navigation rapide, annulation, fraîcheur, mode hors ligne, pause des outils, actualisation manuelle et erreur réseau.
- Tests de l'interface réelle avec réponses synthétiques (pas de connexion SSO), génération de captures desktop/mobile en thème clair et sombre.
- Windows Standard et Admin, démarrage natif, SQLite, PostgreSQL 17, mise à jour et retour arrière sont vérifiés par les workflows de qualification. Leur réussite pour le commit final et l'exécution du parcours métier réel sont **obligatoires avant diffusion**.

Le seul pack diffusé officiellement reste **PROD-LIGHT** (EXE Standard + Admin + `wad/`, runtime WebView2 Evergreen ou Socle). Les artefacts de qualification GitHub ne constituent pas une release et expirent.
