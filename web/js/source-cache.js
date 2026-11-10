import { el, card, toolbar, button, badge, confirm, formatDate, formatNumber, isSimulation } from './ui.js';

// The source cache is distinct from the detailed ticket library and the synthetic demo.
export async function sourceCacheCard(ctx, { compact = false } = {}) {
  if (isSimulation(ctx.state.session)) return null;
  const host = card('Dernier relevé conservé · cache local');
  host.dataset.sourceCache = 'true';
  let disposed = false;
  ctx.signal?.addEventListener('abort', () => { disposed = true; }, { once: true });
  async function refresh() {
    const data = await ctx.api.call('cache.state', {}, { signal: ctx.signal });
    if (disposed) return;
    host.replaceChildren(el('h2', { text: 'Dernier relevé conservé · cache local' }));
    if (!data.available) {
      host.append(el('p', { text: data.error || 'Le prochain import ou relevé réel réussi sera conservé automatiquement sur ce poste pour le retrouver sans connexion.' }));
      return;
    }
    const source = data.fileName || data.title || (data.sourceKind === 'file' ? 'Import CSV / Excel' : 'Relevé GLPI');
    host.append(toolbar(badge(ctx.state.session.cachedSource ? 'Cache local consulté' : 'Copie locale disponible', 'info'),
      el('strong', { text: source })),
      el('p', { class: 'help', text: `${formatNumber(data.rows)} lignes · observation du ${formatDate(data.extractedAt)} · mémorisée le ${formatDate(data.savedAt)}` }),
      el('p', { text: 'Ce relevé daté reste consultable sans connexion. Il ne détermine pas l’état de votre session SSO ; utilisez « Revenir au GLPI Live » pour récupérer les données actuelles.' }));
    if (data.error) host.append(el('p', { class: 'notice warning', text: data.error }));
    if (data.recovered) host.append(el('p', { class: 'notice warning', text: 'La copie précédente a été retrouvée après un problème de lecture de la plus récente. Vérifiez la date affichée.' }));
    if (data.complete === false) host.append(el('p', { class: 'help', text: 'Cette copie conserve les limites du relevé d’origine ; elle ne certifie pas un inventaire complet de GLPI.' }));
    // Restoring a saved observation does not sever the browser session. A new
    // successful collection replaces the displayed cache; on failure the
    // existing observation is kept and the user sees the actual error.
    if (ctx.state.session.cachedSource) {
      host.append(el('p', { class: 'help', text: 'Vous consultez une photographie locale. Votre configuration GLPI reste disponible.' }),
        toolbar(button('Revenir au GLPI Live', async () => {
          try {
            await ctx.api.call('collect.run', {});
            await ctx.refresh();
          } catch (error) {
            if (error.name !== 'AbortError') ctx.notify('Retour au GLPI Live impossible : ' + error.message, 'danger');
          }
        }, { primary: true, icon: 'refresh' }),
        button('Configurer la collecte', () => ctx.navigate('data'), { icon: 'download' })));
    }
    host.append(toolbar(button(ctx.state.session.cachedSource ? 'Consulter le pilotage hors ligne' : 'Ouvrir les dernières données', async () => {
      if (!ctx.state.session.cachedSource) await ctx.api.call('cache.restore', {}, { signal: ctx.signal });
      await ctx.navigate('pilot');
    }, { icon: 'chart', primary: !ctx.state.session.loaded }),
    !compact ? button('Effacer la copie hors ligne', async () => {
      if (!await confirm('Effacer la copie hors ligne', 'Les dernières données mémorisées et leur copie de secours seront supprimées. Les fichiers d’origine, notes, interventions, bibliothèque de tickets et archives restent conservés. Un relevé déjà affiché reste utilisable jusqu’à sa fermeture.', 'Effacer la copie')) return;
      await ctx.api.call('cache.clear', { confirmed: true }, { signal: ctx.signal });
      ctx.notify('Copie hors ligne effacée. Le prochain import réel réussi pourra être mémorisé.');
      await refresh();
    }, { icon: 'trash' }) : null));
  }
  try { await refresh(); }
  catch (error) {
    if (disposed || error.name === 'AbortError') return null;
    host.append(el('p', { class: 'notice warning', text: `Copie hors ligne : ${error.message}` }));
  }
  return disposed ? null : host;
}
