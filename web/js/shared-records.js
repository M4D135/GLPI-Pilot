import { el, button, toolbar, table, empty, badge, formatDate, formatNumber, replaceChildren } from './ui.js';

export async function renderSharedRecords(ctx) {
  const { root, api, signal, navigate, notify } = ctx;
  const call = (operation, args = {}) => api.call(operation, args, { signal });
  const status = el('p', { class: 'help', role: 'status' }), results = el('div');
  root.append(el('p', { text: 'Chaque relevé conserve ses colonnes, ses lignes et sa date d’observation. Ouvrir un relevé commun alimente les tickets, les sites, le planning et le pilotage de ce poste.' }),
    el('p', { class: 'help', text: 'Une nouvelle version du relevé choisi sera chargée à la prochaine synchronisation. Une autre source ouverte volontairement reste en place.' }),
    toolbar(button('Actualiser la liste', load, { icon: 'refresh' }), button('Régler le partage', () => navigate('data', { panel: 'connection' }), { icon: 'settings' })), status, results);
  async function load() {
    const model = await call('sharing.datasets'); if (signal.aborted) return;
    status.textContent = model.enabled ? 'Les échanges suivent la synchronisation configurée pour ce poste.' : 'Partage désactivé. Les relevés déjà reçus restent consultables.';
    replaceChildren(results, ...(model.warnings || []).map(text => el('p', { class: 'notice warning', text })));
    if (!model.items?.length) { results.append(empty('Aucun relevé commun reçu', 'Activez le partage sur les postes concernés, puis synchronisez après une collecte.')); return; }
    if (model.items.length > 200) results.append(el('p', { class: 'notice', text: 'Les 200 derniers périmètres sont affichés.' }));
    results.append(table(model.items.slice(0, 200), [
      { label: 'Relevé', render: item => el('div', {}, el('strong', { text: item.title || 'Relevé de tickets' }), item.id === model.followedId ? badge('Suivi sur ce poste') : null) },
      { label: 'Observation', render: item => el('span', { text: formatDate(item.observedAt) }) },
      { label: 'Lignes', render: item => el('span', { text: formatNumber(item.rows) }) },
      { label: 'Colonnes', render: item => el('span', { text: formatNumber(item.columns) }) },
      { label: 'Ouvrir', render: item => button('Utiliser ce relevé', async () => {
        const result = await call('sharing.dataset.load', { id: item.id, expectedRevision: item.revision });
        notify(result.message); await navigate('records');
      }, { primary: true, attributes: { 'data-shared-dataset': item.id } }) }
    ]));
  }
  await load();
}
