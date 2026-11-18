'use strict';

module.exports = async ({page,reset,ok}) => {
  await page.emulateMedia({colorScheme:'light',forcedColors:'none'}); await reset();
  const light = await page.evaluate(()=>({theme:document.documentElement.dataset.theme,bg:getComputedStyle(document.documentElement).backgroundColor}));
  await page.emulateMedia({colorScheme:'dark'});
  const dark = await page.evaluate(()=>({theme:document.documentElement.dataset.theme,bg:getComputedStyle(document.documentElement).backgroundColor}));
  ok(light.theme==='system' && dark.theme==='system' && light.bg==='rgb(243, 246, 251)' && dark.bg==='rgb(13, 22, 37)',
    'The first paint follows Windows light and dark mode before application settings are loaded');
  ok(await page.evaluate(async()=>{
    const theme=await window.importProduction('/web/js/theme.js');
    return theme.themePreference({})==='system' && theme.themePreference({theme:'dark'})==='dark'
      && theme.themePreference({theme:'light',interface:{followWindowsTheme:false}})==='light'
      && theme.themePreference({theme:'dark',interface:{followWindowsTheme:true}})==='system'
      && theme.themePreference({theme:'dark',interface:{contrastTheme:true}})==='contrast';
  }),'Missing theme preferences follow Windows while recorded fixed and contrast choices remain explicit');
  await page.emulateMedia({colorScheme:'light',forcedColors:'none'});
};
