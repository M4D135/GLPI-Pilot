# GLPI Pilot 4.0.5

## Export reçu après un délai de contrôle

Le choix natif du format peut envoyer l’export puis retirer ou remplacer son menu. La collecte attendait encore l’ancien contrôle avec le délai Playwright de dix secondes, ce qui pouvait interrompre l’import alors que GLPI finissait par télécharger le fichier. Un clic ayant lancé une réponse lente pouvait aussi dépasser son délai d’action avant l’arrivée du téléchargement.

La réception suit désormais la requête native CSV Toutes les pages dès son émission. Une commande déjà envoyée n’est pas répétée et le programme n’attend plus le menu disparu. Il attend le fichier dans le délai d’export configuré, puis sa réception complète et les contrôles habituels de contenu, de volume et de périmètre. Le délai global du navigateur reste inchangé.

Le téléchargement accepté doit correspondre à la destination et au mode CSV observés, à la même tentative et à l’onglet d’export ou à son popup. Les refus HTTP et les requêtes ambiguës restent des erreurs. Une requête envoyée ne constitue pas à elle seule une réception réussie.

## Annulation et diagnostic

Si la réception est interrompue après l’envoi d’une commande, le téléchargement éventuel est annulé et l’onglet de cette collecte ainsi que ses popups identifiés sont fermés. La prochaine collecte utilise un nouvel onglet, afin qu’un ancien téléchargement tardif ne puisse pas lui être attribué. Les autres onglets sont conservés.

La progression distingue l’export demandé, le fichier détecté et son chargement. Le journal indique l’étape de la commande et l’observation effective du téléchargement ; le diagnostic inclut le mode de préparation des colonnes et le délai de liste, sans contenu de tickets ni paramètres de recherche.

## Vérification et mise à jour

Les régressions Windows exécutent la collecte complète avec un menu retiré après soumission, des réponses au-delà de sept et dix secondes, un popup, un choix refusé et une annulation suivie d’une nouvelle tentative. La qualification complète et la vérification de l’archive originale précèdent la publication du même ZIP.

Fermer GLPI Pilot puis remplacer les exécutables et le dossier `wad/` par ceux du pack 4.0.5. Les données, réglages et choix de colonnes existants sont conservés.
