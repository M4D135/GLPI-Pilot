# GLPI Pilot 4.0.17 — collecte vérifiée avant import de connaissances

Cette version corrige un faux bilan de réussite : certains contenus conservés mais non confirmés pouvaient compter zéro fiche à reprendre. Un passage arrivé à son terme pouvait alors avancer la date de dernière réussite alors que son contenu restait exclu du contrat SQL consommé par Relais.

## Ce qui change

- **Avant de lire les fiches**, le précontrôle examine les dates de modification du périmètre. Il bloque la lecture massive si aucune date n’est interprétable et explique où régler le format et le fuseau. Il ne déduit pas automatiquement le fuseau. Une date ISO avec décalage explicite n’exige pas de fuseau supplémentaire. Les cas mixtes restent possibles avec un avertissement chiffré.
- **À la fin du passage**, un bilan validé exige toutes les cibles traitées, un périmètre final vérifié et zéro lecture échouée, partielle, conflictuelle ou non confirmée. La bibliothèque actuelle est également vérifiée en lecture seule contre les cinq critères du contrat SQL de connaissances. Un passage incomplet reste « à vérifier » et n’avance pas la dernière réussite ; la programmation ne le relance pas en boucle.
- **Pour la reprise**, les copies réutilisées en attente de confirmation restent dans les cibles à reprendre. La finalisation répare les marqueurs de reprise d’un ancien journal incohérent sans modifier les corps, les dates ou les preuves. Les motifs historiques restent visibles même si l’ancien compteur de reprises valait zéro.
- **Pour les conflits**, une version dont un conflit subsiste ne peut plus être sautée comme si tout était résolu. Les versions divergentes restent conservées et nécessitent leur examen explicite.
- **Dans l’interface**, les compteurs de présence, de qualité et de reprise sont distincts ; les causes s’ouvrent lorsque le bilan demande une attention. Les réglages de format et fuseau sont directement accessibles. Le cache hors connexion garde son audit local sans navigation GLPI.

Les cinq critères restent `detail_complete=1`, `has_content=1`, `content_confirmed=1`, `confirmation_pending=0` et `conflict_count=0`. Un contenu partiel peut être consultable dans Pilot sans devenir une connaissance admissible dans Relais. Un fragment vide reste exclu par le consommateur. Le bilan de Pilot ne remplace pas les vérifications d’intégrité, de taille et de droits de l’import Relais.

## Mise à jour sans perdre les contenus

Fermer les deux lanceurs et conserver une copie du dossier `%LOCALAPPDATA%\GLPI-Pilot` ainsi que du dossier de bibliothèque si un autre emplacement est configuré. Copier les fichiers SQLite auxiliaires éventuels avec la base pendant que Pilot est fermé. Les réglages sauvegardés ne remplacent pas une copie des données.

Extraire le pack publié dans un nouveau dossier d’application en gardant `GLPI-Pilot.exe`, `GLPI-Pilot-Admin.exe` et `wad/` ensemble, puis ouvrir avec le même compte Windows. Le chemin de bibliothèque et les contenus existants sont réutilisés. La mise à jour ne réinitialise aucune base et n’accorde aucune confirmation rétroactive.

## Vérifier avant une longue collecte

1. Examiner le bilan conservé et les motifs de non-confirmation. Une lecture locale permet encore de consulter les contenus déjà recueillis.
2. Vérifier dans **Bibliothèque → Connexion** le format et le fuseau effectivement utilisés par le portail GLPI. Ne pas choisir un fuseau d’après le seul poste Windows. Si les dates contiennent déjà un décalage explicite, un fuseau supplémentaire n’est pas nécessaire.
3. Charger un relevé GLPI actuel, sélectionner quelques tickets représentatifs et prévisualiser le périmètre. Le précontrôle doit expliquer les dates qu’il peut interpréter.
4. Collecter ce petit périmètre, puis vérifier le nombre de fiches complètes, confirmées et sans conflit. Préparer une copie SQLite cohérente et l’analyser dans Relais avant de lancer la totalité.
5. Si ce premier essai est bon, étendre le périmètre. À la fin, conserver le bilan et vérifier les motifs restants avant l’import final.

