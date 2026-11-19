# GLPI Pilot 4.0.13 — préférences natives des colonnes

## Blocage corrigé

La collecte pouvait réussir sa connexion SSO, reconnaître le tableau et identifier l’export natif, puis s’arrêter à l’ouverture des préférences avec le message : « L’adresse native des préférences ne peut pas être sauvegardée pour la restauration. Aucun ajout n’est envoyé. »

GLPI ajoute normalement `_in_modal=1` à l’adresse de sa fenêtre de préférences. Ce comportement est visible dans [`Ajax::createIframeModalWindow` du code officiel GLPI 9.5.13](https://github.com/glpi-project/glpi/blob/9.5.13/inc/ajax.class.php). La règle de sauvegarde de Pilot n’acceptait que la requête exacte `itemtype=Ticket`. Les fixtures de tests utilisaient cette adresse courte et ne reproduisaient donc pas le paramètre ajouté par la fenêtre native.

La ligne du journal signalant un cadre initial `about:blank` correspond à l’attente du chargement de la fenêtre. Elle ne prouve pas que ce cadre est la cause du refus. La correction conserve l’attente du vrai document GLPI et ses contrôles d’identité.

## Comportement de la correction

La sauvegarde accepte `itemtype=Ticket`, seul ou accompagné une seule fois de `_in_modal=1`, dans les deux ordres. L’adresse réellement observée reste enregistrée sans reconstruction et sert à la restauration après collecte ainsi qu’à la récupération après interruption. La relecture serveur des préférences modernes utilise cette même règle.

Les adresses étrangères, les identifiants intégrés à l’URL, les fragments, les autres types, les paramètres inconnus ou sensibles, les doublons et les valeurs modales différentes restent refusés. La correction ne change pas les commandes natives d’ajout ou de suppression ni les garanties de sauvegarde avant modification.

## Qualification et publication vérifiées

La [qualification Windows et PostgreSQL](https://github.com/M4D135/GLPI-Pilot/actions/runs/44544663495), tentative `1`, a réussi ses **16/16 jobs** sur le commit `b1f1890b2b1d80e97ebb4103adbbd8d4597312c1`. La [publication](https://github.com/M4D135/GLPI-Pilot/actions/runs/44552441910), tentative `1`, a également réussi : la [release v4.0.13](https://github.com/M4D135/GLPI-Pilot/releases/tag/v4.0.13) est disponible depuis le 19 novembre 2026 à 16 h 14 (Europe/Paris). Le ZIP original inspecté a été publié sans reconstruction, puis retéléchargé, comparé octet pour octet et vérifié de nouveau.

| Contrôle | Résultat observé |
| --- | --- |
| Journal et règles pures de restauration | 24/24 |
| Colonnes et récupération dans le navigateur | 72/72 |
| Automatisation générale | 53/53 |
| Attente et identité des exports natifs | 11/11 |

Les nouveaux cas purs vérifient les variantes natives autorisées, les refus et la conservation exacte de l’adresse dans le journal. Trois scénarios navigateur couvrent l’export puis le nettoyage d’une vue ancienne, la relecture serveur puis la restauration d’une vue moderne et la reprise d’un journal interrompu dans une nouvelle session. Les cas principaux de préparation ancienne et moderne utilisent également le paramètre natif. Les adresses courtes restent couvertes par les autres scénarios.

Les tests utilisent des pages synthétiques et ne se connectent pas au portail privé de l’organisation. Le [reçu de qualification et publication](QUALIFICATION-4.0.13.md) détaille les résultats, l’archive originale, son empreinte et les contrôles des fichiers publics.

## Mise à jour

Fermer Pilot, extraire tout le pack 4.0.13 publié dans un dossier local, puis remplacer les deux exécutables et le dossier `wad/` ensemble. Les données, les contenus collectés et les réglages utilisateur sont conservés. Relancer ensuite la collecte avec le choix habituel des colonnes. L’échec précis corrigé ici survient avant tout ajout : il ne nécessite pas une réinitialisation des préférences GLPI.
