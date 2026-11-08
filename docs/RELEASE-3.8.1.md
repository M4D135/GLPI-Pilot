# GLPI Pilot 3.8.1 — Import des sites avec un relevé conservé ouvert

Après le redémarrage, le dernier relevé retrouvé dans le cache était considéré comme une archive pour protéger son interprétation. L'import CSV des sites fusionnait correctement les références dans le brouillon, mais leur enregistrement échouait avec « Fermez l’archive ». L'interface ne proposait pas de fermeture générale de cette consultation.

Le référentiel de travail peut désormais être enregistré pendant la consultation d'un cache ou d'une archive. L'aperçu précise qu'il s'agit des paramètres du poste : les données affichées, leur localisation et les fichiers du relevé conservé restent inchangés. Le prochain import ou relevé courant utilise le référentiel enregistré. Sur un relevé courant, l'aperçu de recalcul et l'application des nouvelles localisations restent disponibles.

Dans **Données → Sites et détection**, importer le CSV, examiner le comparatif et choisir **Fusionner dans le brouillon**. Si un relevé conservé est affiché, utiliser **Enregistrer le référentiel de travail**, puis confirmer son aperçu. Fermer le panneau et choisir **Réglages → Configuration complète et déploiement → Définir comme configuration par défaut** pour diffuser les sites enregistrés avec les autres options.

Les nouveaux contrôles de régression couvrent le parcours réel après restauration du cache, une archive historique, les aperçus périmés et l'inclusion des sites dans la configuration par défaut. La publication reste conditionnée aux contrôles Windows, PostgreSQL, aux démarrages des lanceurs Standard/Admin, aux mises à jour et à la reconstruction du pack DEV.

Les packs PROD et DEV contiennent les deux lanceurs ; le mécanisme de mise à jour conserve la configuration de déploiement voisine des EXE.
