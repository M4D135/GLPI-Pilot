# Collecte reprenable et bibliotheque interne

## Acces et perimetre

Dans **Donnees > Collecte et reprise**, le menu conserve le theme et les animations existants. Il affiche une action principale adaptee a l'etat, puis des rubriques repliees pour le rythme, les transferts et les fiches a reprendre.

La collecte utilise la session autorisee et les entites accessibles du portail configure. Elle ne contourne aucune authentification, ne masque pas les acces et ne modifie aucun ticket GLPI. Les limites de pages et de tickets du profil existant restent appliquees : un index partiel est signale, jamais annonce comme une aspiration exhaustive. Les fichiers binaires joints ne sont pas telecharges automatiquement ; leurs references et metadonnees disponibles restent conservees.

## Pause, arret et reprise

Pause et Arreter interrompent les nouvelles lectures et conservent les transactions deja validees. Les controles ciblent l'identifiant de la collecte active, sans attendre le semaphore de l'operation longue. Le court enregistrement final du journal utilise son propre budget d'annulation. Une panne locale de stockage impose une verification ; aucune donnee non enregistree n'est declaree conservee.

Reprendre reutilise le meme journal, apres verification de l'installation, du contexte, du lecteur et du perimetre d'entites. L'index leger est relu : les corps complets encore valides ne sont pas retelecharges. Un corps durable qui attend seulement sa confirmation peut etre confirme par les nouvelles metadonnees, sans relire son contenu. Une version concurrente ou ambigue ne devient pas valide par cette operation.

Fermer le panneau pendant sa requete peut interrompre la collecte ; la progression reste reprenable. Ce lot ne promet pas une collecte independante de toute navigation. L'application reste necessaire a la programmation. Une pause, un arret, une interruption inattendue ou un etat a verifier bloquent les relances automatiques jusqu'a une action explicite. Un budget atteint peut etre repris lors du prochain passage autorise. Un arret du processus pendant la lecture est detecte a la reouverture.

Les cibles d'une reprise des erreurs ne sont pas elargies silencieusement a tous les tickets. Une fiche absente du nouvel index est conservee et signalee, pas supprimee.

## Rythme et programmation

La programmation est desactivee par defaut. Elle est propre a ce poste et a la source, distincte de la synchronisation du dossier partage ou PostgreSQL. Son activation demande confirmation du poste collecteur designe.

Reglages : intervalle entre passages, delai minimal entre navigations, taille des lots, pause entre lots, budget de temps, plage horaire et fuseau, limite d'erreurs consecutives. Le rythme est sequentiel. Il s'applique aux navigations controlees de liste, fiche, onglet et pagination de detail. Il ne limite pas chaque sous-requete HTTP (images, scripts ou AJAX) que le navigateur emet pour rendre une page. Les valeurs doivent etre adaptees aux regles et a la capacite du portail.

Un partage par dossier utilise un verrou distinct de la synchronisation ordinaire. PostgreSQL utilise une session non mutualisee et un verrou consultatif par installation. Un verrou indisponible ne provoque pas de collecte locale de secours. La coordination ne couvre que les postes configurant le meme espace partage.

## Mise a jour differentielle

Le premier passage enregistre les metadonnees et contenus accessibles. Les passages suivants relisent l'index puis recuperent les contenus nouveaux, modifies, incomplets, non confirmes ou rendus incompatibles par une modification du lecteur. Une date absente, ambigue ou non interpretable ne certifie pas un contenu inchange. Configurez le fuseau et l'ordre des dates de la source.

La comparaison conserve les regles de version et de conflit existantes. Aucun ancien contenu complet n'est remplace silencieusement par une lecture partielle. Les changements apparus pendant la collecte sont controles a nouveau lors de la verification finale.

## Bibliotheque de depart transferable

**Exporter la bibliotheque preparee** produit un ZIP interne .glpilibrary.zip avec deux entrees strictes : manifest.json et tickets.jsonl. Le manifeste identifie le format, l'installation, la date, le nombre de fiches, la taille et le SHA-256 du contenu. Les documents gardent leur provenance et leurs citations. Le fichier n'est pas une copie brute d'une base SQLite ouverte.

Le paquet est **non chiffre et peut contenir des suivis prives**. Il doit rester sur des supports internes autorises et etre distribue uniquement aux destinataires habilites. Ne jamais l'ajouter au depot, aux releases publiques ou au pack generique.

Aucun cookie, mot de passe de connexion, session SSO, identite technique de poste, curseur de synchronisation, programmation ou verrou de collecte n'est transfere. L'export porte sur les documents courants ; ce n'est pas une sauvegarde integrale des historiques et variantes conflictuelles. Les contenus non confirmes restent explicitement non confirmes.

L'import passe par un apercu et deux confirmations de perimetre et de fusion. Le paquet choisi est copie dans un staging local, sa taille et son contenu controles, puis lie a un jeton d'apercu limite a quinze minutes. L'application refuse un paquet change, une autre installation, des chemins supplementaires, une empreinte incoherente et des proprietes JSON dupliquees.

La fusion est transactionnelle : une corruption tardive ou une annulation annule toute cette transaction, sans supprimer les donnees precedentes. Les doublons identiques restent idempotents, les versions concurrentes sont conservees. Le poste receveur reconstruit son index de recherche et ne reprend ni la programmation ni les droits du poste exportateur. Les documents importes ne sont pas places automatiquement dans la file d'envoi du partage.

## Qualification

La suite Core verifie les regles sur de vrais fichiers SQLite. Les parcours navigateur utilisent le controleur et le lecteur de production sur un portail synthetique. Les tests de menu utilisent les modules JS/CSS de production et des reponses API de test ; leur passage sous CSP est distingue des essais locaux en memoire. Les tests complets des EXE, de PostgreSQL, de mise a jour et retour arriere restent des controles distincts avant une release.

Aucune qualification du GLPI/SSO prive, des permissions SMB ou des imprimantes de l'organisation n'est revendiquee. Ce guide decrit le code de la branche de developpement ; il ne vaut pas annonce de publication.
