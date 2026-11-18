'use strict';

module.exports = async ({page,reset,ok}) => {
  await reset();
  await page.evaluate(async () => {
    const {renderOperations} = await window.importProduction('/web/js/pages/foundation.js');
    window.componentRequests = []; window.componentRefreshes = 0; window.componentNotices = [];
    await renderOperations({root:document.querySelector('#root'),signal:controller.signal,
      api:{call:async (operation,args) => {
        if(operation === 'operations.list') return {entries:[],log:[]};
        if(operation === 'components.list') return {known:[
          {label:'Pilote Playwright et son nœud',available:true,status:'Fourni avec le pack',message:'Aucune installation supplémentaire nécessaire.'},
          {label:'Runtime WebView2 à version fixe',available:false,optional:true,status:'Facultatif',message:'Facultatif avec Evergreen.'}
        ]};
        if(operation === 'components.install') {
          componentRequests.push(args);
          return new Promise((resolve,reject)=>{window.finishComponentInstall={resolve,reject};});
        }
        throw new Error('Unexpected operation: '+operation);
      }},notify:message=>componentNotices.push(message),refresh:async()=>{componentRefreshes++;}});
  });
  const path = page.getByLabel('Chemin du ZIP de composant',{exact:true});
  const sum = page.getByLabel('Empreinte SHA-256 (facultatif)',{exact:true});
  const install = page.getByRole('button',{name:'Installer le composant',exact:true});
  ok(await path.inputValue()==='' && await sum.inputValue()==='' && await install.isDisabled(),
    'Component inputs start empty and an absent ZIP cannot launch an installation');
  ok((await path.getAttribute('placeholder')).includes('Chemin du ZIP') && !(await page.locator('#root').innerText()).includes('[object Object]'),
    'Component form renders readable labels and placeholders instead of object strings');
  ok(await page.getByText('Fourni avec le pack',{exact:true}).isVisible() && await page.getByText('Facultatif',{exact:true}).isVisible(),
    'Component diagnostics distinguish the provided driver from optional complementary ZIPs');
  await path.fill('   ');
  ok(await install.isDisabled(),'Whitespace in the component path does not enable installation');
  await path.fill('  C:\\Fictif\\composant.zip  '); await sum.fill('invalid');
  await install.click(); await page.getByRole('alert').waitFor();
  ok(await page.evaluate(()=>componentRequests.length)===0 && await install.isEnabled(),
    'A malformed optional checksum is rejected before any component operation');
  await sum.fill('a'.repeat(64)); await install.click();
  await page.waitForFunction(()=>componentRequests.length===1);
  ok(await path.isDisabled() && await sum.isDisabled() && await install.isDisabled(),
    'An in-flight component installation cannot be edited or submitted twice');
  await page.evaluate(()=>finishComponentInstall.reject(new Error('ZIP de contrôle introuvable.')));
  await page.waitForFunction(()=>!document.querySelector('button[aria-busy=true]'));
  ok(await path.inputValue()==='  C:\\Fictif\\composant.zip  ' && await install.isEnabled(),
    'A refused component installation preserves the entered path and allows correction');
  await install.click(); await page.waitForFunction(()=>componentRequests.length===2);
  await page.evaluate(()=>finishComponentInstall.resolve({message:'Installation de contrôle terminée.'}));
  await page.waitForFunction(()=>componentRefreshes===1 && !document.querySelector('button[aria-busy=true]'));
  ok(await page.evaluate(()=>componentRequests.every(value=>value.archive==='C:\\Fictif\\composant.zip' && value.sha256==='a'.repeat(64)))
    && await page.evaluate(()=>componentNotices.length)===1,
    'The component form sends the actual trimmed path and checksum and refreshes after success');
};
