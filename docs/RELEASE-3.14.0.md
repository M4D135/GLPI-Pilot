# GLPI Pilot 3.14.0 · Windows x64

Seul le pack **PROD-LIGHT** est publié. Il contient les exécutables Standard et Admin et le dossier `wad/`, sans runtime WebView2. Le runtime WebView2 fixe, le même pour Pesto, FILPRO, Domenia et GLPI Pilot, est publié une seule fois dans le dépôt [Socle Windows](https://github.com/M4D135/socle-windows/releases) : les releases de chaque outil ne transportent plus 300 Mo de runtime.

Sur un poste où WebView2 Evergreen est installé, rien ne change. Sinon, GLPI Pilot le signale au démarrage dans une fenêtre Windows et propose de choisir le ZIP `Socle_WebView2-<version>_Windows-x64.zip`. Le ZIP n’est accepté que si son empreinte est l’une de celles connues de l’application (156.0.4314.42 : `42dc4b13…71b2`). Il est installé comme composant, hors du dossier de l’application : les mises à jour le conservent. Le même ZIP peut aussi être installé depuis Réglages · Composants.

Un poste installé avec un pack complet (PROD, avant 3.14.0) se met à jour vers le pack allégé dès qu’Evergreen ou le runtime du Socle est présent. Sinon, l’assistant de mise à jour demande d’installer d’abord le runtime du Socle dans Réglages · Composants. Le partage réseau de mises à jour propose la version allégée la plus récente.

La qualification Windows construit toujours le pack complet pour ses contrôles internes, mais seul le ZIP PROD-LIGHT est scellé, joint au run qualifié et publié.

Avant de mettre à jour, sauvegarder les bases et les réglages puis aligner les versions des postes partageant le même espace. Les exécutables ne sont pas signés. Comparer l’empreinte du ZIP téléchargé à `SHA256SUMS.txt` avant extraction.