Une correction de format ou de fuseau change le contexte de collecte. Un ancien journal ne doit pas être repris sous une nouvelle interprétation des dates. Des versions anciennes sans date comparable peuvent rester en conflit après une nouvelle lecture ; aucun réglage ne doit les déclarer artificiellement récentes. Consulter les versions ou appliquer la procédure ci-dessous. Changer seulement le dossier de bibliothèque ne crée pas une base vierge : Pilot transfère normalement l’ancienne bibliothèque vers ce dossier.

### Recollecter sur une copie en conservant la bibliothèque d’origine

Si les anciennes versions empêchent une collecte confirmée, un départ neuf peut être choisi explicitement avec les fonctions existantes de l’édition Admin. Il n’est jamais effectué par cette mise à jour.

1. Arrêter les collectes, désactiver la collecte au démarrage et les programmations, fermer les deux lanceurs et sauvegarder le dossier de bibliothèque complet.
2. Ouvrir **GLPI-Pilot-Admin.exe**. Choisir dans les réglages de bibliothèque un nouveau dossier local distinct et le mode local sans partage, puis enregistrer. Pilot copie la bibliothèque vers ce nouvel emplacement ; vérifier le chemin avant l’étape suivante. L’ancien dossier reste conservé.
3. Dans **Administration locale**, choisir **Réinitialiser les tickets locaux** sur cette copie, après lecture de l’aperçu et confirmation. Une seconde sauvegarde SQLite est créée dans `Backups`, contrôlée par `integrity_check` et identifiée par son SHA-256 avant l’effacement des tickets de la copie. Les données d’équipe et le planning sont conservés ; ne pas choisir la réinitialisation de toute la base.
4. Conserver le chemin et l’empreinte de sauvegarde. Vérifier les réglages de dates, charger un relevé GLPI actuel et effectuer le petit essai avant la collecte complète.
5. Garder le partage désactivé pendant cette vérification. Une synchronisation pourrait réintroduire les anciennes versions ; le rapprochement avec le partage doit être examiné séparément.

Le portail et la base distante de GLPI ne sont pas modifiés. L’original, la copie avant réinitialisation et la nouvelle collecte restent distincts. L’édition Standard ne propose pas cette opération d’administration.

## Qualification et limites

Les tests utilisent de vraies bases SQLite et des tickets fictifs. Ils couvrent les anciens journaux sans reprise, le précontrôle des dates, les contenus partiels, les conflits et l’absence d’écriture pendant le bilan. Une fixture de capture native suit `BeginCapture → SetCaptureIndex → ObserveCaptureSeed → SaveCaptureDetail → ConfirmCaptureTicket → FinishCaptureVerification → FinishCapture` ; la confirmation change l’admissibilité sans changer les corps.

La qualification Windows et PostgreSQL du commit `1e8782c539e0b2263de47ed76c274e524e005a5a` a réussi **16/16 jobs** dans le [run 44892356079](https://github.com/M4D135/GLPI-Pilot/actions/runs/44892356079), tentative **1**, sans job ignoré, annulé ou en échec. La [release v4.0.17](https://github.com/M4D135/GLPI-Pilot/releases/tag/v4.0.17) est publiée depuis le **21 novembre 2026 à 11:39:30 (Europe/Paris)**. Le ZIP original inspecté a été publié sans recompilation ni recompression ; le ZIP et ses empreintes publiques ont été retéléchargés et comparés octet par octet aux originaux. Les **381 fichiers manifestés** ont été vérifiés de nouveau. Le [reçu de qualification et publication](QUALIFICATION-4.0.17.md) conserve l’identité du pack, ses empreintes et les liens des preuves.

Aucun accès au portail privé de l’organisation ni aucune analyse de sa base réelle n’est revendiqué. La durée d’une future collecte dépend du portail, du nombre de rubriques, des temps d’attente et des réglages ; les essais synthétiques ne garantissent pas une durée terrain.
