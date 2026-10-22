import { el, button, card, toolbar, empty, badge, input, select, field, table, form, confirm, formatNumber } from '../ui.js';
import { page, pager, referenceField, highlighted, details, facts } from './terrain-common.js';

const matchNames = ['Reconnue', 'Sans correspondance', 'Ambiguë', 'Titre indisponible'];

export async function renderReview(ctx) {
  const p = page(ctx, 'Revoir les localisations', 'Retrouvez la décision de détection, corrigez un lieu localement et proposez une règle réutilisable.');
  if (!ctx.state.session?.loaded) { p.body.append(empty('Chargez un relevé pour revoir les localisations', 'Le référentiel et son simulateur restent disponibles sans relevé.'), button('Ouvrir le référentiel', () => ctx.navigate('terrain-reference'))); return () => p.cleanup(); }
  let revision = ctx.state.session.revision, offset = 0;
  const filter = select([{ value: 'review', label: 'À revoir' }, { value: 'ambiguous', label: 'Ambiguës' }, { value: 'unmatched', label: 'Sans localisation' }, { value: 'manual', label: 'Corrections locales' }, { value: 'matched', label: 'Reconnues' }, { value: 'all', label: 'Toutes les lignes' }], 'review');
  const search = input('', { placeholder: 'Identifiant, titre ou lieu', maxLength: 120 }), summary = el('div'), listing = el('div');
  p.body.append(summary, card('File de révision', toolbar(field('Afficher', filter), field('Rechercher', search), button('Rechercher', p.act(async () => { offset = 0; await load(); })),
    button('Actualiser', p.act(async () => { const state = await p.call('terrain.reference.state'); revision = state.revision; await load(); })), button('Règles et référentiel', () => ctx.navigate('terrain-reference'))), listing));
  async function load() {
    const data = await p.call('terrain.review.list', { revision, filter: filter.value, search: search.value, offset, limit: 50 }); revision = data.revision;
    summary.replaceChildren(el('div', { class: 'terrain-statbar' }, badge(`${formatNumber(data.summary.matchedCount)} reconnues`, 'ok'), badge(`${formatNumber(data.summary.unmatchedCount)} sans correspondance`),
      badge(`${formatNumber(data.summary.ambiguousCount)} ambiguës`, 'warn'), badge(`${formatNumber(data.summary.titleUnavailableCount)} titres absents`), badge(`${formatNumber(data.summary.manualCount)} corrections locales`)), el('p', { text: data.summary.message }));
    listing.replaceChildren(table(data.items, [
      { key: 'id', label: 'ID' }, { key: 'title', label: 'Titre' }, { key: 'cityName', label: 'Ville' }, { key: 'siteName', label: 'Site' },
      { key: 'state', label: 'Décision', render: row => badge(row.isOverride ? 'Correction locale' : matchNames[row.state], row.state === 2 ? 'warn' : '') },
      { key: 'actions', label: '', render: row => button('Examiner', p.act(() => examine(row.rowIndex))) }
    ], { key: 'rowIndex', maxRows: 50, onSelect: row => p.run(() => examine(row.rowIndex)), emptyText: 'Aucune ligne dans cette sélection.' }), pager(data, next => p.run(async () => { offset = next; await load(); })));
  }
  async function examine(row) {
    const data = await p.call('terrain.review.detail', { revision, row }); const errors = el('div', { role: 'alert' });
    let site;
    const city = referenceField(p, 'Ville corrigée', 'city', data.correction?.cityId || data.match.cityId || '', data.match.cityName || '', { saved: true, onChange: () => site?.set('') });
    site = referenceField(p, 'Site facultatif', 'site', data.correction?.siteId || data.match.siteId || '', data.match.siteName || '', { saved: true, parent: () => city.read(), empty: 'Ville seule' });
    const reason = form([{ key: 'reason', label: 'Motif de la correction', required: true, maxLength: 1000, help: 'Justification conservée localement avec le ticket ; 1 000 caractères au maximum.' }], { reason: data.correction?.reason || '' });
    const promotion = form([{ key: 'term', label: 'Terme à généraliser', required: true }, { key: 'requiredContext', label: 'Contexte requis facultatif' }, { key: 'contextColumn', label: 'Colonne du contexte', help: 'Vide : contexte recherché dans le titre.' }]);
    const content = el('div', { class: 'terrain-editor' }, errors, highlighted(data.title, data.highlights),
      data.titleTruncated ? el('p', { text: 'Titre affiché limité à 20 000 caractères.' }) : '',
      facts([['Ville', data.match.cityName], ['Site', data.match.siteName], ['Bâtiment', data.match.buildingName], ['Confiance', data.match.confidence], ['Décision', data.match.explanation]]),
      details('Termes et règles examinés', table(data.match.evidence, [{ key: 'cityName', label: 'Ville candidate' }, { key: 'term', label: 'Terme' }, { key: 'decision', label: 'Décision' }, { key: 'explanation', label: 'Justification' }], { maxRows: 100 })),
      details('Champs source du ticket', table(data.fields, [{ key: 'name', label: 'Colonne' }, { key: 'value', label: 'Valeur' }], { maxRows: 100 })),
      card('Correction locale', el('p', { text: 'La correction s’attache à l’installation GLPI et à l’identifiant unique du ticket. Le relevé, les tableaux de bord et les tournées seront recalculés.' }), city.element, site.element, reason.element,
        data.correction ? el('p', { text: `Correction conservée le ${new Date(data.correction.updatedUtc).toLocaleString('fr-FR')}.` }) : ''),
      card('Proposer une règle à partir de cette correction', promotion.element,
        button('Ajouter la règle au brouillon du référentiel', () => p.run(async () => {
          if (!city.read()) throw new Error('Choisissez la ville à reconnaître avant de proposer la règle.');
          const draft = await p.call('terrain.reference.state');
          await p.call('terrain.reference.promote', { draftVersion: draft.draftVersion, cityId: city.read(), ...promotion.read() });
          p.message('Règle ajoutée au brouillon du référentiel. Sa simulation globale reste nécessaire avant application.');
        }, errors)), button('Ouvrir le référentiel', () => { popup.close(); ctx.navigate('terrain-reference'); })));
    const popup = p.dialog(`Localisation du ticket ${data.id || 'sans ID'}`, content, [
      { label: 'Fermer', action: close => close() },
      ...(data.correction ? [{ label: 'Retirer la correction locale', danger: true, action: close => p.run(async () => {
        if (!await confirm('Retirer cette correction', 'Les règles du référentiel seront de nouveau utilisées pour ce ticket.', 'Retirer')) return;
        const result = await p.call('terrain.review.correct', { revision, row, cityId: '', siteId: '', reason: '' }); revision = result.revision; close(); await load();
      }, errors) }] : []),
      { label: 'Enregistrer la correction', primary: true, action: close => p.run(async () => {
        if (!city.read()) throw new Error('Choisissez la ville de correction.');
        const result = await p.call('terrain.review.correct', { revision, row, cityId: city.read(), siteId: site.read(), reason: reason.read().reason }); revision = result.revision;
        close(); await load(); p.message('Correction enregistrée et localisations recalculées.');
      }, errors) }
    ]);
  }
  filter.addEventListener('change', p.act(async () => { offset = 0; await load(); }));
  search.addEventListener('keydown', event => { if (event.key === 'Enter') p.run(async () => { offset = 0; await load(); }); });
  p.on('session.changed', () => p.message('Le relevé a changé. Actualisez la file de révision pour continuer.'));
  await load(); return () => p.cleanup();
}
