# GLPI Pilot 3.4.1 — Équipes sans doublons et météo du périmètre

Cette correction traite deux incohérences de lecture dans Pilotage : le même périmètre proposé comme équipe et comme groupe, puis un volume filtré jugé selon les seuils du service en haut et ceux de l’équipe dans une seconde carte.

## Un seul choix pour un même périmètre

Le sélecteur **Équipes** est commun à Pilotage et Terrain. Les groupes GLPI du relevé restent la source des affectations. Une correspondance d’équipe sert à leur donner un nom plus lisible ou à réunir plusieurs groupes.

Lorsqu’une équipe et un groupe couvrent exactement le même ensemble de groupes, ils apparaissent une seule fois. Le nom de la première correspondance configurée est conservé. Les vrais regroupements indiquent les groupes inclus ; deux noms identiques couvrant des groupes différents restent des choix distincts. La recherche retrouve également les anciens noms et les groupes sources.

Les anciennes clés de sélection restent reconnues. La déduplication suit la comparaison des groupes du moteur : casse ignorée, accents et ponctuation conservés. Cocher plusieurs équipes réunit leurs tickets sans compter une ligne commune plusieurs fois. Aucune correspondance ni règle météo n’est supprimée du profil.

## Une météo adaptée aux tickets affichés

Pour une équipe sélectionnée dont la règle couvre exactement les lignes du périmètre, la météo principale reprend les seuils de cette équipe. La carte d’équipe entièrement représentée en tête est retirée de l’affichage courant. Les autres équipes hors sélection ne sont plus affichées comme des zéros maîtrisés.

Exemple fictif : avec **84 tickets**, des seuils d’équipe **10 / 20** donnent une météo sous tension dans la carte principale. La sélection ne réutilise plus les seuils globaux **100 / 200** pour afficher simultanément une météo maîtrisée sur ces mêmes 84 tickets. Les seuils utilisés restent visibles.

Une sélection de plusieurs équipes, un sous-ensemble ou une restriction supplémentaire n’hérite pas automatiquement d’un seuil de service ou d’une somme des seuils d’équipes. Le total reste consultable ; l’absence de seuil applicable est explicite. Les cartes qui décrivent encore exactement leur propre périmètre conservent leurs règles.

L’ordre historique des équipes reste respecté : une ligne multigroupe appartient à la première équipe correspondante pour sa carte météo. Si cette attribution exclusive et le filtre d’équipe ne couvrent pas les mêmes lignes, la carte principale signale cette différence au lieu de présenter une évaluation globale trompeuse. Les détails des tickets conservent leurs périmètres réels.

## Compositions et exports préservés

Le contexte météo est calculé pour l’affichage courant. Il ne réécrit ni les seuils enregistrés, ni les dimensions, ni les cartes masquées, ni les compositions personnalisées. **Composer** conserve les emplacements temporairement écartés et explique leur état. Retirer le filtre retrouve la composition complète.

Les exports de rapport utilisent le même contexte de calcul. L’import CSV/Excel et la simulation suivent les mêmes règles que le relevé courant. Le référentiel de sites et les profils existants restent compatibles.

## Installation et validation

Fermer l’ancienne instance, puis extraire entièrement le pack **3.4.1** avec **GLPI-Pilot.exe** et son dossier **wad**. Les préférences du profil utilisateur sont conservées. Les tests ciblés portent sur les catalogues, les sélections simples et cumulées, les lignes multigroupes, les filtres supplémentaires, les seuils nuls ou désactivés, les exports et l’absence de mutation des réglages.

Le [rapport de vérification](Rapport-verification.txt) donne les résultats locaux. Le workflow Windows du commit publié vérifie les packs, le démarrage natif, la mise à jour et la reconstruction DEV avant publication.
