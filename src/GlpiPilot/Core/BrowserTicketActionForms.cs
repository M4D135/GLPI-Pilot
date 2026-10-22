using System.Text.Json;
using Microsoft.Playwright;

namespace GlpiPilot.Core;

public sealed partial class BrowserService
{
    private sealed class TicketFormState
    {
        public string Selector { get; set; } = "";
        public string Kind { get; set; } = "";
        public string ActionPath { get; set; } = "";
        public string FieldName { get; set; } = "";
        public string SubmitName { get; set; } = "";
        public string SubmitValue { get; set; } = "";
        public string Current { get; set; } = "";
        public string Structure { get; set; } = "";
        public string Snapshot { get; set; } = "";
        public string OtherFields { get; set; } = "";
        public string EditorId { get; set; } = "";
        public bool HiddenSelectWidget { get; set; }
        public List<TicketActionChoice> Choices { get; set; } = new();
    }
    private static readonly JsonSerializerOptions ActionJson = new() { PropertyNameCaseInsensitive = true };
    // Fixed implementation; neither settings nor adapters may supply JavaScript.
    private const string InspectActionFormsScript = """
    arg => {
      const visible = e => !!e && !e.closest('[hidden],[aria-hidden="true"]') && !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length) && getComputedStyle(e).visibility !== 'hidden';
      const norm = s => String(s ?? '').replace(/\s+/g,' ').trim();
      const installation = new URL(arg.installation);
      if (location.origin !== installation.origin) return JSON.stringify([]);
      const safeAction = (form, kind) => {
        const url = new URL(form.action, location.href);
        const names = kind === 'assignment' ? ['ticket_user.form.php','ticket_group.form.php','ticket.form.php'] : kind === 'task' ? ['tickettask.form.php'] : ['itilfollowup.form.php'];
        return url.origin === installation.origin && !url.username && !url.password && !url.search && !url.hash && names.some(n => url.pathname === installation.pathname+'front/'+n) && form.method.toLowerCase() === 'post' ? url.pathname : '';
      };
      const css = e => { const id=e.getAttribute('id'); if(id && document.querySelectorAll('#'+CSS.escape(id)).length===1) return '#'+CSS.escape(id); const parts=[]; for(let p=e;p && p!==document.documentElement;p=p.parentElement) { const siblings=[...p.parentElement.children].filter(x=>x.tagName===p.tagName); parts.unshift(p.tagName.toLowerCase()+':nth-of-type('+(siblings.indexOf(p)+1)+')'); } return 'html>'+parts.join('>'); };
      const result=[];
      for(const form of [...document.forms].slice(0,100)) {
        if (!visible(form) || form.target && form.target !== '_self') continue;
        let controls=[...form.elements].filter(e=>e.name && !e.disabled);
        if(controls.length>200 || controls.some(e=>e.type==='password' || e.type==='file' && e.files.length)) continue;
        const field=n=>controls.filter(e=>e.name===n);
        const raw=n=>field(n).length===1 ? field(n)[0].value : '';
        const ticketIds = ['tickets_id', ...(raw('itemtype')==='Ticket'?['items_id']:[]), ...(new URL(form.action,location.href).pathname.endsWith('/ticket.form.php')?['id']:[])].flatMap(n=>field(n).map(e=>String(e.value)));
        if(!ticketIds.length || ticketIds.some(id=>id!==arg.id)) continue;
        if(raw('id') && raw('id')!=='0' && !new URL(form.action,location.href).pathname.endsWith('/ticket.form.php')) continue; // no editing an existing followup/task or actor row
        let kind = safeAction(form,'followup') ? 'followup' : safeAction(form,'task') ? 'task' : safeAction(form,'assignment') ? 'assignment' : '';
        if(!kind) continue;
        if(kind==='assignment' && /ticket_(?:user|group)\.form\.php$/.test(form.action) && raw('type')!=='2') continue;
        const allowed=new Set(['id','tickets_id','items_id','itemtype','entities_id', ...(kind==='assignment'?['type','users_id','groups_id','_users_id_assign','_groups_id_assign']:kind==='followup'?['content','is_private','requesttypes_id']:['content','is_private','taskcategories_id','actiontime','state','users_id_tech','groups_id_tech','plan[begin]','plan[end]'])]);
        const submitters=controls.filter(e=>e.type==='submit' && (e.tagName==='BUTTON' || e.tagName==='INPUT'));
        const submit=submitters.filter(e=>visible(e) && (kind==='assignment'?['add','update','_add']:['add','_add']).includes(e.name) && !/delete|purge|supprimer|remove|résoudre|solve/i.test(e.value+' '+e.innerText));
        if(submit.length!==1 || submit[0].hasAttribute('formaction') && new URL(submit[0].formAction).href!==new URL(form.action).href || submit[0].getAttribute('formmethod') || submit[0].getAttribute('formtarget') && submit[0].getAttribute('formtarget') !== '_self') continue;
        controls=controls.filter(e=>!['submit','button','reset','file'].includes(e.type));
        if(controls.some(e=>!allowed.has(e.name) && !/^(?:_glpi_csrf_token|csrf(?:_token)?|_token|token)$/i.test(e.name))) continue;
        const publicFields=controls.filter(e=>allowed.has(e.name));
        let primary=kind==='assignment'?publicFields.filter(e=>['users_id','groups_id','_users_id_assign','_groups_id_assign'].includes(e.name)&&e.tagName==='SELECT'&&!e.multiple):publicFields.filter(e=>e.name==='content'&&e.tagName==='TEXTAREA');
        if(primary.length!==1) continue;
        const input=primary[0]; if(input.readOnly) continue; let editorId='', hiddenSelectWidget=false;
        if(!visible(input) && input.tagName==='SELECT') {
          const widgets=[input.previousElementSibling,input.nextElementSibling,input.id?document.getElementById('s2id_'+input.id):null].filter(Boolean);
          hiddenSelectWidget=input.matches('.select2-hidden-accessible,.select2-offscreen,[data-select2-id]') && widgets.some(widget=>widget.matches('.select2,.select2-container')&&form.contains(widget)&&visible(widget));
          if(!hiddenSelectWidget) continue;
        }
        if(!visible(input) && !hiddenSelectWidget) {
          if(input.tagName!=='TEXTAREA' || !input.id) continue;
          const editor=window.tinymce?.get(input.id), iframe=document.getElementById(input.id+'_ifr');
          if(!editor || !iframe || !visible(iframe) || !form.contains(iframe) || !iframe.contentDocument?.body?.isContentEditable) continue;
          editorId=input.id;
        }
        const value=e=>e===input && editorId ? window.tinymce.get(editorId).getContent({format:'text'}) : e.type==='checkbox'||e.type==='radio' ? (e.checked?e.value:'(non coché)') : e.value;
        const choices=input.tagName==='SELECT' ? [...input.options].filter(o=>!o.disabled && /^\d+$/.test(o.value) && o.value!=='0').slice(0,2000).map(o=>({value:o.value,label:norm(o.textContent)})) : [];
        const structure=JSON.stringify({kind,path:safeAction(form,kind),fields:publicFields.map(e=>[e.name,e.tagName,e.type,!!e.multiple, ['itemtype','type'].includes(e.name)?e.value:'']).sort(),submit:[submit[0].name,submit[0].value]});
        const snapshot=JSON.stringify(publicFields.map(e=>[e.name,value(e)]));
        const other=publicFields.filter(e=>e!==input && !['id','tickets_id','items_id','itemtype','type'].includes(e.name)).map(e=>e.name+' : '+(e.tagName==='SELECT'?norm(e.selectedOptions[0]?.textContent):value(e))).join('\n') || 'Aucun autre champ métier.';
        result.push({selector:css(form),kind,actionPath:safeAction(form,kind),fieldName:input.name,submitName:submit[0].name,submitValue:submit[0].value,current:value(input),structure,snapshot,otherFields:other,editorId,hiddenSelectWidget,choices});
      }
      return JSON.stringify(result);
    }
    """;

