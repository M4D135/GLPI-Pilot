import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
function files(directory,extension){return fs.readdirSync(directory,{withFileTypes:true}).flatMap(entry=>entry.isDirectory()?files(path.join(directory,entry.name),extension):entry.name.endsWith(extension)?[path.join(directory,entry.name)]:[]);}
const address=/(?<![\p{L}\p{N}_-])(?:vous|votre|vos|bonjour|bienvenue|tu|ton|tes)(?![\p{L}\p{N}_-])/iu;
const read=name=>fs.readFileSync(path.join(root,name),'utf8');
test('French interface copy uses neutral labels rather than addressing the operator',()=>{
 const matches=files(path.join(root,'web/js'),'.js').flatMap(file=>fs.readFileSync(file,'utf8').split('\n').flatMap((line,index)=>address.test(line)?[`${path.relative(root,file)}:${index+1}: ${line}`]:[]));
 assert.deepEqual(matches,[]);
});
test('Promotional headings are absent and functional headings stay accessible',()=>{
 const all=files(path.join(root,'web/js'),'.js').map(file=>fs.readFileSync(file,'utf8')).join('\n');
 for(const copy of ['Votre journée en un regard','Un outil à votre manière','Comprendre. Prioriser. Agir.','Retrouver, comprendre et partager'])assert.ok(!all.includes(copy),copy);
 assert.ok(read('web/js/pages/home-workspace.js').includes("el('h2',{text:'Activité'})"));
 assert.ok(read('web/js/pages/settings-workspace.js').includes("el('h2', { text: 'Configuration' })"));
 assert.ok(!read('web/js/app.js').includes('page-description'));
 assert.ok(!read('web/js/pages/settings-workspace.js').includes('section.lead'));
});
test('Native and runtime messages are neutral; analysis vocabulary and portal recognition are not rewritten',()=>{
 const exempt=new Set(['Core/BrowserNavigation.cs','Core/TicketLibraryResolutionService.cs','Core/LanguageAssistantService.cs']);
 const base=path.join(root,'src/GlpiPilot');
 const matches=files(base,'.cs').filter(file=>!exempt.has(path.relative(base,file).split(path.sep).join('/'))).flatMap(file=>fs.readFileSync(file,'utf8').split('\n').flatMap((line,index)=>address.test(line)?[`${path.relative(base,file)}:${index+1}`]:[]));
 assert.deepEqual(matches,[]);
 assert.ok(read('src/GlpiPilot/Core/BrowserNavigation.cs').includes('votre compte'));
 assert.ok(read('src/GlpiPilot/Core/TicketLibraryResolutionService.cs').includes('"bonjour"'));
});
test('Safeguards and operational help remain available',()=>{
 const settings=read('web/js/pages/settings-workspace.js'),deployment=read('web/js/pages/settings-deployment.js');
 for(const section of ['apparence','pilotage','collecte','tickets','historique','equipe','deploiement','entretien','demonstration','apropos'])assert.ok(settings.includes(`id: '${section}'`),section);
 assert.ok(deployment.includes('Les bases, tickets, calendriers enregistrés, sessions SSO et secrets restent sur chaque poste.'));
 assert.ok(deployment.includes('if (!acknowledged.control.checked)'));
 assert.ok(read('web/js/pages/settings.js').includes('Attente de connexion / SSO (secondes)'));
});
