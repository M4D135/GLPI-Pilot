import { el, input, select, field, checkbox, dialog, caption } from './analytics-ui.js';

/** Edit a selection identified by the C# run, never by a client-made filter key. */
export function openWeatherSelectionEditor(ctx, { model, widget, reload, trigger }) {
  const selection = widget.weatherSelection;
  if (!selection || selection.canEdit === false) return;
  const initial = selection.configured ? selection : selection.policy;
  const nativeUnit = widget.content?.unit || '';
  const numericLabel = nativeUnit && nativeUnit !== 'tickets' ? `Valeur de l’indicateur (${nativeUnit})` : 'Nombre';
  const unit = select([{ value: 'count', label: numericLabel }, { value: 'percent', label: 'Pourcentage (%)' }], initial?.unit || 'count');
  const mode = select([{ value: 'selection', label: 'Cette sélection uniquement' },
    { value: 'widget', label: 'Ce widget, toutes les sélections' }], !selection.configured && selection.policy ? 'widget' : 'selection');
  const denominators = selection.denominators || [];
  const suggestedBase = selection.recommendedDenominatorKey || denominators[0]?.key || 'selection-active';
  const denominator = select(denominators.map(item => ({ value: item.key,
    label: `${item.label}${item.key === suggestedBase ? ' · conseillée' : ''}${item.available === false ? ' · base indisponible' : ''}` })), initial?.denominatorKey || suggestedBase);
  unit.querySelector('option[value="percent"]').disabled = selection.percentageAvailable === false;
  const warning = input(initial?.warningValue ?? '', { type: 'number', min: 0, step: 'any', required: true });
  const critical = input(initial?.criticalValue ?? '', { type: 'number', min: 0, step: 'any', required: true });
  const lower = checkbox('Une valeur plus basse est préférable', initial?.lowerIsBetter !== false);
  const denominatorField = field('Base du pourcentage', denominator);
  const explanation = caption(''), scopeHelp = caption(''), baseHelp = caption(''), configured = caption('');
  const percentageHelp = selection.percentageAvailable === false ? caption(selection.percentageReason || 'Cet indicateur ne permet pas un calcul de part en pourcentage.') : null;
  const error = el('p', { class: 'notice danger', role: 'alert', hidden: true });
  const form = el('form', { class: 'weather-selection-form', onSubmit: event => event.preventDefault() },
    el('h3', { text: widget.settings.title || widget.content?.label || 'Météo du widget' }),
    el('p', { class: 'weather-selection-scope', text: selection.scopeDescription || widget.scopeLabel }),
    el('div', { class: 'analytics-form-grid' }, field('Unité des seuils', unit), field('Portée des seuils', mode)), percentageHelp, scopeHelp,
    denominatorField, baseHelp,
    el('div', { class: 'analytics-form-grid' }, field('Seuil de vigilance', warning), field('Seuil de tension', critical)),
    lower.element, explanation, configured, error);
  let pending = false, disposed = false;
  const current = () => mode.value === 'widget' ? selection.policy : selection.configured ? selection : null;
  const explain = () => {
    const percent = unit.value === 'percent';
    for (const control of [warning, critical]) { if (percent) control.max = '100'; else control.removeAttribute('max'); }
    const all = mode.querySelector('option[value="widget"]'); all.disabled = !percent;
    if (!percent && mode.value === 'widget') mode.value = 'selection';
    denominatorField.hidden = baseHelp.hidden = !percent;
    const base = denominators.find(item => item.key === denominator.value);
    baseHelp.textContent = base ? `Pourcentage = volume du widget ÷ ${base.label.toLocaleLowerCase('fr')} × 100. Une base nulle ou indisponible laisse la météo sans évaluation.`
      : 'La base nécessaire au calcul du pourcentage est indisponible.';
    scopeHelp.textContent = mode.value === 'widget'
      ? 'Ces seuils en pourcentage suivent les nouvelles sélections de ce widget. Des seuils enregistrés pour une sélection précise restent prioritaires.'
      : 'Ces seuils sont enregistrés avec les filtres de cette sélection. Un autre périmètre garde ses propres réglages.';
    configured.textContent = current() ? mode.value === 'widget' ? 'Une règle réutilisable est enregistrée pour ce widget.'
      : 'Des seuils sont enregistrés pour cette sélection.' : 'Aucun seuil personnalisé n’est enregistré pour cette portée.';
    const thresholdUnit = percent ? '%' : nativeUnit === 'tickets' ? 'nombre de tickets' : nativeUnit || 'valeur de l’indicateur';
    explanation.textContent = lower.control.checked
      ? `Les seuils sont exprimés en ${thresholdUnit}. La vigilance et la tension commencent strictement au-dessus de leur seuil ; le seuil de tension doit être supérieur ou égal au seuil de vigilance.`
      : `Les seuils sont exprimés en ${thresholdUnit}. La vigilance et la tension commencent strictement en dessous de leur seuil ; le seuil de tension doit être inférieur ou égal au seuil de vigilance.`;
    const remove = modal?.element.querySelector('[data-weather-selection-remove]'); if (remove) remove.disabled = pending || !current();
  };
  let modal;
  lower.control.addEventListener('change', explain); denominator.addEventListener('change', explain);
  unit.addEventListener('change', () => {
    if (unit.value === 'percent' && current()?.unit !== 'percent') denominator.value = suggestedBase;
    explain();
  });
  mode.addEventListener('change', () => {
    const saved = current();
    unit.value = saved?.unit || (mode.value === 'widget' ? 'percent' : 'count');
    denominator.value = saved?.denominatorKey || suggestedBase;
    warning.value = saved?.warningValue ?? ''; critical.value = saved?.criticalValue ?? '';
    lower.control.checked = saved?.lowerIsBetter !== false; error.hidden = true; explain();
  });
  const actions = [{ label: 'Annuler', action: close => { if (!pending) close(); } },
    { label: 'Enregistrer les seuils', primary: true, action: close => apply(false, close) },
    { label: 'Supprimer ces seuils', danger: true, disabled: !current(), attributes: { 'data-weather-selection-remove': '' }, action: close => apply(true, close) }];
  modal = dialog('Seuils pour cette sélection', form, actions, { returnFocus: trigger }); explain();
  modal.element.classList.add('weather-selection-dialog');
  modal.element.dataset.weatherSelectionKey = selection.key;
  const onAbort = () => modal.close();
  modal.element.addEventListener('cancel', event => { if (pending) { event.preventDefault(); event.stopImmediatePropagation(); } }, true);
  modal.element.addEventListener('pilot:closed', () => { disposed = true; ctx.signal.removeEventListener('abort', onAbort); }, { once: true });
  ctx.signal.addEventListener('abort', onAbort, { once: true });

  async function apply(remove, close) {
    if (pending || disposed || remove && !current()) return;
    error.hidden = true;
    if (!remove && unit.value === 'percent' && selection.percentageAvailable === false) {
      error.textContent = selection.percentageReason || 'Cet indicateur ne permet pas un calcul de part en pourcentage.'; error.hidden = false; return;
    }
    if (!remove && !form.reportValidity()) return;
    const values = { unit: unit.value, denominatorKey: denominator.value || suggestedBase,
      warningValue: Number(warning.value), criticalValue: Number(critical.value), lowerIsBetter: lower.control.checked };
    if (!remove && (!Number.isFinite(values.warningValue) || !Number.isFinite(values.criticalValue)
      || values.warningValue < 0 || values.criticalValue < 0)) {
      error.textContent = 'Saisissez deux seuils numériques positifs ou nuls.'; error.hidden = false; return;
    }
    if (!remove && (values.lowerIsBetter ? values.criticalValue < values.warningValue : values.criticalValue > values.warningValue)) {
      error.textContent = values.lowerIsBetter ? 'Le seuil de tension doit être supérieur ou égal au seuil de vigilance.'
        : 'Le seuil de tension doit être inférieur ou égal au seuil de vigilance.';
      error.hidden = false; return;
    }
    pending = true;
    for (const control of modal.element.querySelectorAll('button,input,select')) control.disabled = true;
    try {
      await ctx.api.call(`dashboard.weatherSelection.${remove ? 'remove' : 'save'}`, {
        runId: model.runId, revision: model.revision, widgetId: widget.id, mode: mode.value, ...(remove ? {} : values)
      }, { signal: ctx.signal });
      if (disposed || ctx.signal.aborted) return;
      close(); await reload();
      ctx.notify(remove ? 'Les seuils personnalisés de cette portée ont été supprimés.' : 'Les seuils sont enregistrés.');
    } catch (reason) {
      if (reason?.name !== 'AbortError' && !disposed) { error.textContent = reason.message; error.hidden = false; }
      else if (reason?.name !== 'AbortError') ctx.notify(reason.message, 'error');
    } finally {
      pending = false;
      for (const control of modal.element.querySelectorAll('button,input,select')) control.disabled = false;
      explain();
    }
  }
  return modal;
}
