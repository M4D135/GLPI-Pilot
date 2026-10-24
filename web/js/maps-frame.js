// Dedicated map window. No application bridge, commands, tickets or saved configuration.
(() => {
  let initialized = false;
  window.showGoogleMap = model => {
    if (initialized || !model || typeof model.embedUrl !== 'string') return false;
    let url;
    try { url = new URL(model.embedUrl); } catch { return false; }
    if (url.origin !== 'https://www.google.com' || !['/maps/embed/v1/place', '/maps/embed/v1/directions'].includes(url.pathname)
      || !url.searchParams.get('key') || url.username || url.password || url.hash || model.embedUrl.length > 2048) return false;
    initialized = true;
    document.documentElement.dataset.theme = model.theme === 'light' ? 'light' : 'dark';
    document.documentElement.dataset.accent = ['blue', 'violet', 'green', 'orange', 'rose'].includes(model.accent) ? model.accent : 'blue';
    document.querySelector('#map-title').textContent = model.title || 'Google Maps';
    if (model.simulation) document.querySelector('#map-context').textContent = 'Simulation · accès Internet demandé. Seuls les lieux choisis sont transmis à Google.';
    const frame = document.querySelector('#map-frame'), status = document.querySelector('#map-status');
    frame.addEventListener('load', () => { status.textContent = 'Carte chargée. Si Google affiche un refus, vérifiez la clé et ses restrictions dans les réglages Google Maps.'; }, { once: true });
    frame.addEventListener('error', () => { status.textContent = 'Carte indisponible. Vérifiez la connexion Internet ou utilisez le lien Google Maps sans clé.'; }, { once: true });
    status.textContent = 'Chargement de Google Maps…'; frame.hidden = false; frame.src = url.href; return true;
  };
})();
