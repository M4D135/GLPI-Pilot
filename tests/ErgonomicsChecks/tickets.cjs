'use strict';
module.exports = async function ticketChecks({ page, reset, ok, capture }) {
  await page.setViewportSize({ width: 1280, height: 800 }); await reset();
  await page.evaluate(async () => {
    const { renderActions } = await window.importProduction('/web/js/pages/workplace-actions.js');
    const { dialog } = await window.importProduction('/web/js/ui.js');
    const host = document.createElement('div'); document.querySelector('#root').append(host);
    window.ticketFixture = { personal: { key: 'f'.repeat(64), id: '42', url: 'https://glpi-fixture.invalid/front/ticket.form.php?id=42', followupDraft: 'Brouillon de test non publié' }, canRead: true };
    window.actionCalls = []; window.ticketNavigation = []; window.ticketNotices = []; window.appliedReads = 0;
    window.delayApplied = false; window.receiptState = 'verified'; window.failPreview = false; window.slowPreview = false;
    window.actionState = { canWrite: true, adapters: [], receipts: [], candidates: [{ key: 'followup-1', kind: 'followup', name: 'Suivi reconnu', choices: [], otherFields: 'is_private : Oui' }] };
    let counter = 0;
    const api = { call: async (op, args = {}) => {
      actionCalls.push({ op, args: structuredClone(args) });
      if (op === 'workplace.actions.get') return structuredClone(actionState);
      if (op === 'workplace.actions.inspect') {
        actionState.candidates = [
          { key: 'followup-1', kind: 'followup', name: 'Suivi reconnu', choices: [], otherFields: 'is_private : Oui' },
          { key: 'task-1', kind: 'task', name: 'Tâche reconnue', choices: [], otherFields: 'is_private : Oui' },
          { key: 'assign-1', kind: 'assignment', name: 'Affectation reconnue', choices: [{ value: '2', label: 'Technicien fictif' }], currentValue: '2', otherFields: 'Groupe inchangé' }
        ]; return structuredClone(actionState);
      }
      if (op === 'workplace.actions.preview') {
        if (failPreview) throw new Error('Formulaire devenu indisponible');
        const result = { token: 'preview-' + ++counter, ticket: { id: '42', url: ticketFixture.personal.url }, operation: args.adapterKey, before: '(vide)', after: args.value, otherFields: 'Visibilité privée sur la fixture', expiresAt: new Date(Date.now() + 120000).toISOString() };
        if (slowPreview) await new Promise(resolve => { window.finishPreview = resolve; });
        return result;
      }
      if (op === 'workplace.actions.confirm') {
        const receipt = { id: args.token, state: receiptState, operation: 'Tâche de test', requestedAt: new Date().toISOString(), before: '', after: 'Texte fictif', otherFields: 'Privé', message: receiptState === 'verified' ? 'Résultat relu sur fixture' : 'Résultat inconnu sur fixture, vérifier sans renvoyer.' };
        actionState.receipts.push(receipt); return receipt;
      }
      if (op === 'workplace.ticket.read') return structuredClone(ticketFixture);
      if (op === 'workplace.ticket.browser' || op === 'workplace.actions.invalidate') return {};
      throw new Error('Unexpected synthetic operation ' + op);
    } };
    window.ticketContext = { root: host, signal: controller.signal, state: { session: {}, capabilities: ['field.get', 'calendar.forTicket'] }, api,
      navigate: async (page, args) => ticketNavigation.push({ page, args: structuredClone(args) }), notify: (message, tone) => ticketNotices.push({ message, tone }) };
    window.actionModal = dialog('Actions assistées', host); actionModal.element.classList.add('tickets-tool-modal');
    window.actionCleanup = await renderActions(ticketContext, host, ticketFixture, async () => { appliedReads++; if (delayApplied) await new Promise(resolve => { window.releaseApplied = resolve; }); });
    window.rerenderActions = async () => { await actionCleanup(); actionCleanup = await renderActions(ticketContext, host, ticketFixture, async () => { appliedReads++; if (delayApplied) await new Promise(resolve => { window.releaseApplied = resolve; }); }); };
  });
  const sent = async () => page.evaluate(() => actionCalls.filter(x => x.op === 'workplace.actions.confirm').length);
  ok(await page.getByRole('region', { name: 'Piloter ce ticket', exact: true }).count() === 1 && await page.evaluate(() => actionCalls.length === 1), 'Opening ticket piloting only reads action state; it never collects, inspects or writes implicitly');
  await page.locator('[data-ticket-command=task]').click();
  ok(await page.getByRole('button', { name: 'Prévisualiser l’action…', exact: true }).count() === 0 && await sent() === 0, 'A missing task form never falls back to sending a follow-up');
  await page.getByRole('button', { name: 'Inspecter le formulaire ouvert', exact: true }).click();
  ok(await page.getByLabel('Adaptateur de formulaire', { exact: true }).inputValue() === 'task-1', 'Explicit inspection retains the requested task kind');
  await page.getByLabel('Texte à ajouter', { exact: true }).fill('Diagnostic fictif à relire');
  await page.getByRole('button', { name: 'Prévisualiser l’action…', exact: true }).click();
  ok(await sent() === 0 && await page.getByRole('dialog', { name: 'Relire l’action GLPI', exact: true }).count() === 1, 'Preview shows before/after and never sends the action');
  await page.keyboard.press('Escape');
  await page.waitForFunction(() => ![...document.querySelectorAll('dialog')].some(d => d.getAttribute('aria-label') === 'Relire l’action GLPI'));
  ok(await page.evaluate(() => actionCalls.some(x => x.op === 'workplace.actions.invalidate')) && await sent() === 0, 'Escape invalidates the preview, including a close outside the explicit cancel button');
  await page.getByRole('button', { name: 'Prévisualiser l’action…', exact: true }).click();
  await page.getByRole('button', { name: 'Confirmer l’aperçu et envoyer une fois…', exact: true }).click();
  await page.getByRole('button', { name: 'Annuler', exact: true }).click();
  ok(await sent() === 0, 'Cancelling final confirmation leaves the ticket untouched');
  await page.getByRole('button', { name: 'Confirmer l’aperçu et envoyer une fois…', exact: true }).click();
  await page.getByRole('button', { name: 'Envoyer une fois', exact: true }).click();
  await page.waitForFunction(() => appliedReads === 1);
  ok(await sent() === 1 && await page.evaluate(() => actionCalls.filter(x => x.op === 'workplace.actions.confirm')[0].args.ticketKey === ticketFixture.personal.key), 'Final confirmation sends exactly once with the original validated ticket key and refreshes after a verified receipt');
  await page.evaluate(() => { receiptState = 'unknown'; });
  await page.getByRole('button', { name: 'Prévisualiser l’action…', exact: true }).click();
  await page.getByRole('button', { name: 'Confirmer l’aperçu et envoyer une fois…', exact: true }).click();
  await page.getByRole('button', { name: 'Envoyer une fois', exact: true }).click();
  await page.waitForFunction(() => actionState.receipts.length === 2);
  ok(await page.locator('.work-receipt[open]').count() === 1 && await sent() === 2 && await page.evaluate(() => appliedReads) === 1, 'An unknown outcome stays visibly unresolved and never auto-retries or pretends to refresh successfully');
  await page.evaluate(() => { failPreview = true; });
  await page.getByRole('button', { name: 'Prévisualiser l’action…', exact: true }).click();
  ok(await page.getByLabel('Texte à ajouter', { exact: true }).inputValue() === 'Diagnostic fictif à relire' && await sent() === 2, 'A refused preview preserves the unsent text');
  await page.evaluate(() => { failPreview = false; slowPreview = true; });
  const pending = page.getByRole('button', { name: 'Prévisualiser l’action…', exact: true }).click(); await pending;
  await page.waitForFunction(() => !!window.finishPreview);
  await page.getByLabel('Texte à ajouter', { exact: true }).fill('Texte modifié pendant la lecture');
  await page.evaluate(() => { slowPreview = false; finishPreview(); });
  await page.waitForFunction(() => ticketNotices.some(n => n.message.includes('saisie a changé')));
  ok(await page.getByRole('dialog', { name: 'Relire l’action GLPI', exact: true }).count() === 0 && await sent() === 2, 'Text edited during a delayed preview rejects that stale preview without sending');
  await page.locator('.ticket-control-local > summary').click();
  for (const command of ['plan', 'materials', 'personal', 'draft']) await page.locator(`[data-ticket-command=${command}]`).click();
  ok(await page.evaluate(() => ticketNavigation.length === 4 && ticketNavigation.every(x => (x.args.ticketKey || x.args.fieldTicketKey) === ticketFixture.personal.key) && ticketNavigation[1].args.tab === 'preparation') && await sent() === 2, 'Local planning, material, notes and draft shortcuts retain the exact ticket and make no GLPI write');
  await page.locator('[data-ticket-command=native]').click();
  ok(await page.evaluate(() => actionCalls.filter(x => x.op === 'workplace.ticket.browser').length === 1 && ticketNotices.some(n => n.message.includes('aucune modification'))) && await sent() === 2, 'Status/priority/resolution command opens the native GLPI ticket and never submits a form');
  for (const [width, height] of [[1280, 800], [1093, 614], [390, 800]]) {
    await page.setViewportSize({ width, height });
    ok(await page.locator('dialog').evaluate(node => node.scrollWidth <= node.clientWidth + 1) && await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `Ticket commands remain bounded at ${width}×${height}`);
  }
  await capture('ticket-piloting');
  await page.evaluate(async () => { actionState.canWrite = false; ticketFixture.canRead = false; await rerenderActions(); });
  ok(await page.getByRole('button', { name: 'Prévisualiser l’action…', exact: true }).isDisabled() && await page.locator('[data-ticket-command=native]').isDisabled(), 'Unavailable live source disables both submission preview and native ticket opening');
  await page.evaluate(async () => { actionState.canWrite = true; ticketFixture.canRead = true; await rerenderActions(); delayApplied = true; receiptState = 'verified'; });
  await page.getByRole('button', { name: 'Prévisualiser l’action…', exact: true }).click();
  await page.getByRole('button', { name: 'Confirmer l’aperçu et envoyer une fois…', exact: true }).click();
  await page.getByRole('button', { name: 'Envoyer une fois', exact: true }).click();
  await page.waitForFunction(() => !!window.releaseApplied);
  await page.evaluate(() => { window.cleanupFinished = false; window.cleanupError = ''; window.waitingCleanup = actionCleanup().then(() => { cleanupFinished = true; }).catch(error => { cleanupError = error.message; }); });
  ok(!await page.evaluate(() => cleanupFinished) && await page.evaluate(() => cleanupError) === '', 'Closing during verified-ticket refresh waits instead of discarding the close request');
  await page.evaluate(async () => { releaseApplied(); await waitingCleanup; });
  ok(await page.evaluate(() => cleanupFinished && !cleanupError) && await sent() === 3, 'A deferred close completes after the single submission and final state read without a repeated send');
  await page.evaluate(() => { actionModal.close(); });
};
