import { el, button, card, toolbar, badge, form, confirm, formatDate, formatNumber, isSimulation } from '../ui.js';
import { page, facts } from './terrain-common.js';
import { sharedUpdatePanels } from './terrain-update-shared.js';

export async function renderUpdates(ctx) {
  const p = page(ctx, 'Mise à jour et retour arrière', isSimulation(ctx.state.session)?'Essayez la préparation, l’installation et le retour arrière avec des packs fictifs. La version affichée évolue dans la simulation.':'Choisissez un dossier réseau ou un ZIP local, vérifiez le pack, puis décidez de son installation. Les patchs de sites se diffusent séparément.');
  let state = await p.call('update.state'), prepared = state.prepared;
  const simulation=state.simulation===true||isSimulation(ctx.state.session), label=(normal,simulated)=>simulation?simulated:normal;
  const summary = el('div'), archiveInfo = el('div'), review = el('div');
  const references = form([{ key: 'sha256', label: 'SHA-256 attendu de l’archive', required: true, maxLength: 64, help: 'Empreinte provenant d’une publication de confiance.' },
    { key: 'commit', label: 'Commit attendu', required: true, maxLength: 40, help: 'Identifiant complet de 40 caractères hexadécimaux.' }], { sha256: '', commit: '' });
  const policy = form([{ key: 'requireSignature', label: 'Exiger une signature Authenticode de l’éditeur approuvé', type: 'checkbox' },
    { key: 'trustedPublisherSha256', label: 'SHA-256 du certificat d’éditeur approuvé', maxLength: 64, help: 'À obtenir par une source de confiance indépendante.' }], state.policy);
  let policyInitial = JSON.stringify(policy.read());
  const install = button(label('Fermer GLPI Pilot et installer la version vérifiée','Simuler l’installation de cette version'), p.act(async () => {
    ensurePolicySaved(); if (!prepared) throw new Error('Vérifiez un pack avant de demander son installation.');
    if (!await confirm(prepared.isRollback ? 'Installer la version conservée' : 'Installer le pack vérifié',
      simulation?`La version ${prepared.version} deviendra la version de simulation. Vous pourrez ensuite essayer le retour arrière depuis cet écran.`:`GLPI Pilot va se fermer pour installer la version ${prepared.version}. Une sauvegarde vérifiée des binaires sera conservée. Si le démarrage de la nouvelle version échoue, l’auxiliaire tentera de restaurer le pack précédent.`, label('Fermer et installer','Appliquer la simulation'))) return;
    const result = await p.call('update.install', { token: prepared.token, version: prepared.version, confirmed: true });
    if (result.closing) p.message('Installation lancée ; fermeture de GLPI Pilot…');
    else if(result.simulation){state=result.state||await p.call('update.state');prepared=state.prepared;paintSummary();paintArchive();paintPrepared();p.message(`Installation simulée terminée : version ${result.version||state.version}. Le retour arrière est disponible.`);}
  }), { primary: true, disabled: !prepared || !state.windows });
  const shared = sharedUpdatePanels(p, { simulation, administration: state.administration, sharedFolder: state.sharedFolder, ensurePolicySaved,
    clearPrepared: () => { prepared = null; paintPrepared(); }, setPrepared: value => { prepared = value; paintPrepared(); } });
  p.body.append(summary,
    shared.network, shared.sites,
    card(label('Préparer un pack ZIP local','Préparer un pack de démonstration'), toolbar(button(label('Choisir le ZIP…','Utiliser le pack de démonstration'), p.act(async () => {
      const result = await p.call('update.selectArchive'); if (result.cancelled) return;
      state.archive = { name: result.name }; prepared = null;
      references.controls.get('sha256').control.value = result.simulation?result.sha256||'':''; references.controls.get('commit').control.value = result.simulation?result.commit||'':'';
      paintArchive(); paintPrepared();
    }))), archiveInfo, references.element,
      button('Vérifier le ZIP local', p.act(async () => { ensurePolicySaved(); prepared = null; paintPrepared(); prepared = await p.call('update.prepareLocal', references.read()); paintPrepared(); p.message('Tous les fichiers du pack ont été contrôlés. L’installation reste à votre initiative.'); }))),
    card('Confiance dans l’éditeur', policy.element,
      el('p', { text: 'La signature est facultative tant qu’aucun certificat de confiance n’est fourni. L’empreinte du fichier vérifie son intégrité ; l’état réel de signature de l’exécutable est affiché séparément dans le bilan.' }),
      button('Enregistrer la politique de confiance', p.act(async () => { state = await p.call('update.policy', { policy: policy.read() }); policyInitial = JSON.stringify(policy.read()); prepared = null; paintPrepared(); p.message('Politique enregistrée. Vérifiez de nouveau le pack avant de l’installer.'); }))),
    card('Pack préparé', review, toolbar(install,
      button('Préparer le retour à la version conservée', p.act(async () => {
        ensurePolicySaved(); if (!await confirm('Préparer un retour arrière', 'La dernière sauvegarde des binaires de cette installation sera vérifiée. L’installation du retour arrière attendra ensuite votre confirmation.', 'Vérifier la sauvegarde')) return;
        prepared = null; paintPrepared(); prepared = await p.call('update.prepareRollback', { confirmed: true }); paintPrepared(); p.message('Retour arrière préparé à partir de la sauvegarde vérifiée.');
      }))),
      el('p', { class: 'terrain-muted', text: label('L’auxiliaire attend la fermeture de l’application, conserve l’ancien pack, remplace les binaires et vérifie le démarrage réel de WebView. Les données et préférences locales restent dans leur emplacement habituel. Une erreur de disque ou des fichiers maintenus ouverts peut nécessiter une récupération manuelle depuis la sauvegarde indiquée.','L’installation simulée actualise la version et conserve un bilan dans cet espace. Le retour arrière permet de retrouver la version simulée précédente.') })));

  function ensurePolicySaved() { if (JSON.stringify(policy.read()) !== policyInitial) throw new Error('Enregistrez la politique de confiance modifiée avant de préparer ou d’installer un pack.'); }
  function paintArchive() { archiveInfo.replaceChildren(el('p', { text: state.archive?.name ? `Archive choisie : ${state.archive.name}` : 'Aucune archive locale choisie.' })); }
  function paintPrepared() {
    install.disabled = !prepared || !state.windows;
    review.replaceChildren(prepared ? el('div', {}, badge(prepared.isRollback ? 'Retour arrière préparé' : label('Pack vérifié','Pack fictif préparé'), 'ok'),
      facts([['Version', prepared.version], ['Édition', prepared.edition], ['Commit', prepared.commit], ['Fichiers contrôlés', prepared.files], ['Signature', prepared.signature.description], ['Certificat SHA-256', prepared.signature.certificateSha256], ['Sauvegarde prévue', prepared.backup]]),
      el('p', { text: label('La version en cours reste inchangée jusqu’au clic de fermeture et d’installation.','Appliquez l’installation pour mettre à jour la version de simulation.') })) : el('p', { text: 'Choisissez un pack puis lancez sa vérification pour obtenir le bilan complet avant installation.' }));
  }
  references.element.addEventListener('input', () => { prepared = null; paintPrepared(); });
  policy.element.addEventListener('input', () => { prepared = null; paintPrepared(); });
  function paintSummary(){
    summary.replaceChildren(facts([[label('Version installée','Version de simulation'), state.version], ...(simulation&&state.actualVersion?[['Version de l’application',state.actualVersion]]:[]), ['Installation', state.installRoot]]));
    if (!state.windows) summary.append(el('p', { class: 'terrain-warning', text: 'Le contrôle et l’installation Authenticode nécessitent Windows. Cet environnement permet la vérification des moteurs et des fixtures hors ligne.' }));
    if (state.outcome) summary.append(card('Dernière opération', facts([['Date', formatDate(state.outcome.at)], ['État', state.outcome.state], ['Version', state.outcome.version], ['Bilan', state.outcome.detail], ['Sauvegarde', state.outcome.backupPath]])));
  }
  p.on('update.progress', value => p.message(value.total ? `${value.message} ${formatNumber(value.bytes / 1048576, { maximumFractionDigits: 1 })} / ${formatNumber(value.total / 1048576, { maximumFractionDigits: 1 })} Mio` : value.message));
  paintSummary(); paintArchive(); paintPrepared(); return () => p.cleanup();
}
