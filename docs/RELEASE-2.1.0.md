# GLPI Pilot 2.1.0 — poursuite complète des chantiers fonctionnels

Version Windows x64 autonome. Les packs PROD et DEV sont publiés uniquement après la réussite de tous les contrôles Windows du commit correspondant.

## Télécharger et démarrer

Le pack **PROD** contient `GLPI-Pilot.exe` et `wad/`. Le pack **DEV** contient le même exécutable, les mêmes ressources, puis les sources, tests, scripts et guides dans `wad/sources/`. Extraire tout le ZIP et conserver `wad` à côté de l’EXE. Edge ou Chrome doit être disponible ; aucune installation de Python, Node.js ou .NET n’est nécessaire pour utiliser le logiciel.

Les empreintes des deux ZIP sont publiées dans `SHA256SUMS.txt`. Chaque pack comporte son inventaire et `wad/version.json`, qui identifie le commit testé. Une version publiée pour un autre commit n’est jamais remplacée silencieusement.

## Collectes, sources et données

Les profils conservent maintenant leurs repères, limites, délais, dossier local et conventions d’interprétation. Les sessions navigateur sont séparées par navigateur et installation GLPI. Les campagnes continuent après une erreur propre à une source et produisent un bilan JSON/CSV pour toutes les sources, y compris celles non lancées après annulation.

Les étapes SSO sont chronométrées. Les délais de listes et de fiches sont distincts du SSO et du téléchargement. Le prévol contrôle l’espace libre et l’écriture. La pagination conserve des points de reprise bornés et atomiques ; première page, dernière page, colonnes, total et périmètre sont recontrôlés avant réutilisation. Une reprise incompatible redémarre depuis le début. Une coupure ou expiration de session donne lieu à des tentatives bornées, sans saisir de mot de passe ni mélanger des recherches.

Le contrôle d’échantillon rapproche les identifiants et les valeurs comparables, sans les inscrire au journal. L’assistant de correspondance présente les rôles, formats, fuseau, séparateurs et dictionnaires, puis les cellules brutes et interprétées avant application. Les changements de structure et renommages possibles sont signalés. Les imports locaux et archives gardent leur nature et ne deviennent pas des collectes GLPI certifiées.

## Pilotage et analyses

Les sections et widgets se déplacent et s’éditent par groupes ; favoris et usages récents sont conservés. Barres empilées volume/pourcentage, seuil de Pareto, satisfaction avec couverture, partition d’échéances et météo de charge complètent les mesures existantes. Une carte de coordonnées fonctionne sans service de tuiles et agit sur les autres widgets.

L’historique représente les lacunes de collecte et propose mouvements, transferts, cohortes J+1/J+7/J+30, durées par statut avec bornes d’incertitude, projections conditionnelles, anomalies et décomposition des variations. Un référentiel daté d’effectifs ou postes par site permet des taux pour cent unités, avec provenance et validité. Les relevés incompatibles ne sont pas raccordés artificiellement.

Les exports PNG/PDF réunissent date, filtres et qualification des données. Le rapport HTML autonome assemble critères, graphiques, qualité et toutes les lignes du périmètre retenu. L’atelier dispose de groupes repliables avec sous-totaux recalculés à partir des contributeurs et de packs de requêtes JSON importables avec aperçu des conflits.

## Tickets, techniciens et terrain

Les observations de tâches, attentes, validations, échanges, relations et documents sont structurées lorsqu’elles sont réellement présentes dans les pages lues. Les inconnues sont explicites. Les nouveautés sont mises en évidence dans les rubriques et la lecture reprend par ticket. Ma journée réunit les tournées et un briefing des changements observés depuis la session précédente.

Une file d’enrichissement ciblé contrôle priorité, nombre maximal de tickets, annulation, dates et résultats. Elle peut relire des tickets absents d’une comparaison ; leur état actuel reste séparé de l’archive. Les calendriers de techniciens comprennent présences récurrentes, demi-journées, exceptions, capacités et estimations par catégorie avec fourchettes. Les scénarios restent des propositions explicables.

Les affectations, suivis et tâches GLPI disposent d’un parcours assisté : observation du formulaire autorisé, préparation visible, confirmation unique et relecture. Un résultat ambigu ne provoque aucune seconde soumission automatique. Les brouillons locaux restent distincts des actions publiées.

Le référentiel géographique comprend bâtiments, hiérarchie, règles de position et de format, ordre des sources et motifs de correction. Les changements sont simulés sur tout le relevé avant application. Les tournées ont un cycle brouillon/prête/effectuée, des horaires de site et des propositions sur plusieurs jours/techniciens tenant compte des contraintes déclarées. Contacts et matériel complètent la fiche terrain.

Les adaptateurs OSRM et Open-Meteo sont facultatifs. Le service doit être configuré et l’envoi des coordonnées autorisé ; les réponses gardent leur source, leur date et leurs limites. Les propositions de répartition ne garantissent pas un optimum mathématique ni l’état réel du trafic.

## Automatisation, assistance et maintenance

Horaires fixes multiples, jours actifs, recettes ordonnées et conditionnelles, reprise des lancements dus après retour de veille/réseau et politique commune de tranquillité. Les alertes suivent aussi les variations comparables, nouveaux contributeurs, raisons de résolution et santé des collectes.

Les imports de configuration présentent les différences complètes et permettent de conserver l’actuel, l’importé ou deux variantes, avec réécriture des références concernées. Les préférences personnelles restent locales. L’assistant de première utilisation, F11, navigation personnalisable, palettes adaptées, positionnement par moniteur et mémoire des tableaux complètent l’interface.

L’assistant de langage naturel propose un moteur local à règles et un client compatible configuré par l’utilisateur. Préparation, génération, aperçu et ajout au brouillon sont des actions distinctes. Les réponses sont bornées, les références vérifiées et aucune requête arbitraire n’est exécutée. La génération externe exige un consentement sur les données préparées ; la clé est protégée localement par Windows.

La mise à jour officielle vérifie release, tag, commit, archive et inventaire, puis prépare une sauvegarde complète. L’auxiliaire attend l’arrêt de l’application et exige un accusé de démarrage du nouveau programme ; les échecs déclenchent un retour aux anciens fichiers. Les processus ou fichiers verrouillés interdisent un remplacement silencieux. Le retour manuel vers une sauvegarde est aussi contrôlé.

## Validation et prérequis extérieurs

La publication exige les suites métier, les pages GLPI synthétiques dans Chromium, les contrôles WPF sur Windows, le démarrage et le zoom du pack, puis les remplacements et restaurations réels sur des copies du pack. Les journaux et captures du workflow constituent les preuves d’exécution ; une compilation croisée n’est pas présentée comme un essai Windows.

Les 200 propositions sont détaillées dans `wad/docs/Suivi-200-ameliorations.md`. Deux prérequis ne peuvent pas être attestés ici : la validation du portail/SSO et du matériel d’entreprise, et la fourniture d’un certificat de signature d’éditeur. La chaîne Authenticode est implémentée ; cette livraison ne prétend pas fournir un EXE signé en l’absence de ce certificat. Aucun portail privé, aucun compte d’entreprise et aucun fournisseur externe n’a été utilisé par les tests synthétiques.
