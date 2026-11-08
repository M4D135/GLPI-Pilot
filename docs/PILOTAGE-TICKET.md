# Piloter un ticket depuis Actions assistées

Le centre ticket est intégré au chantier 3.13.0 avec la refonte ergonomique. Les notes `RELEASE-3.13.0.md` décrivent la livraison ; la qualification se vérifie sur le SHA des packs publiés.

## Commandes

Le centre « Piloter le ticket » se trouve dans Tickets > Actions assistées. Il réunit Ajouter un suivi, Ajouter une tâche, Affecter et Toutes les actions. L'ouverture ne déclenche ni collecte, ni inspection de formulaire, ni écriture dans GLPI. Choisir une opération non reconnue ne la remplace jamais par une opération différente.

Le moteur existant peut préparer les formulaires natifs GLPI d'affectation, de suivi et de tâche. Le technicien ouvre le formulaire voulu dans sa session GLPI, demande son inspection, prépare un aperçu puis confirme l'envoi. Les droits et les champs réellement proposés dépendent de l'installation GLPI. Ce chantier n'ajoute pas d'adaptateur d'écriture pour le statut, la priorité ou la résolution : leur commande ouvre la fiche native, où la validation reste explicite.

« Organiser l'intervention » regroupe planning, matériel et fiches terrain, notes/checklist/rappels et compte rendu. Ces raccourcis conservent la clé du ticket validé. Ils ne prétendent pas changer son statut ou son affectation GLPI.

## Protections

- Les adaptations restent propres à l'installation et aux formulaires effectivement reconnus. Le moteur C# de contrôle de structure, de destination et de soumission unique est inchangé.
- Un aperçu affiche avant/après et les autres champs transmis. Il doit être relu, notamment pour la visibilité et les destinataires. La confirmation finale autorise un seul envoi.
- La fermeture par Échap, une saisie modifiée, une expiration ou une durée de validité illisible invalident l'aperçu. Une réponse tardive ne peut pas réutiliser une saisie modifiée.
- Un résultat indéterminé reste visible et impose une vérification manuelle. Aucun renvoi automatique, même si le succès n'a pas pu être prouvé. Les reçus restent conservés.
- Un reçu vérifié entraîne une relecture explicite de la fiche. Un refus d'aperçu conserve le texte non envoyé.
- Imports et archives ne deviennent pas des sources autorisées en écriture. Simulation et vraies actions GLPI restent distinctes.

Aucun ticket privé n'a été ouvert, modifié ou clôturé pendant le développement de cette proposition.

## Vérifications et limites

`tests/TicketControlChecks/run.cjs` couvre 18 vérifications, avec les modules JS/CSS de production et des réponses API synthétiques. Ouverture sans mutation, opération indisponible sans substitution, inspection explicite, aperçu, annulation, confirmation unique, reçu indéterminé, saisie conservée, aperçu tardif, navigation locale et accès au formulaire natif sont couverts. Fenêtres 1280x800, 1093x614 et 390x800.

Ces 18 vérifications passent localement en mode `GLPI_TEST_OFFLINE_BLOBS=1`. Le navigateur du conteneur refuse la navigation vers les URL de test : ce mode charge les modules locaux en mémoire sans désactiver cette politique. Il ne qualifie pas la CSP de production, le pont C#, le SSO privé ni les vrais EXE Windows.

Le workflow `library-collection-check.yml` utilise les mêmes tests sans ce mode ; `ergonomics-check.yml` exécute les contrôles WorkplaceBridgeChecks sur Windows. Ces deux workflows sont appelés par la qualification complète `windows.yml`, via `library-compatibility-check.yml`. Leurs résultats doivent être vérifiés sur le commit concerné. Aucun succès CI, publication ou qualification des packs n'est anticipé par ce document.

```sh
GLPI_TEST_PLAYWRIGHT_MODULE=/chemin/vers/.playwright/package \
GLPI_TEST_CHROMIUM=/chemin/vers/chromium \
node tests/TicketControlChecks/run.cjs
```

La refonte des widgets, cartes, tournées et du matériel est intégrée dans la même version. Le workflow principal exige la qualification ergonomique et Windows complète avant publication. La release et le tag v3.12.0 restent inchangés.
