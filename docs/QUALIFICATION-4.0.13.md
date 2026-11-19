# Qualification GLPI Pilot 4.0.13 — 19 novembre 2026

## Résultat établi

Le correctif du refus de l’adresse native des préférences est qualifié sur le commit `b1f1890b2b1d80e97ebb4103adbbd8d4597312c1`.

- [Qualification Windows et PostgreSQL](https://github.com/M4D135/GLPI-Pilot/actions/runs/44544663495) : run `44544663495`, tentative `1`, **16/16 jobs réussis**, aucun échec, annulation ou job ignoré.
- La branche `main` reste sur ce commit exact pour permettre la publication du pack qualifié.
- Ce reçu documentaire est conservé sur une branche distincte ; il ne fait pas partie des octets du pack inspecté.

## Défaut corrigé et preuves

GLPI ajoute `_in_modal=1` à l’adresse de sa fenêtre native de préférences. Le journal de restauration de la 4.0.12 et la relecture moderne n’acceptaient que la requête exacte `itemtype=Ticket`. Le correctif autorise uniquement le marqueur modal natif facultatif, conserve l’adresse observée sans reconstruction et garde les contrôles d’origine, de document, de compte et de propriété des colonnes.

La comparaison dans une copie isolée donne **21/24, sortie 1** avec le journal exact du commit précédent `1e087b0ad2bf356aa6d1aadf8411d2a22e83d892`, puis **24/24, sortie 0** avec le correctif. La nouvelle suite refuse également les paramètres inconnus, les doublons et les variantes ambiguës.

| Contrôle officiel | Résultat |
| --- | --- |
| Configuration et restauration pure | 94/94 et 24/24 |
| Colonnes et récupération navigateur | 72/72 |
| Automatisation générale et attente des exports natifs | 53/53 et 11/11 |
| Lecture des fiches et acquisition de bibliothèque | 36/36 et 41/41 |
| Isolation, arrêt et reprise du moteur | 208/208 |
| Données et actualisation WebView, Standard puis Admin | 55/55 et 19/19 dans chaque édition |
| Vrais exécutables Windows, zoom et PDF | 72/72 |
| Mise à jour Windows et retour arrière | 4/4 |
| PostgreSQL : bibliothèque, partage, corpus SQL, administration | 55/55, 44/44, 12/12 et 19/19 |

Les trois nouveaux cas de navigateur passent : export legacy puis restauration exacte avec l’adresse modale ; relecture persistante moderne puis restauration ; interruption et reprise froide par l’adresse exacte du journal avant la liste des tickets. Les pages et données sont synthétiques ; aucun accès au portail privé de l’organisation n’a été utilisé.

## Archive originale inspectée

- [Artefact original](https://github.com/M4D135/GLPI-Pilot/actions/runs/44544663495/artifacts/14541452026) : `14541452026`.
- Nom : `GLPI-Pilot_4.0.13_PROD-LIGHT_Windows-x64.zip`.
- Taille : `138679990` octets.
- SHA-256 : `bccf32c766c00334c1946ccbde0e26bfb4417cffdd585ae9cb045b8ae5c1bf1a`.
- Version, commit source et distribution concordent avec `wad/version.json`.
- Racine : `GLPI-Pilot.exe`, `GLPI-Pilot-Admin.exe` et `wad/`.
- 370 fichiers couverts par le manifeste, plus le manifeste lui-même ; exécutables Standard et Admin distincts.
- Aucun runtime WebView2 embarqué, export de sources ou nom de données opérationnelles interdit par le vérificateur.

L’empreinte réelle du ZIP téléchargé concorde avec le digest de l’artefact et le reçu officiel du job Windows `135631957606`. Les deux modes de `select-verified-release.py` ont validé le lien entre le run complet, sa tentative, le commit, l’archive et le reçu horodaté. Le ZIP n’a pas été reconstruit ou recompressé.

## Publication à confirmer

Le clic de lancement du workflow public a été refusé par le contrôle d’approbation automatique, faute d’autorisation explicite de l’utilisateur couvrant la publication précise de la 4.0.13. Après ce refus, aucun workflow de publication n’est observé pour ce commit. La release publique précédente reste la 4.0.12 au moment de ce reçu.

Le formulaire de `publish-verified-release.yml` est préparé sur `main` :

| Champ | Valeur inspectée |
| --- | --- |
| `source_run` | `44544663495` |
| `source_attempt` | `1` |
| `light_sha256` | `bccf32c766c00334c1946ccbde0e26bfb4417cffdd585ae9cb045b8ae5c1bf1a` |

À la reprise, vérifier que `main` désigne toujours le commit qualifié, puis attendre ou utiliser l’accord explicite de publication. Après mise en ligne, vérifier le succès du workflow, le tag et les mêmes octets publics avant de compléter le reçu de livraison. Une compilation, une qualification ou ce document ne constitue pas une publication.
