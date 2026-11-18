import { el, icon } from '../ui.js';
import { ticketCacheView } from '../ticket-cache.js';

export function ticketCacheIndicator(cache) {
  const view = ticketCacheView(cache);
  return el('span', {
    class: 'ticket-cache-indicator', role: 'img', tabIndex: 0,
    title: view.description, 'aria-label': view.description,
    dataset: { cacheState: view.state, cacheTone: view.tone },
    onClick: event => event.stopPropagation(),
    onKeydown: event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); event.stopPropagation(); } }
  }, icon('database'), el('span', { class: 'ticket-cache-mark', 'aria-hidden': 'true', text: view.mark }));
}

export function ticketCacheDates(cache) {
  return el('p', { class: 'ticket-cache-dates' }, ...ticketCacheView(cache).dates.map(item =>
    el('span', {}, el('span', { text: item.label + ' : ' }), el('strong', { text: item.value }))));
}