    private async Task<List<(IFrame Frame, TicketFormState State)>> InspectActionFormsAsync(IPage page, TicketReference ticket, CancellationToken ct)
    {
        EnsureTicketPageIdentity(page, ticket);
        var output = new List<(IFrame, TicketFormState)>();
        foreach (var frame in await TicketFramesAsync(page, ticket, ct))
        {
            var inspection = await ReadTicketJsonAsync<TicketDomInspection>(frame, "arg => window.__glpiPilotTicket.inspect(arg)", new { id = ticket.Id, origin = Origin(ticket.Url), selectors = Array.Empty<string>() }, ct);
            if (inspection.WrongId || inspection.Authentication || !inspection.IdentityConfirmed) continue;
            var json = await frame.EvaluateAsync<string>(InspectActionFormsScript, new { id = ticket.Id, installation = TicketActionService.Installation(ticket) }).WaitAsync(ct);
            var forms = JsonSerializer.Deserialize<List<TicketFormState>>(json, ActionJson) ?? new();
            output.AddRange(forms.Select(state => (frame, state)));
        }
        EnsureTicketPageIdentity(page, ticket);
        return output;
    }

    private const string ActionReadbackScript = """
    arg => {
      const norm = s => String(s??'').replace(/\s+/g,' ').trim();
      const visible=e=>!!e&&!e.closest('[hidden],[aria-hidden="true"]')&&!!(e.offsetWidth||e.offsetHeight||e.getClientRects().length);
      const nodes=[...document.querySelectorAll(arg.kind==='task'?'.h_item:has(.ITILTask),.timeline-item[data-itemtype="TicketTask"],.timeline-item[data-type="task"]':'.h_item:has(.ITILFollowup),.timeline-item[data-itemtype="ITILFollowup"],.timeline-item[data-type="followup"]')];
      return JSON.stringify(nodes.filter(visible).map(e=>({id:e.getAttribute('data-items-id')||e.getAttribute('data-id')||e.id||'',text:norm(e.querySelector('.rich_text_container,.timeline-content,.item_content')?.innerText||'')})).filter(e=>e.id && e.text));
    }
    """;
    private sealed record ActionReadbackEntry(string Id, string Text);
    private async Task<IReadOnlyList<ActionReadbackEntry>> ReadActionEvidenceAsync(IPage page, TicketReference ticket, string kind, CancellationToken ct)
    {
        var entries = new List<ActionReadbackEntry>();
        foreach (var frame in await TicketFramesAsync(page, ticket, ct))
        {
            var json = await frame.EvaluateAsync<string>(ActionReadbackScript, new { kind }).WaitAsync(ct);
            entries.AddRange(JsonSerializer.Deserialize<List<ActionReadbackEntry>>(json, ActionJson) ?? new());
        }
        EnsureTicketPageIdentity(page, ticket); return entries;
    }
}
