import { el, button, toolbar, dialog, showError, formatNumber } from '../ui.js';
import land from '../../vendor/natural-earth/land.js';

const tileUrl = 'https://tile.openstreetmap.org/{z}/{x}/{y}.png';
const routeColors = ['#397ad8', '#009b83', '#c47513', '#a760c5', '#d35767', '#548b36'];
const coordinate = place => Number.isFinite(place.latitude) && Number.isFinite(place.longitude)
  && Math.abs(place.latitude) <= 90 && Math.abs(place.longitude) <= 180;
const positionText = place => coordinate(place)
  ? `${place.latitude.toLocaleString('fr-FR', { maximumFractionDigits: 5 })}, ${place.longitude.toLocaleString('fr-FR', { maximumFractionDigits: 5 })}`
  : 'Coordonnées non renseignées';

export function openSiteMap(ctx, siteId = '') {
  return openMap(ctx, siteId ? { kind: 'site', id: siteId } : { kind: 'sites' });
}

export async function openMap(ctx, request) {
  const model = await ctx.api.call('maps.preview', request, { signal: ctx.signal });
  if (ctx.signal.aborted) return;
  const L = window.L;
  if (!L?.map) throw new Error('La carte locale est indisponible. Vérifiez que le dossier web/vendor/leaflet est présent dans le pack.');
  const routes = model.routes || [], entries = routes.flatMap((route, routeIndex) => (route.places || []).map((place, index) => ({
    route, place, routeIndex, index, key: `${routeIndex}:${index}`, located: coordinate(place),
    color: routeColors[routeIndex % routeColors.length]
  })));
  const located = entries.filter(entry => entry.located), missing = entries.filter(entry => !entry.located);
  const errors = el('div', { class: 'maps-errors', role: 'alert' });
  const state = el('span', { class: 'maps-state', role: 'status', 'aria-live': 'polite', text: 'Fond local · sans rues' });
  const mapElement = el('div', { class: 'maps-canvas', 'aria-label': 'Carte des lieux',
    dataset: { locatedPlaces: located.length, missingPlaces: missing.length, orderConnections: 0, tiles: 'offline' } });
  const list = el('div', { class: 'maps-place-list', 'aria-label': 'Lieux de la carte' });
  const sources = el('details', { class: 'maps-sources' }, el('summary', { text: 'Sources des fonds de carte' }),
    el('p', { text: 'Fond local : Natural Earth, domaine public. Il représente les terres à petite échelle, sans rues ni calcul routier.' }),
    el('p', { text: 'Fond détaillé : © OpenStreetMap contributors. Données sous licence Open Database License (ODbL). Conditions : https://www.openstreetmap.org/copyright' }));
  let map, layer = null, disposed = false, generation = 0, resizing = 0, observer = null;
  const markers = new Map(), rows = new Map(); let current = null;
  const streets = button('Afficher les rues', () => setStreets(!layer), {
    icon: 'map', disabled: !located.length, attributes: { 'aria-pressed': 'false' },
    title: located.length ? 'Afficher le fond OpenStreetMap dans cette carte · nécessite Internet' : 'Renseignez les coordonnées des lieux pour les placer sur la carte'
  });
  const controls = toolbar(button('Tout afficher', () => fit(), { disabled: !located.length }), streets, state);
  controls.classList.add('maps-toolbar');
  const legend = el('p', { class: 'maps-legend', text: routes.some(route => route.ordered)
    ? 'Les pointillés indiquent seulement l’ordre des visites, sans trajet routier. Ils s’interrompent aux étapes sans coordonnées.'
    : 'Les repères utilisent les coordonnées enregistrées dans le référentiel.' });
  const description = el('div', { class: 'maps-description' },
    el('p', { class: 'maps-summary', text: `${formatNumber(entries.length)} lieu${entries.length === 1 ? '' : 'x'} · ${formatNumber(located.length)} repère${located.length === 1 ? '' : 's'} sur la carte${missing.length ? ` · ${formatNumber(missing.length)} sans coordonnées` : ''}` }),
    model.simulation ? el('span', { class: 'badge info', text: 'Simulation · fond local par défaut' }) : null);
  const body = el('div', { class: 'maps-workspace' }, description, controls, errors,
    el('div', { class: 'maps-grid' }, el('div', { class: 'maps-stage' }, mapElement, legend), list),
    model.notice ? el('p', { class: 'maps-notice', text: model.notice }) : null,
    ...(model.notes || []).map(text => el('p', { class: 'maps-note', text })), sources);
  const modal = dialog(model.title || 'Carte des sites et des tournées', body);
  modal.element.classList.add('maps-dialog');
  const closeOnAbort = () => modal.close();
  ctx.signal.addEventListener('abort', closeOnAbort, { once: true });
  modal.element.addEventListener('pilot:closed', cleanup, { once: true });

  function setState(label, tone = '') { state.textContent = label; state.className = `maps-state ${tone}`; }
  function syncTileButton() {
    streets.querySelector('span').textContent = layer ? 'Fond hors ligne' : 'Afficher les rues';
    streets.setAttribute('aria-pressed', String(!!layer));
    mapElement.dataset.tiles = layer ? 'online' : 'offline';
    attribution?.replaceChildren(...[el('span', { text: 'Natural Earth' }), layer ? el('span', { text: ' · ' }) : null,
      layer ? attributionLink() : null].filter(Boolean));
  }
  function attributionLink() {
    return el('a', { href: 'https://www.openstreetmap.org/copyright', text: '© OpenStreetMap contributors', onClick: event => {
      event.preventDefault(); sources.open = true; sources.scrollIntoView({ block: 'nearest' });
    } });
  }
  async function revoke() {
    // Cleanup must survive page cancellation. A preview may have expired, but
    // disabling its image lease is idempotent and must always reach the host.
    try { await ctx.api.call('maps.tiles', { token: model.token, enabled: false }); } catch { /* The local layer is already detached. */ }
  }
  function detachTiles() {
    if (!layer) return;
    const previous = layer; layer = null;
    // Leaflet installs its map-listener cleanup on the layer's remove event.
    // Calling off() first would delete that cleanup and break the next zoom.
    map.removeLayer(previous); previous.off();
  }
  async function setStreets(enabled, reason = '') {
    const operation = ++generation;
    streets.disabled = true;
    if (!enabled) {
      detachTiles();
      if (!disposed) { fit(); syncTileButton(); setState(reason || 'Fond local · sans rues', reason ? 'warning' : ''); }
      await revoke(); if (!disposed && operation === generation) streets.disabled = !located.length; return;
    }
    if (disposed || !located.length) return;
    errors.replaceChildren(); setState('Chargement du fond détaillé…');
    try {
      await ctx.api.call('maps.tiles', { token: model.token, enabled: true }, { signal: ctx.signal });
      if (disposed || operation !== generation || ctx.signal.aborted) { await revoke(); return; }
      const next = L.tileLayer(tileUrl, {
        minZoom: 0, maxZoom: 19, noWrap: true, detectRetina: false, keepBuffer: 0,
        updateWhenIdle: true, updateWhenZooming: false, referrerPolicy: 'strict-origin-when-cross-origin'
      });
      layer = next; fit(); syncTileButton();
      next.on('load', () => { if (!disposed && layer === next) setState('Rues · OpenStreetMap'); });
      next.once('tileerror', () => {
        // Finish Leaflet's tile event before detaching its layer or changing
        // zoom. Removing it inside tileerror leaves in-flight view listeners
        // with a null map during the same event dispatch.
        queueMicrotask(() => {
          if (!disposed && layer === next) void setStreets(false, 'Fond détaillé indisponible · le plan local reste affiché');
        });
      });
      next.addTo(map);
    } catch (error) {
      if (!disposed) { detachTiles(); fit(); syncTileButton(); }
      await revoke();
      if (!disposed && operation === generation && !ctx.signal.aborted) {
        setState('Fond local · sans rues'); showError(errors, error);
      }
    } finally { if (!disposed && operation === generation) streets.disabled = !located.length; }
  }
  function fit() {
    if (!map || disposed) return;
    if (located.length) map.fitBounds(L.latLngBounds(located.map(entry => [entry.place.latitude, entry.place.longitude])), {
      padding: [38, 38], maxZoom: !layer && located.length === 1 ? 7 : 15, animate: false
    });
    else map.setView([20, 0], 2, { animate: false });
  }
  function select(entry, fromMap = false) {
    if (disposed) return;
    if (current) { rows.get(current.key)?.setAttribute('aria-pressed', 'false'); markers.get(current.key)?.getElement()?.classList.remove('maps-marker-selected'); }
    current = entry;
    rows.get(entry.key)?.setAttribute('aria-pressed', 'true');
    const marker = markers.get(entry.key);
    marker?.getElement()?.classList.add('maps-marker-selected');
    if (marker) { if (!fromMap) map.panTo(marker.getLatLng(), { animate: false }); marker.openPopup(); }
    else map.closePopup();
    if (fromMap) rows.get(entry.key)?.scrollIntoView({ block: 'nearest' });
  }
  function popupContent(entry) {
    const place = entry.place;
    return el('div', { class: 'maps-popup' }, el('strong', { text: place.label }),
      place.address ? el('p', { text: place.address }) : null,
      el('p', { text: place.precision }), el('p', { class: 'maps-coordinates', text: positionText(place) }));
  }
  function cleanup() {
    if (disposed) return;
    disposed = true; generation++; observer?.disconnect(); cancelAnimationFrame(resizing);
    ctx.signal.removeEventListener('abort', closeOnAbort);
    detachTiles();
    map?.remove(); void revoke();
  }

  let attribution;
  try {
    map = L.map(mapElement, { attributionControl: false, zoomControl: false, minZoom: 1, maxZoom: 19,
      zoomAnimation: false, fadeAnimation: false, markerZoomAnimation: false, worldCopyJump: false }).setView([20, 0], 2);
    // The opaque local land must stay below the optional raster tiles. Paths
    // use overlayPane by default, which would cover every street on land.
    map.createPane('maps-land').style.zIndex = '100';
    L.control.zoom({ zoomInTitle: 'Agrandir la carte', zoomOutTitle: 'Réduire la carte' }).addTo(map);
    L.control.scale({ imperial: false }).addTo(map);
    const attributionControl = L.control({ position: 'bottomright' });
    attributionControl.onAdd = () => {
      attribution = el('div', { class: 'maps-attribution' }, el('span', { text: 'Natural Earth' }));
      L.DomEvent.disableClickPropagation(attribution); L.DomEvent.disableScrollPropagation(attribution); return attribution;
    };
    attributionControl.addTo(map);
    L.geoJSON(land, { pane: 'maps-land', interactive: false, style: { className: 'maps-land', weight: 1, fillOpacity: 1 } }).addTo(map);
    let connections = 0;
    for (const [routeIndex, route] of routes.entries()) {
      const group = entries.filter(entry => entry.routeIndex === routeIndex);
      const heading = el('h3', { text: route.title || (route.ordered ? 'Étapes de la tournée' : 'Sites du référentiel') });
      const routeList = el(route.ordered ? 'ol' : 'ul', { class: 'maps-route-list' });
      for (const entry of group) {
        const place = entry.place;
        const label = route.ordered ? `${place.sequence || entry.index + 1}. ${place.label}` : place.label;
        const control = button(label, () => select(entry), { attributes: { class: 'button maps-place', 'aria-pressed': 'false',
          dataset: { placeKey: entry.key, placeId: place.id, located: String(entry.located) } } });
        control.style.setProperty('--map-route-color', entry.color);
        control.append(...[place.address ? el('small', { class: 'maps-place-address', text: place.address }) : null,
          el('small', { class: entry.located ? 'maps-place-precision' : 'maps-place-missing', text: entry.located ? place.precision : 'Sans coordonnées · non placé' }),
          entry.located ? el('small', { class: 'maps-coordinates', text: positionText(place) }) : null].filter(Boolean));
        rows.set(entry.key, control); routeList.append(el('li', {}, control));
        if (!entry.located) continue;
        const pin = el('span', { class: 'maps-pin', text: route.ordered ? place.sequence || entry.index + 1 : '•' });
        pin.style.setProperty('--map-route-color', entry.color);
        const marker = L.marker([place.latitude, place.longitude], {
          title: label, alt: label, keyboard: true, autoPanOnFocus: true,
          icon: L.divIcon({ className: 'maps-marker', html: pin, iconSize: [30, 30], iconAnchor: [15, 15], popupAnchor: [0, -18] })
        }).addTo(map).bindPopup(popupContent(entry), { maxWidth: 300, autoPan: true });
        marker.on('click', () => select(entry, true));
        marker.on('keydown', event => {
          if (event.originalEvent.key === 'Enter' || event.originalEvent.key === ' ') {
            L.DomEvent.stop(event.originalEvent); select(entry, true);
          }
        });
        markers.set(entry.key, marker);
        marker.getElement()?.setAttribute('data-place-key', entry.key);
        const previous = group[entry.index - 1];
        if (route.ordered && previous?.located) {
          L.polyline([[previous.place.latitude, previous.place.longitude], [place.latitude, place.longitude]], {
            color: entry.color, weight: 2.5, dashArray: '7 7', interactive: false, className: 'maps-order-line'
          }).addTo(map); connections++;
        }
      }
      list.append(el('section', { class: 'maps-route', dataset: { routeId: route.id } }, heading, routeList));
    }
    if (!entries.length) list.append(el('p', { class: 'maps-empty', text: 'Aucun lieu dans cette sélection. Les sites du référentiel apparaîtront ici lorsqu’ils seront renseignés.' }));
    if (missing.length) list.append(el('section', { class: 'maps-missing' },
      el('h3', { text: `Sans coordonnées (${formatNumber(missing.length)})` }),
      el('p', { text: 'Ces lieux restent dans la liste. Leur adresse ne permet pas de les placer sans coordonnées enregistrées.' }),
      el('ul', {}, ...missing.map(entry => el('li', { text: entry.place.label })))));
    mapElement.dataset.orderConnections = String(connections);
    map.invalidateSize({ animate: false }); fit();
    resizing = requestAnimationFrame(() => { if (!disposed) { map.invalidateSize({ animate: false }); fit(); } });
    observer = new ResizeObserver(() => {
      cancelAnimationFrame(resizing); resizing = requestAnimationFrame(() => { if (!disposed) map.invalidateSize({ animate: false, pan: false }); });
    });
    observer.observe(mapElement);
    if (!model.simulation && located.length) void setStreets(true);
  } catch (error) { cleanup(); modal.close(); throw error; }
  return modal;
}
