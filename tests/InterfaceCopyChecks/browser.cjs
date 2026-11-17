'use strict';
// Production UI modules with explicit synthetic replies. No corporate GLPI data.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const root=path.resolve(__dirname,'../..');
const {chromium}=require(process.env.GLPI_TEST_PLAYWRIGHT_MODULE||require.resolve('playwright',{paths:[path.join(root,'tests/DomChecks')]}));
const output=process.env.GLPI_TEST_SCREENSHOTS||path.join(root,'artifacts/interface-copy');
fs.mkdirSync(output,{recursive:true});
let count=0;
const ok=(condition,label)=>{assert.ok(condition,label);console.log(`PASS ${++count} ${label}`);};
(async()=>{
 const browser=await chromium.launch({headless:true,executablePath:process.env.GLPI_TEST_CHROMIUM,args:['--no-sandbox','--disable-dev-shm-usage']});
 try{
  const page=await browser.newPage({viewport:{width:1440,height:1050},reducedMotion:'reduce'});page.setDefaultTimeout(6000);
  const errors=[];page.on('pageerror',error=>errors.push(error.message));
  const reset=async()=>{await require('../ErgonomicsChecks/offline.cjs')(page);await page.evaluate(()=>{window.controller=new AbortController();document.documentElement.dataset.motion='reduce';Object.assign(document.querySelector('#root').style,{height:'100dvh',overflow:'auto',padding:'20px'});});};
  const capture=async name=>{await page.screenshot({path:path.join(output,name+'.png'),animations:'disabled'});};
  await require('../ErgonomicsChecks/hypermotion.cjs')({page,reset,ok,capture});
  await reset();
  await page.evaluate(async()=>{
   const settings={theme:'dark',interfaceZoomPercent:100,exportFolder:'C:\\Demo\\Exports',sources:[],activeSourceId:'',dashboard:{personalTechnician:''},
    interface:{density:'compact',accent:'blue',colorVision:'standard',reduceMotion:true,followWindowsTheme:false,followWindowsMotion:false,hiddenNavigation:[],frozenColumns:0},
    ticketDetails:{sections:[],maxRowsPerSection:500},history:{enabled:true,retentionDays:90,maximumSnapshots:100,storageBudgetMb:512},automation:{},library:{},updates:{}};
   const state={settings,session:{loaded:false,revision:7,rows:0},version:'4.0.0'};
   window.copyCalls=[];window.copyErrors=[];
   const api={on:()=>()=>{},call:async(op,args)=>{copyCalls.push({op,args});
    if(op==='settings.get')return structuredClone(settings);
    if(op==='deployment.state')return{simulation:false,fileName:'configuration.json',defaultPath:'C:\\Demo\\wad\\configuration.json',seedStatus:'none',seedMessage:'Aucune configuration par défaut.'};
    if(op==='app.appearance')return{};
    throw new Error('Unexpected synthetic operation '+op);
   }};
   const {renderSettingsWorkspace}=await window.importProduction('/web/js/pages/settings-workspace.js');
   window.settingsDispose=await renderSettingsWorkspace({root:document.querySelector('#root'),state,api,signal:controller.signal,params:{},pages:[],notify:message=>copyErrors.push(message),navigate:()=>{},exitSimulation:()=>{}});
  });
  ok(await page.locator('.cfg-hero h2').innerText()==='Configuration','Settings uses a functional title');
  ok(await page.locator('.cfg-hero-main > p,.cfg-pane-titles > p').count()===0,'Settings promotional banner and repetitive section leads are removed');
  const sections=await page.locator('[data-settings-nav]').evaluateAll(nodes=>nodes.map(n=>n.dataset.settingsNav));
  for(const section of sections){
   await page.locator(`[data-settings-nav="${section}"]`).click();
   ok(await page.locator(`[data-settings-pane="${section}"]`).isVisible(),`Settings section remains usable: ${section}`);
   const text=await page.locator('#root').innerText();
   ok(!/(?<![\p{L}\p{N}_-])(?:vous|votre|vos|bonjour|bienvenue|tu)(?![\p{L}\p{N}_-])/iu.test(text),`Neutral copy in ${section}`);
  }
  await page.locator('[data-settings-nav=apparence]').click();
  await page.locator('#root').evaluate(node=>node.scrollTop=0);await capture('settings-neutral-dark');
  await page.locator('[data-settings-nav=collecte]').click();
  ok(await page.getByText('Attente de connexion / SSO (secondes)',{exact:true}).isVisible(),'SSO field help and actual controls remain reachable');
  await page.locator('[data-settings-nav=apparence]').click();
  const input=page.getByRole('combobox',{name:'Rechercher un réglage',exact:true});await input.fill('archives');
  ok(await page.locator('#cfg-search-list [role=option]').count()>0,'Settings search still finds retention controls');
  await input.fill('');
  for(const width of [1280,390]){
   await page.setViewportSize({width,height:900});
   ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),`Settings does not overflow at ${width}px`);
  }
  await page.setViewportSize({width:1440,height:1050});await page.evaluate(()=>document.documentElement.dataset.theme='light');
  await capture('settings-neutral-light');
  ok(await page.evaluate(async()=>{const{el}=await window.importProduction('/web/js/ui.js');const literal='Bonjour, votre ticket contient « vous » : texte utilisateur à conserver.';return el('p',{text:literal}).textContent===literal;}),'User-provided content is not rewritten by the UI helpers');
  ok(errors.length===0,'No unhandled browser errors');
  ok(await page.evaluate(()=>copyErrors.length===0),'No hidden settings errors');
  await page.evaluate(()=>settingsDispose());
  fs.writeFileSync(path.join(output,'verification.json'),JSON.stringify({checks:count,mode:'Local Chromium, production UI modules, synthetic replies; no production CSP or Windows host',errors},null,2));
  console.log(`${count}/${count} neutral interface checks passed.`);
 }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
