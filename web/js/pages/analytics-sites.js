import { el, table, caption, command, details, formatNumber } from './analytics-ui.js';
import { openSiteMaps } from './google-maps.js';

export function sitesTable(ctx, sites, activate, copyIds) {
  return table(sites, [
    { key: 'label', label: 'Site' },
    { key: 'map', label: 'Carte', render: site => command(ctx, 'Google Maps', () => openSiteMaps(ctx, site.key),
      { disabled: !!site.unknown, attributes: { 'aria-label': `Google Maps · ${site.label}` } }) },
    { key: 'count', label: 'Stock actif', render: site => command(ctx, formatNumber(site.count),
      () => activate({ kind: 'sites', key: site.key }), { disabled: site.count === 0, attributes: { 'aria-label': `${site.label} · ${formatNumber(site.count)} tickets · Voir les lignes` } }) },
    { key: 'tickets', label: 'Premiers identifiants', render: site => el('div', { class: 'analytics-site-ticket-links' },
      ...(site.tickets ?? []).map(ticket => command(ctx, ticket.id || 'Sans ID', () => activate({ kind: 'record', rowIndex: ticket.index }),
        { title: ticket.title, attributes: { 'aria-label': `Consulter la ligne ${ticket.index + 1} · ${ticket.id || 'sans ID'} · ${site.label}` } }))) },
    { key: 'advice', label: 'Préconisation', render: site => el('span', { class: site.unknown && site.count ? 'warning' : '', text: site.advice }) },
    { key: 'copy', label: 'Liste complète', render: site => command(ctx, 'Copier les ID', () => copyIds({ kind: 'sites', key: site.key }),
      { disabled: site.count === 0, attributes: { 'aria-label': `Copier tous les ID · ${site.label}` } }) }
  ], { key: 'key', maxRows: 500 });
}

export function renderSites(ctx, data, setting, activate, more, copyIds) {
  const body = el('div', { class: 'analytics-sites' },
    caption(`${formatNumber(data.population)} ${data.grain} actifs · les sites à zéro sont conservés · les localisations incertaines restent à vérifier.`),
    sitesTable(ctx, data.sites ?? [], activate, copyIds));
  if (data.total > data.sites.length) body.append(command(ctx, `Tous les sites (${formatNumber(data.total)})`, () => more('sites', setting.sourceKey)));
  if (setting.note) body.append(caption(setting.note));
  if (setting.showDescription) body.append(details('Reconnaissance et préconisations', caption(data.explanation),
    caption(`Intervention à planifier à partir de ${formatNumber(data.threshold)} tickets pour un site connu. Un ticket sans site reconnu demande une vérification. Ce repère ne réserve pas d’intervention.`),
    caption('Les cinq premiers identifiants ouvrent les lignes du relevé. La copie conserve tous les identifiants renseignés du site, y compris les occurrences répétées dans la source.')));
  return body;
}
