'use strict';
// Real production ES modules and CSS, with explicitly synthetic API replies.
// This suite does not replace C# bridge, persistence, or native Windows checks.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const project = path.resolve(__dirname, '../..');
const { chromium } = require(process.env.GLPI_TEST_PLAYWRIGHT_MODULE || require.resolve('playwright', { paths: [path.join(project, 'tests/DomChecks')] }));
let checks = 0, browser, server;
const ok = (condition, label) => { assert.ok(condition, label); console.log(`PASS ${++checks} ${label}`); };
const html = fs.readFileSync(path.join(project, 'web/index.html'), 'utf8')
  .replaceAll('href="css/', 'href="/web/css/').replaceAll('href="vendor/', 'href="/web/vendor/')
  .replace('src="vendor/', 'src="/web/vendor/').replace('<script type="module" src="js/app.js"></script>', '')
  .replace('<div id="app" class="app-loading" role="status">Ouverture de GLPI Pilot…</div>', '<main class="workspace" id="root"></main>');
(async () => {
  try {
    const origin = 'https://glpi-ergonomics.test';
    browser = await chromium.launch({ headless: true, executablePath: process.env.GLPI_TEST_CHROMIUM, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
    const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
    const page = await context.newPage(); page.setDefaultTimeout(6000);
    const errors = []; let external = 0;
    page.on('pageerror', error => errors.push(error.message));
    await context.route('**/*', async route => {
      const url = new URL(route.request().url());
      if (url.origin !== origin) { external++; return route.abort(); }
      if (url.pathname === '/') return route.fulfill({ contentType: 'text/html; charset=utf-8', body: html });
      const file = path.resolve(project, '.' + url.pathname);
      if (!file.startsWith(path.join(project, 'web') + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) return route.fulfill({ status: 404, body: 'Unknown synthetic fixture' });
      return route.fulfill({ contentType: file.endsWith('.js') ? 'text/javascript; charset=utf-8' : file.endsWith('.css') ? 'text/css; charset=utf-8' : 'application/octet-stream', body: fs.readFileSync(file) });
    });
    const reset = async () => {
      if (process.env.GLPI_TEST_OFFLINE_BLOBS === '1') await require('./offline.cjs')(page);
      else { await page.goto(origin); await page.evaluate(() => { window.importProduction = name => import(name); }); }
      await page.evaluate(() => { const root = document.querySelector('#root'); Object.assign(root.style, { height: '100dvh', width: '100%', overflow: 'auto', padding: '16px', boxSizing: 'border-box' }); window.controller = new AbortController(); });
    };
    const capture = async name => {
      if (!process.env.GLPI_TEST_SCREENSHOTS) return;
      fs.mkdirSync(process.env.GLPI_TEST_SCREENSHOTS, { recursive: true });
      await page.screenshot({ path: path.join(process.env.GLPI_TEST_SCREENSHOTS, name + '.png') });
      // Guide figures (guides/LIRE.md): light-theme copy only when GLPI_GUIDE_CAPTURES is set, then the tested theme comes back.
      if (!process.env.GLPI_GUIDE_CAPTURES) return;
      fs.mkdirSync(process.env.GLPI_GUIDE_CAPTURES, { recursive: true });
      const before = await page.evaluate(() => { const root = document.documentElement, theme = root.dataset.theme || ''; root.dataset.theme = 'light'; return theme; });
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      await page.screenshot({ path: path.join(process.env.GLPI_GUIDE_CAPTURES, name + '.png'), animations: 'disabled' });
      await page.evaluate(theme => { if (theme) document.documentElement.dataset.theme = theme; else delete document.documentElement.dataset.theme; }, before);
    };
    await require('./workspace-grid.cjs')({ page, reset, ok, capture });
    if (process.env.GLPI_TEST_GRID_ONLY === '1') {
      ok(errors.length === 0 && external === 0, 'Grid checks use production modules without JavaScript errors or external requests');
      console.log(`${checks}/${checks} grid interface checks passed.`); return;
    }
    await reset();
    await page.evaluate(async () => {
      const { mountMap } = await window.importProduction('/web/js/pages/maps.js'); const { el } = await window.importProduction('/web/js/ui.js');
      const root = document.querySelector('#root'), host = el('div');
      root.append(el('div', { style: { height: '200px' }, text: 'Avant la carte' }), host, el('div', { style: { height: '1400px' }, text: 'Après la carte' }));
      window.mapController = mountMap({ signal: controller.signal, api: { call: async () => ({ enabled: false }) } }, {
        title: 'Carte synthétique', token: 'fixture', routes: [{ id: 'r', title: 'Lieux fictifs', ordered: true, places: [
          { id: 'a', label: 'Site A', latitude: 44, longitude: 5, precision: 'Site', sequence: 1 },
          { id: 'b', label: 'Site B', latitude: 44.1, longitude: 5.1, precision: 'Site', sequence: 2 }
        ] }]
      }, host, { view: { streets: false } });
    });
    const canvas = page.locator('.maps-canvas');
    let zoom = await page.evaluate(() => mapController.getView().zoom);
    await canvas.hover(); await page.mouse.wheel(0, 180); await page.waitForTimeout(150);
    ok(await page.evaluate(() => document.querySelector('#root').scrollTop) > 100 && await page.evaluate(() => mapController.getView().zoom) === zoom, 'Wheel over an embedded map scrolls its page without zooming');
    const plus = page.locator('.leaflet-control-zoom-in');
    await plus.hover(); const beforeControl = await page.evaluate(() => document.querySelector('#root').scrollTop);
    await page.mouse.wheel(0, 90); await page.waitForTimeout(150);
    ok(await page.evaluate(() => document.querySelector('#root').scrollTop) > beforeControl && await page.evaluate(() => mapController.getView().zoom) === zoom, 'Wheel over a map zoom control also releases page scrolling');
    await plus.click(); ok(await page.evaluate(() => mapController.getView().zoom) === zoom + 1, 'Explicit zoom buttons still work');
    await page.locator('[data-map-action=expand]').click();
    ok(await page.locator('dialog.maps-reading-focus').count() === 1, 'An explicit enlarged map uses the same live map');
    zoom = await page.evaluate(() => mapController.getView().zoom);
    await page.locator('dialog .maps-canvas').hover(); await page.mouse.wheel(0, -160); await page.waitForTimeout(180);
    ok(await page.evaluate(() => mapController.getView().zoom) > zoom, 'Wheel zoom is enabled only in the enlarged map');
    await page.keyboard.press('Escape');
    await page.waitForFunction(() => !document.querySelector('dialog') && document.activeElement?.dataset.mapAction === 'expand');
    ok(await page.locator('dialog').count() === 0 && await page.evaluate(() => document.activeElement?.dataset.mapAction) === 'expand', 'Escape returns focus to the map enlargement button');
    const afterZoom = await page.evaluate(() => mapController.getView().zoom);
    await canvas.hover(); await page.mouse.wheel(0, 80); await page.waitForTimeout(120);
    ok(await page.evaluate(() => mapController.getView().zoom) === afterZoom, 'Closing enlargement restores the non-capturing embedded wheel policy');
    await page.locator('[data-map-action=list]').click();
    ok(await page.locator('.maps-place-list').isVisible() && await page.locator('.maps-place').count() === 2, 'The optional list retains all original places');
    for (const width of [1366, 1024, 390]) {
      await page.setViewportSize({ width, height: 800 });
      ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `Map/list controls do not overflow the page at ${width}px`);
    }
    await capture('map-compact-list');
    await page.evaluate(() => controller.abort());
    ok(await page.evaluate(() => !document.querySelector('.maps-canvas')._leaflet_id), 'Aborting the page releases the Leaflet instance');

    await page.setViewportSize({ width: 1280, height: 800 }); await reset();
    await page.evaluate(async () => {
      const { bindWorkspaceGrid } = await window.importProduction('/web/js/pages/workspace-grid-ui.js');
      const { el, table } = await window.importProduction('/web/js/ui.js');
      const root = document.querySelector('#root');root.classList.add('pilot-workspace');root.dataset.layoutEditing = 'false';
      const grid = el('div', { class: 'pilot-widget-grid' });
      window.board = { widgets: [{ id: 'w', span: 6, height: 220, autoHeight: false, section: 'main', gridX: 0, gridY: 0 }] };window.saved = 0; window.picked = null;
      const rows = Array.from({ length: 25 }, (_, i) => ({ id: i + 1, title: `Ticket fictif ${i + 1}` }));
      const body = el('div', { class: 'analytics-widget-body' },table(rows,[{key:'id',label:'ID'},{key:'title',label:'Titre'}],{onSelect:row=>{window.picked=row.id;}}));
      window.originalBody=body;
      const card = el('article', { class: 'pilot-widget analytics-widget', dataset: { widgetId: 'w', autoHeight: 'false' } },el('div',{class:'analytics-widget-heading'},el('h3',{text:'Tickets fictifs'})),body);
      grid.append(card);const host=el('div',{class:'pilot-composition'},el('section',{dataset:{sectionKey:'main'}},el('div',{class:'pilot-grid-viewport'},grid)));
      root.append(el('div',{style:{height:'100px'}}),host,el('div',{style:{height:'1400px'}}));
      window.gridController=bindWorkspaceGrid({host,getBoard:()=>board,canEdit:()=>false,persist:()=>{window.saved++;},notify:()=>{},signal:controller.signal});
    });
    await page.waitForSelector('[data-grid-ready=true]');
    ok(await page.locator('.pilot-widget tbody tr:visible').count() === 3, 'Small summary shows three rows without deleting the remaining rows');
    ok((await page.locator('.workspace-reading-caption').innerText()).includes('22'), 'The preview explicitly announces additional loaded content');
    ok(await page.locator('.analytics-widget-body').evaluate(node=>getComputedStyle(node).overflowY==='hidden'), 'The bounded preview has no inner vertical scrollbar; enlargement exposes the complete content');
    ok(await page.evaluate(async () => {
      const { passiveWidget } = await window.importProduction('/web/js/pages/presentation-layout.js');
      const original = document.querySelector('.pilot-widget'), copy = passiveWidget(original);
      return copy.querySelectorAll('tbody tr:not([hidden])').length === 25
        && !copy.querySelector('[data-reading-hidden],.workspace-reading-footer')
        && !copy.hasAttribute('data-reading-size') && !copy.hasAttribute('data-reading-limited')
        && original.querySelectorAll('[data-reading-hidden]').length === 22;
    }), 'Presentation clones retain all loaded rows without changing the compact live widget');
    await page.locator('.pilot-widget tbody tr:visible').last().focus(); await page.keyboard.press('ArrowDown');
    ok(await page.evaluate(()=>picked===null&&document.activeElement?.dataset.widgetExpand==='w'), 'Keyboard navigation stops at the summary boundary without selecting a hidden ticket');
    await page.locator('[data-widget-expand=w]').click();
    ok(await page.locator('dialog tbody tr:visible').count() === 25 && await page.evaluate(()=>document.querySelector('dialog .analytics-widget-body')===originalBody), 'Enlargement shows every loaded row and reuses the original event-bearing DOM');
    await page.locator('dialog tbody tr').nth(20).click();
    ok(await page.evaluate(()=>picked) === 21, 'Row actions remain wired in the enlarged widget');
    await page.keyboard.press('Escape');
    ok(await page.locator('.pilot-widget tbody tr:visible').count()===3 && await page.evaluate(()=>saved)===0 && await page.evaluate(()=>board.widgets[0].height)===220, 'Closing restores the compact preview without changing saved geometry');
    await page.locator('[data-widget-expand=w]').click(); await page.evaluate(()=>controller.abort());
    ok(await page.locator('dialog').count()===0, 'A page abort also closes an enlarged widget without leaving an orphan dialog');

    await reset();
    await page.evaluate(async()=>{
      const {renderMaterialEditor}=await window.importProduction('/web/js/pages/field-preparation-editor.js');const {el}=await window.importProduction('/web/js/ui.js');
      const root=document.querySelector('#root');const body=el('div',{class:'field-modal-body'});const modal=el('dialog',{class:'field-modal'},body);root.append(modal);modal.showModal();
      window.savedPlan=null;window.dirty=0;window.materialErrors=[];
      const catalogue={categories:[{id:'cat',name:'Accessoires',archived:false}],references:[{id:'keyboard',categoryId:'cat',label:'Clavier',code:'KEY',unit:'piece',note:'Vérifier le connecteur.',archived:false}]};
      renderMaterialEditor({root:body,signal:controller.signal,state:{identity:{label:'Technicien fictif'}},model:{token:'token',catalogue,document:{participantLabels:[],plan:{title:'Intervention fictive',state:'draft',noMaterialsRequired:false,lines:[{id:'l1',referenceId:'keyboard',label:'Clavier',category:'Accessoires',code:'KEY',unit:'piece',quantity:2,preparedQuantity:1,preparedBy:'',note:'Vérifier le connecteur.'},{id:'l2',referenceId:'cable',label:'Câble',category:'Réseau',code:'CBL',unit:'metre',quantity:2.5,preparedQuantity:0,preparedBy:'',note:''}]}}},
        api:window.materialApi={call:async(op,args)=>{if(op==='field.save'){window.savedPlan=structuredClone(args.plan);return{saved:true};}if(op==='field.kits')return{suggestions:[]};throw new Error(op);}},markDirty:()=>{dirty++;},onSaved:()=>{},onCancel:()=>{},onError:e=>materialErrors.push(e.message)});
    });
    ok(await page.locator('.field-line-details[open]').count()===0 && await page.getByLabel('Quantité prévue : Clavier',{exact:true}).isVisible(), 'Material quantities are visible while metadata starts folded');
    await page.locator('[data-field-ready=l1]').click();
    ok(await page.getByLabel('Quantité préparée : Clavier',{exact:true}).inputValue()==='2' && await page.locator('[data-field-ready=l1]').isDisabled(), 'One-line preparation sets only that line and disables the redundant action');
    await page.locator('[data-field-action=save-plan]').click();
    ok(await page.evaluate(()=>savedPlan.lines[0].preparedQuantity===2&&savedPlan.lines[1].preparedQuantity===0&&savedPlan.lines[1].quantity===2.5), 'A piece line never changes metre quantities or the other line');
    await page.getByRole('button',{name:'Tout marquer préparé',exact:true}).click();
    await page.getByRole('button',{name:'Annuler',exact:true}).click();
    await page.locator('[data-field-action=save-plan]').click();
    ok(await page.evaluate(()=>savedPlan.lines[1].preparedQuantity===0), 'Cancelling bulk preparation does not modify any quantities');
    await page.getByRole('button',{name:'Tout marquer préparé',exact:true}).click();
    await page.getByRole('button',{name:'Marquer les lignes préparées',exact:true}).click();
    await page.locator('[data-field-action=save-plan]').click();
    ok(await page.evaluate(()=>savedPlan.lines[1].preparedQuantity===2.5), 'Confirmed bulk preparation preserves the decimal unit and still requires save');
    await page.getByLabel('Chercher du matériel',{exact:true}).fill('clavier');
    await page.getByLabel('Quantité à ajouter',{exact:true}).fill('3');
    await page.getByLabel('Quantité à ajouter',{exact:true}).press('Enter');
    ok(await page.locator('[data-field-line]').count()===3, 'Search with a unique active reference and Enter adds a line without navigating away');
    for(const [width,height] of [[1280,800],[1366,768],[1093,614],[390,800]]){
      await page.setViewportSize({width,height});
      ok(await page.locator('.field-modal').evaluate(node=>node.scrollWidth<=node.clientWidth+1)&&await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),`Material editor remains bounded at ${width}×${height}`);
    }
    await capture('material-compact');
    ok(await page.evaluate(()=>materialErrors.length)===0,'Material interactions have no application errors');
    await page.evaluate(()=>{
      window.materialSaveCalls=0;window.pendingMaterial=null;
      materialApi.call=(op,args)=>{if(op!=='field.save')throw new Error(op);materialSaveCalls++;return new Promise((resolve,reject)=>{pendingMaterial={resolve,reject,args};});};
      const save=document.querySelector('[data-field-action=save-plan]');save.click();save.click();
    });
    await page.waitForFunction(()=>materialSaveCalls===1);
    ok(await page.evaluate(()=>materialSaveCalls===1&&document.querySelector('.field-material-editor').getAttribute('aria-busy')==='true')&&await page.getByLabel('Quantité prévue : Clavier',{exact:true}).first().isDisabled(), 'Rapid save clicks send once and disable editing while the captured preparation is in flight');
    await page.evaluate(()=>pendingMaterial.reject(new Error('Synthetic save refusal')));
    await page.waitForFunction(()=>document.querySelector('.field-material-editor').getAttribute('aria-busy')===null);
    ok(await page.getByLabel('Quantité prévue : Clavier',{exact:true}).first().inputValue()==='2'&&!await page.getByLabel('Quantité prévue : Clavier',{exact:true}).first().isDisabled()&&await page.evaluate(()=>materialErrors.at(-1)==='Synthetic save refusal'), 'A failed save restores editable controls and retains the unsent quantities');

    await page.setViewportSize({width:1280,height:800});await reset();
    await page.evaluate(async()=>{
      const {renderFieldPreparation}=await window.importProduction('/web/js/pages/field-preparation.js');
      window.fieldCalls=[];window.notifications=[];window.rowRevision=1;
      const rows=()=>[{id:'a',title:'Préparation A',revision:rowRevision,readiness:'partial',conflicts:0,participantLabels:[]},{id:'b',title:'Préparation B',revision:1,readiness:'ready',conflicts:0,participantLabels:[]}];
      window.fieldCleanup=await renderFieldPreparation({root:document.querySelector('#root'),signal:controller.signal,state:{session:{testMode:true},settings:{}},params:{},navigate:()=>{},notify:message=>notifications.push(message),
        api:window.fieldApi={call:async(op,args)=>{fieldCalls.push({op,args:structuredClone(args)});if(op==='field.options')return{sites:[],technicians:[],teams:[],tours:[{id:'tour',label:'Tournée fictive'}]};if(op==='field.get')return{revision:7,rows:rows(),total:2,summary:{planCount:2,partlyPreparedCount:1,readyCount:1,materials:[]},sync:{}};throw new Error(op);}}});
    });
    ok(!await page.locator('.field-selection-bar').isVisible()&&!await page.locator('.field-advanced-filters').getAttribute('open'), 'Advanced filters and selection commands do not crowd the initial page');
    await page.locator('[data-field-select=a]').check();await page.locator('[data-field-select=b]').check();
    ok(await page.locator('.field-selection-bar').isVisible(),'Selecting a preparation reveals the contextual output commands');
    await page.evaluate(()=>{rowRevision=2;});await page.getByRole('button',{name:'Actualiser',exact:true}).click();
    ok(!await page.locator('[data-field-select=a]').isChecked()&&await page.locator('[data-field-select=b]').isChecked()&&await page.evaluate(()=>notifications.some(x=>x.includes('a changé'))), 'Refresh keeps unchanged selections but explicitly removes stale revisions');
    await page.getByLabel('Du',{exact:true}).fill('2026-11-17');await page.getByLabel('Au, inclus',{exact:true}).fill('2026-11-17');
    await page.getByRole('button',{name:'Appliquer',exact:true}).click();
    ok(await page.evaluate(()=>fieldCalls.filter(x=>x.op==='field.get').at(-1).args.to)==='2026-11-18','An inclusive one-day UI range is translated to the unchanged exclusive backend contract');
    await page.getByRole('button',{name:'Partiellement préparées : 1. Filtrer',exact:true}).click();
    ok(await page.evaluate(()=>fieldCalls.filter(x=>x.op==='field.get').at(-1).args.readiness)==='partial','A manager counter opens the actual corresponding filter');
    await page.locator('[data-field-action=reset-filters]').click();
    ok(await page.evaluate(()=>{const a=fieldCalls.filter(x=>x.op==='field.get').at(-1).args;return a.readiness===''&&!a.from&&!a.to;})&&await page.locator('[data-field-select=b]').isChecked()===false,'Reset clears visible and folded criteria and never retains a stale selection');
    await page.getByRole('button',{name:'À préparer : 0. Filtrer',exact:true}).click();
    ok(await page.evaluate(()=>fieldCalls.filter(x=>x.op==='field.get').at(-1).args.readiness)==='to-prepare','The manager can directly filter preparations not yet started');
    await page.locator('[data-field-action=reset-filters]').click();
    await page.evaluate(()=>{
      window.originalFieldCall=fieldApi.call;window.optionsCalls=0;window.releaseOptions=null;
      window.fieldReadsBefore=fieldCalls.filter(x=>x.op==='field.get').length;
      fieldApi.call=(op,args)=>{
        if(op==='field.options'&&++optionsCalls===1)return new Promise(resolve=>{releaseOptions=()=>resolve({sites:[],technicians:[],teams:[],tours:[]});});
        return originalFieldCall(op,args);
      };
    });
    await page.getByRole('button',{name:'Actualiser',exact:true}).click();
    await page.waitForFunction(()=>releaseOptions!==null);
    await page.getByLabel('Périmètre',{exact:true}).selectOption('mine');
    await page.waitForFunction(()=>fieldCalls.filter(x=>x.op==='field.get').length===fieldReadsBefore+1);
    await page.evaluate(()=>releaseOptions());await page.waitForTimeout(80);
    ok(await page.evaluate(()=>fieldCalls.filter(x=>x.op==='field.get').length===fieldReadsBefore+1),'A late options response cannot start a stale workspace read after a newer refresh');
    await page.evaluate(()=>{
      window.releaseSelection=null;
      fieldApi.call=(op,args)=>op==='field.get'&&args.selectAll?new Promise(resolve=>{releaseSelection=()=>resolve({selections:[{id:'a',expectedRevision:rowRevision}]});}):originalFieldCall(op,args);
    });
    await page.locator('[data-field-action=select-all]').click();await page.waitForFunction(()=>releaseSelection!==null);
    await page.locator('[data-field-action=reset-filters]').click();
    await page.evaluate(()=>releaseSelection());await page.waitForTimeout(80);
    ok(!await page.locator('[data-field-select=a]').isChecked()&&!await page.locator('.field-selection-bar').isVisible(),'A select-all response from a previous filter cannot restore a stale selection');
    await page.locator('[data-field-action=select-all]').click();await page.waitForTimeout(50);
    await page.locator('[data-field-select=b]').check();
    await page.evaluate(()=>releaseSelection());await page.waitForTimeout(80);
    ok(await page.locator('[data-field-select=b]').isChecked()&&!await page.locator('[data-field-select=a]').isChecked(),'A delayed select-all result cannot overwrite a newer manual selection');
    await page.evaluate(()=>{fieldApi.call=originalFieldCall;});
    await capture('field-preparation-overview');
    await require('./tickets.cjs')({page,reset,ok,capture});
    await require('./tours.cjs')({page,reset,ok,capture});
    ok(errors.length===0,`Production modules report no JavaScript errors (${errors.join('; ')})`);
    ok(external===0,'Synthetic ergonomics checks make no external requests');
    console.log(`${checks}/${checks} ergonomics checks passed.`);
  } catch(error) { console.error(error);process.exitCode=1; }
  finally { if(browser)await browser.close();if(server)await new Promise(resolve=>server.close(resolve)); }
})();
