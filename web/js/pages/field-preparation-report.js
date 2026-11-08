// The C# report already encodes data. This second boundary only admits the static
// report vocabulary: no active attributes, links, images, embedded frames or styles.
const allowed=new Set(['ARTICLE','SECTION','HEADER','FOOTER','H1','H2','H3','H4','P','DIV','SPAN','STRONG','EM','B','I','SMALL','TABLE','CAPTION','THEAD','TBODY','TFOOT','TR','TH','TD','UL','OL','LI','BR','HR','PRE']);
const blocked=new Set(['SCRIPT','STYLE','IFRAME','OBJECT','EMBED','IMG','SVG','MATH','LINK','META','FORM','INPUT','BUTTON','VIDEO','AUDIO','TEMPLATE']);
const classes=new Set(['sheet','meta','muted','note','check','checkbox','num','subtle','totals','signature','page-break','keep','text','notes','ticket','badge','check-col']);
let afterPrint=null;
export function safeFieldReport(html){
 if(typeof html!=='string'||new TextEncoder().encode(html).length>16*1024*1024)throw new Error('Le document imprimable est absent ou trop volumineux.');
 const parsed=new DOMParser().parseFromString(html,'text/html');
 if(!parsed.body.querySelector('h1'))throw new Error('Le document reçu n’est pas une fiche de préparation.');
 const report=document.createElement('article');report.className='field-report';
 function append(parent,node){
  if(node.nodeType===Node.TEXT_NODE){parent.append(document.createTextNode(node.textContent));return;}
  if(node.nodeType!==Node.ELEMENT_NODE||blocked.has(node.tagName))return;
  if(!allowed.has(node.tagName)){for(const child of node.childNodes)append(parent,child);return;}
  const copy=document.createElement(node.tagName.toLowerCase());
  copy.className=[...node.classList].filter(value=>classes.has(value)).join(' ');
  if(node.tagName==='TH'&&['row','col'].includes(node.getAttribute('scope')))copy.setAttribute('scope',node.getAttribute('scope'));
  for(const key of ['colspan','rowspan']){const value=node.getAttribute(key);if((node.tagName==='TH'||node.tagName==='TD')&&/^([1-9]|1[0-9]|20)$/.test(value||''))copy.setAttribute(key,value);}
  for(const child of node.childNodes)append(copy,child);parent.append(copy);
 }
 for(const node of parsed.body.childNodes)append(report,node);
 return report;
}
export function clearFieldPrint(){
 if(afterPrint)window.removeEventListener('afterprint',afterPrint);afterPrint=null;
 document.querySelectorAll('[data-field-print="true"]').forEach(node=>node.remove());document.body.classList.remove('field-printing');
}
export function printFieldReport(report){
 if(!(report instanceof HTMLElement)||!report.classList.contains('field-report'))throw new Error('Ouvrez d’abord l’aperçu des fiches.');
 clearFieldPrint();const portal=document.createElement('div');portal.dataset.fieldPrint='true';portal.className='field-print-document';portal.append(report.cloneNode(true));
 document.body.append(portal);document.body.classList.add('field-printing');afterPrint=()=>clearFieldPrint();window.addEventListener('afterprint',afterPrint,{once:true});
 try{window.print();}catch(error){clearFieldPrint();throw error;}
}
