import { el, button, card, toolbar, field, input, table, confirm, formatNumber } from '../ui.js';
import { facts, pager } from './terrain-common.js';

/** The share is consulted only by an explicit command; no discovery runs on render. */
export function sharedUpdatePanels(p, options) {
  const { simulation, administration, ensurePolicySaved, clearPrepared, setPrepared } = options;
  const folder = input(options.sharedFolder || '', { maxLength: 3000, placeholder: '\\\\serveur\\diffusion\\GLPI-Pilot', disabled: simulation });
  let savedFolder = folder.value, offer = null, patch = null;
  const sharedReview = el('div'), patchReview = el('div');
  const disabled = { disabled: simulation };
  function folderReady() {
    if (simulation) throw new Error('Les partages réels restent inaccessibles depuis la démonstration.');
    if (folder.value.trim() !== savedFolder) throw new Error('Enregistrez le dossier modifié avant de consulter sa publication.');
    if (!savedFolder) throw new Error('Choisissez et enregistrez le dossier de diffusion.');
  }
  function invalidate() { offer = null; patch = null; clearPrepared(); paintOffer(); paintPatch(); }
  const choose = button('Choisir le dossier…', p.act(async () => {
    const result = await p.call('update.shared.pick'); if (result.cancelled) return;
    folder.value = result.folder; invalidate(); p.message('Dossier choisi. Enregistrez-le avant de rechercher les publications.');
  }), disabled);
  const save = button('Enregistrer le dossier', p.act(async () => {
    const result = await p.call('update.shared.save', { folder: folder.value.trim() });
    savedFolder = result.folder; folder.value = savedFolder; invalidate();
    p.message(savedFolder ? 'Dossier de diffusion enregistré. Aucune recherche automatique n’est lancée.' : 'Dossier de diffusion retiré.');
  }), disabled);
  const find = button('Rechercher le pack réseau', p.act(async () => {
    folderReady(); offer = null; paintOffer();
    offer = await p.call('update.shared.find', { consent: true }); paintOffer();
    p.message(offer.newer ? 'Une version plus récente est proposée sur le partage.' : 'Le pack du partage ne dépasse pas la version installée.');
  }), disabled);
  const copy = button('Copier et vérifier le pack réseau', p.act(async () => {
    folderReady(); ensurePolicySaved(); if (!offer?.newer) throw new Error('Recherchez une version plus récente sur le partage.');
    if (!await confirm('Préparer la mise à jour réseau', `Copier localement la version ${offer.version} (${formatNumber(offer.bytes / 1048576, { maximumFractionDigits: 1 })} Mio) et vérifier son empreinte ? L’installation attendra une confirmation séparée.`, 'Copier et vérifier')) return;
    clearPrepared(); const result = await p.call('update.shared.prepare', { token: offer.token, consent: true });
    setPrepared(result); p.message('Pack réseau copié et vérifié localement. L’installation attend votre décision.');
  }), { disabled: true });
  const preview = button('Comparer le patch de sites', p.act(async () => {
    folderReady(); patch = null; paintPatch(); patch = await p.call('update.sites.preview'); paintPatch();
    p.message('Comparatif préparé. Les valeurs locales restent inchangées jusqu’à votre confirmation.');
  }), disabled);
  const apply = button('Appliquer le patch de sites', p.act(async () => {
    folderReady(); if (!patch) throw new Error('Comparez le patch avant de l’appliquer.');
    if (!await confirm('Appliquer le référentiel partagé', `${patch.added} ajout(s), ${patch.modified} modification(s), ${patch.unchanged} élément(s) identique(s). Les valeurs modifiées, y compris vos corrections locales affichées dans « Avant », seront remplacées par celles du patch. Les éléments absents sont conservés. Une sauvegarde sera créée avant application.`, 'Sauvegarder et appliquer')) return;
    const result = await p.call('update.sites.apply', { token: patch.token, confirmed: true });
    patch = null; paintPatch();
    patchReview.append(card('Patch de sites appliqué', facts([['Ajouts', result.added], ['Modifications', result.modified], ['Identiques', result.unchanged], ['SHA-256 appliqué', result.sha256], ['Sauvegarde du référentiel précédent', result.backupPath]])));
    p.message('Référentiel mis à jour. Les identifiants et les éléments absents du patch ont été conservés.');
  }), { primary: true, disabled: true });
  const exportPatch = administration ? button('Exporter un patch de sites', p.act(async () => {
    const result = await p.call('admin.sites.export'); if (result.cancelled) return;
    p.dialog('Patch prêt pour diffusion', facts([['Dossier créé', result.folder], ['Lignes', result.rows], ['SHA-256', result.sha256]]));
    p.message('Le patch exporté contient le référentiel enregistré. Déposez ses deux fichiers dans le dossier de diffusion après validation.');
  }), disabled) : null;
  const network = card('Dossier réseau partagé',
    el('p', { text: 'Alternative à GitHub : déposez le ZIP de l’édition du poste (PROD ou PROD-LIGHT) et SHA256SUMS.txt dans ce dossier. Le logiciel copie le pack localement avant de le vérifier.' }),
    simulation ? el('p', { class: 'terrain-warning', text: 'Démonstration : aucun dossier réseau réel n’est consulté. Quittez la démonstration pour utiliser une publication réelle.' }) : null,
    field('Dossier de diffusion', folder, 'Chemin Windows UNC ou dossier local accessible avec les droits de votre session.'), toolbar(choose, save, find, copy), sharedReview);
  const sites = card('Patch des sites',
    el('p', { text: 'Le même dossier peut contenir GLPI-Pilot-Sites-v1.csv et GLPI-Pilot-Sites.sha256. Ce patch met à jour les villes, sites et bâtiments sans remplacer le logiciel. Les éléments absents du fichier restent conservés.' }),
    el('p', { text: 'Vérifiez les valeurs avant/après, notamment vos corrections locales. Les identifiants restent stables pour conserver les liens des interventions et des tournées. Aucun ticket distant n’est modifié.' }),
    toolbar(preview, apply, exportPatch), patchReview);
  folder.addEventListener('input', invalidate);
  function paintOffer() {
    copy.disabled = simulation || !offer?.newer;
    sharedReview.replaceChildren(offer ? facts([['Version', offer.version], ['Archive', offer.archiveName], ['Taille', `${formatNumber(offer.bytes / 1048576, { maximumFractionDigits: 1 })} Mio`], ['SHA-256', offer.archiveSha256]])
      : el('p', { text: 'Aucune publication réseau consultée.' }));
  }
  function paintPatch() {
    apply.disabled = simulation || !patch || patch.added + patch.modified === 0;
    if (!patch) { patchReview.replaceChildren(el('p', { text: 'Aucun patch comparé. La comparaison ne modifie pas les données.' })); return; }
    const items = patch.items || [];
    patchReview.replaceChildren(facts([['Ajouts', patch.added], ['Modifications', patch.modified], ['Identiques', patch.unchanged], ['SHA-256', patch.sha256]]),
      ...items.map(item => el('details', { class: 'terrain-details', open: item.kind !== 'Identique' },
        el('summary', { text: `${item.kind} · ${item.type} · ${item.name}` }),
        el('p', { text: `${item.detail} · Identifiant : ${item.id}` }),
        table(item.fields || [], [{ key: 'name', label: 'Champ' }, { key: 'before', label: 'Avant' }, { key: 'after', label: 'Après' }], { maxRows: 100, emptyText: 'Aucune valeur modifiée.' }))),
      pager(patch, offset => p.run(async () => { folderReady(); patch = await p.call('update.sites.page', { token: patch.token, offset, limit: patch.limit || 50 }); paintPatch(); })));
  }
  paintOffer(); paintPatch();
  return { network, sites };
}
