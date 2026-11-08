using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GlpiPilot.Core;
using GlpiPilot.Runtime;
using Microsoft.Playwright;

internal static partial class Program
{
    private const string PresentationSvgReferencesResolved = """
        node => { const svgs=[...node.querySelectorAll('svg')]; let references=0;
          return svgs.length>0&&svgs.every(svg=>[...svg.querySelectorAll('*')].every(element=>[...element.attributes].every(attribute=>
            [...attribute.value.matchAll(/url\(#([^)]*)\)/g)].every(match=>{references++;const target=document.getElementById(match[1]);return target&&svg.contains(target)&&match[1].startsWith('pilot-presentation-');}))))&&references>0; }
        """;

    private static async Task CheckPresentationInterfaceAsync(string[] args)
    {
        var source=Path.Combine(_root,"presentation-source.csv");
        await File.WriteAllTextAsync(source,"ID;Titre;Statut;Priorité;Type;Attribué à - Technicien;Attribué à - Groupe de techniciens;Catégorie;Date d'ouverture;Date d'échéance\n"
            +string.Join("\n",Enumerable.Range(1,120).Select(i=>$"{i};Ticket fictif {i};{(i%3==0?"En attente":"Nouveau")};{(i%4==0?"Haute":"Normale")};Incident;{(i%2==0?"Alex":"Camille")};{(i%2==0?"Équipe A":"Équipe B")};{(i%3==0?"Matériel":"Réseau")};2026-09-01 09:00;2026-10-01 09:00"))+"\n");
        var settings=new AppSettings { Theme="light", ExportFolder=Path.Combine(_root,"Exports") }; settings.Interface.OnboardingCompleted=true;
        using var playwright=await Playwright.CreateAsync();
        await using var browser=await playwright.Chromium.LaunchAsync(new(){Headless=true,ExecutablePath=Environment.GetEnvironmentVariable("GLPI_TEST_CHROMIUM"),Args=["--no-sandbox","--disable-dev-shm-usage"]});
        JsonElement savedDeck; string savedFingerprint,weatherId;
        await using(var ui=await WeatherSelectionUi.CreateAsync(browser,settings,source))
        {
            await ui.Navigate(); await ui.ChooseTeams("Équipe A"); await ui.Stock(60);
            var page=ui.Page;
            ILocator Editor()=>page.GetByRole(AriaRole.Dialog,new(){Name="Préparer la présentation",Exact=true});
            ILocator Stage()=>Editor().Locator(".presentation-stage-viewport");
            ILocator Viewer()=>page.GetByRole(AriaRole.Dialog,new(){Name="Présentation du pilotage",Exact=true});
            async Task Open(){await page.GetByRole(AriaRole.Button,new(){Name="Présenter",Exact=true}).ClickAsync();await Editor().Locator("[data-presentation-editor][data-ready=true]").WaitForAsync();}
            async Task Save(){await Editor().GetByRole(AriaRole.Button,new(){Name="Enregistrer la présentation",Exact=true}).ClickAsync();await Editor().Locator("[data-presentation-editor][data-dirty=false]").WaitForAsync();await ui.Idle();}
            async Task Close(){await Editor().Locator(".modal-heading").GetByRole(AriaRole.Button,new(){Name="Fermer",Exact=true}).ClickAsync();await Editor().WaitForAsync(new(){State=WaitForSelectorState.Detached});}
            async Task Add(string id){var target=Editor().Locator($"[data-presentation-add='{id}']");if(!await target.IsDisabledAsync())await target.ClickAsync();}
            async Task CurrentTitle(string value){await Editor().GetByLabel("Titre de la diapo",new(){Exact=true}).FillAsync(value);await Editor().GetByLabel("Titre de la diapo",new(){Exact=true}).PressAsync("Tab");}
            async Task<(float X,float Y,float Width,float Height)> Bounds(ILocator node) { var box=await node.BoundingBoxAsync()??throw new Exception("Missing presentation bounds");return(box.X,box.Y,box.Width,box.Height); }
            var scoped=await page.Locator(".workspace-scope-summary").InnerTextAsync();
            var calculations=ui.Operations.Count(op=>op.Operation=="dashboard.get");
            weatherId=await page.Locator(".pilot-widget:has(svg defs [id])").First.GetAttributeAsync("data-widget-id")??throw new Exception("No weather SVG widget");
            var metric=await page.Locator(".pilot-widget[data-widget-kind=metric]").First.GetAttributeAsync("data-widget-id")??throw new Exception("No metric");
            var chart=await page.Locator(".pilot-widget[data-widget-kind=distribution],.pilot-widget[data-widget-kind=matrix]").First.GetAttributeAsync("data-widget-id")??throw new Exception("No chart");
            var tableId=await page.Locator(".pilot-widget[data-widget-kind=attention],.pilot-widget[data-widget-kind=oldest]").First.GetAttributeAsync("data-widget-id")??throw new Exception("No table");
            Require(await page.Locator(".workspace-commandbar .motion-control-group").CountAsync()==0,"Pilotage keeps animation preferences in Settings and does not repeat a global animation control in its command bar.");
            await Open();
            Require(await Stage().Locator("[data-placement-id]").CountAsync()>=4&&await Editor().Locator("[data-slide-id]").CountAsync()==1
                &&(await Editor().Locator(".presentation-editor-context").InnerTextAsync()).Contains("Équipe A",StringComparison.Ordinal),"The real presentation editor starts with multiple actual widgets on one 16:9 page and names the selected team scope.");
            await WaitForPageAsync(page,"()=>{const canvas=document.querySelector('dialog.presentation-editor-modal .presentation-stage-viewport>.presentation-canvas'),host=document.querySelector('dialog.presentation-editor-modal .presentation-stage-host');return !!canvas&&!!host&&canvas.style.transform.startsWith('scale(')&&canvas.getBoundingClientRect().width<=host.clientWidth&&canvas.getBoundingClientRect().width>200;}");
            await ui.Capture(args,"presentation-editor-initial");
            var moved=Stage().Locator("[data-placement-id]").First; var placement=await moved.GetAttributeAsync("data-placement-id");
            var before=await Bounds(moved); var handle=await Bounds(moved.Locator("[data-presentation-drag]"));
            await page.Mouse.MoveAsync(handle.X+handle.Width/2,handle.Y+handle.Height/2);await page.Mouse.DownAsync();await page.Mouse.MoveAsync(handle.X+handle.Width/2+28,handle.Y+handle.Height/2+18,new(){Steps=8});await page.Mouse.UpAsync();
            var after=await Bounds(moved);
            Console.WriteLine("PRESENTATION DRAG: "+JsonSerializer.Serialize(new{beforeX=before.X,beforeY=before.Y,afterX=after.X,afterY=after.Y,style=await moved.GetAttributeAsync("style")}));
            await ui.Capture(args,"presentation-editor-drag");
            Require(after.X>before.X+10&&after.Y>before.Y+5,"A real mouse drag places a widget freely inside the slide instead of reordering a vertical list.");
            var resize=await Bounds(moved.Locator("[data-presentation-resize=se]")); before=await Bounds(moved);
            await page.Mouse.MoveAsync(resize.X+resize.Width/2,resize.Y+resize.Height/2);await page.Mouse.DownAsync();await page.Mouse.MoveAsync(resize.X+resize.Width/2+24,resize.Y+resize.Height/2+22,new(){Steps=8});await page.Mouse.UpAsync();after=await Bounds(moved);
            Require(after.Width>before.Width+10&&after.Height>before.Height+10,"The southeast handle genuinely resizes width and height with the mouse.");
            await Editor().GetByLabel("Largeur du widget (%)",new(){Exact=true}).FillAsync("32");await Editor().GetByLabel("Largeur du widget (%)",new(){Exact=true}).PressAsync("Tab");
            Require(await Stage().Locator($"[data-placement-id='{placement}']").EvaluateAsync<string>("node=>node.style.width")=="32%","The numeric inspector applies a precise width without changing the selected widget identity.");
            await Editor().GetByRole(AriaRole.Button,new(){Name="Annuler la modification de présentation",Exact=true}).ClickAsync();
            Require(await Stage().Locator($"[data-placement-id='{placement}']").EvaluateAsync<string>("node=>node.style.width")!="32%","Undo restores the preceding placement geometry.");
            await Editor().GetByRole(AriaRole.Button,new(){Name="Rétablir la modification de présentation",Exact=true}).ClickAsync();
            Require(await Stage().Locator($"[data-placement-id='{placement}']").EvaluateAsync<string>("node=>node.style.width")=="32%","Redo reapplies the exact saved inspector change.");
            await Editor().GetByRole(AriaRole.Button,new(){Name="Répartir les widgets de cette diapo",Exact=true}).ClickAsync();
            await CurrentTitle("Synthèse de l’équipe A");
            await ui.Capture(args,"presentation-editor-light");
            await Editor().GetByRole(AriaRole.Button,new(){Name="Ajouter une diapo",Exact=true}).ClickAsync();await CurrentTitle("Météo et priorités");
            await Add(weatherId);await Add(chart);await Add(tableId);
            await Editor().GetByRole(AriaRole.Button,new(){Name="Répartir les widgets de cette diapo",Exact=true}).ClickAsync();
            Require(await Editor().Locator("[data-slide-id]").CountAsync()==2&&await Stage().Locator("[data-placement-id]").CountAsync()==3,"A second slide independently combines a weather widget, an analysis and a table.");
            await Editor().GetByRole(AriaRole.Button,new(){Name="Dupliquer la diapo",Exact=true}).ClickAsync();
            Require(await Editor().Locator("[data-slide-id]").CountAsync()==3&&await Stage().Locator("[data-placement-id]").CountAsync()==3,"Duplicating a slide retains all three widgets and their placements with separate slide identity.");
            await Editor().GetByRole(AriaRole.Button,new(){Name="Supprimer la diapo",Exact=true}).ClickAsync();
            Require(await Editor().Locator("[data-slide-id]").CountAsync()==2,"Deleting a duplicate removes that page without changing the original composition.");
            await Editor().GetByRole(AriaRole.Button,new(){Name="Monter la diapo 2",Exact=true}).ClickAsync();await Save();
            var stored=await Call(ui.App,"dashboard.presentation.get",new{runId=ui.Model.GetProperty("runId").GetString(),revision=ui.App.Revision});
            savedDeck=stored.GetProperty("deck").Clone();savedFingerprint=stored.GetProperty("fingerprint").GetString()!;
            Require(savedDeck.GetProperty("slides")[0].GetProperty("title").GetString()=="Météo et priorités"&&savedDeck.GetProperty("slides").GetArrayLength()==2,"Saving writes the real two-slide composition, its custom titles and its reordered pages to the application configuration.");
            await Close();await Open();
            Require(await Editor().Locator("[data-slide-id]").CountAsync()==2&&await Editor().GetByLabel("Titre de la diapo",new(){Exact=true}).InputValueAsync()=="Météo et priorités","Closing and reopening retrieves the saved multi-widget composition.");
            await Call(ui.App,"app.appearance",new{theme="dark"});await ui.Flush();await ui.Capture(args,"presentation-editor-dark");
            await Editor().GetByRole(AriaRole.Button,new(){Name="Présenter",Exact=true}).ClickAsync();await Viewer().WaitForAsync();
            Require(await Viewer().Locator("[data-placement-id]").CountAsync()==3&&await Viewer().Locator("[data-presentation-area] button,[data-presentation-area] a,[data-presentation-area] input").CountAsync()==0
                &&await Viewer().Locator(".motion-control-group").CountAsync()==0,"Projection shows the three arranged widgets together and keeps editing controls and global animation settings out of the slide.");
            Require(await Viewer().Locator("[data-presentation-area]").EvaluateAsync<bool>(PresentationSvgReferencesResolved),"Every gradient and clip reference in the projected weather SVG resolves inside its own cloned SVG.");
            Require((await Viewer().Locator(".presentation-canvas-footer").InnerTextAsync()).Contains("Équipe A",StringComparison.Ordinal)
                &&(await Viewer().Locator(".presentation-provenance").InnerTextAsync()).Contains("60",StringComparison.Ordinal),"Each slide retains the filtered perimeter, real observation date and ticket count.");
            await ui.Capture(args,"presentation-viewer-dark");await Call(ui.App,"app.appearance",new{theme="light"});await ui.Flush();await ui.Capture(args,"presentation-viewer-light");
            await Viewer().GetByRole(AriaRole.Button,new(){Name="Diapo suivante",Exact=true}).ClickAsync();
            Require(await Viewer().GetAttributeAsync("data-presentation-slide")=="1"&&await Viewer().GetByRole(AriaRole.Button,new(){Name="Diapo suivante",Exact=true}).IsDisabledAsync(),"Navigation advances by a whole composed page and stops at the last slide.");
            await Viewer().GetByRole(AriaRole.Button,new(){Name="Diapo précédente",Exact=true}).ClickAsync();
            Require(await Viewer().GetByRole(AriaRole.Button,new(){Name="Diapo précédente",Exact=true}).IsDisabledAsync(),"Returning to the first page restores the disabled previous-page boundary.");
            var html=Path.Combine(_root,"presentation-slides.html");ui.Host.NextSave=html;
            await Viewer().GetByRole(AriaRole.Button,new(){Name="Exporter la présentation",Exact=true}).ClickAsync();
            var export=page.GetByRole(AriaRole.Dialog,new(){Name="Exporter la présentation",Exact=true});await export.WaitForAsync();
            await export.GetByLabel("Format",new(){Exact=true}).SelectOptionAsync("html");await export.GetByRole(AriaRole.Button,new(){Name="Exporter",Exact=true}).ClickAsync();await ui.Idle();await Until(()=>File.Exists(html));
            var htmlText=System.Net.WebUtility.HtmlDecode(await File.ReadAllTextAsync(html));
            Require(htmlText.Contains("Météo et priorités",StringComparison.Ordinal)&&htmlText.Contains("Synthèse de l’équipe A",StringComparison.Ordinal),"The actual HTML export contains the saved slide composition and its two titles.");
            var pdf=Path.Combine(_root,"presentation-slides.pdf");ui.Host.NextSave=pdf;
            await Viewer().GetByRole(AriaRole.Button,new(){Name="Exporter la présentation",Exact=true}).ClickAsync();await export.GetByLabel("Format",new(){Exact=true}).SelectOptionAsync("pdf");
            await export.GetByRole(AriaRole.Button,new(){Name="Exporter",Exact=true}).ClickAsync();await ui.Idle();await Until(()=>File.Exists(pdf));await WaitForPageAsync(page,"!document.querySelector('.presentation-export-document')");
            var pdfText=Encoding.Latin1.GetString(await File.ReadAllBytesAsync(pdf));var media=Regex.Match(pdfText,@"/MediaBox\s*\[\s*0\s+0\s+([0-9.]+)\s+([0-9.]+)\s*\]");
            Console.WriteLine("PRESENTATION PDF: "+JsonSerializer.Serialize(new{bytes=new FileInfo(pdf).Length,pages=Regex.Matches(pdfText,@"/Type\s*/Page\b").Count,mediaBox=media.Value,viewer=await Viewer().IsVisibleAsync(),printContext=ui.PdfText}));
            var captureIndex=Array.IndexOf(args,"--screenshots");if(captureIndex>=0&&captureIndex+1<args.Length)File.Copy(pdf,Path.Combine(args[captureIndex+1],"presentation-slides.pdf"),true);
            Require(new FileInfo(pdf).Length>1000&&Regex.Matches(pdfText,@"/Type\s*/Page\b").Count==2&&media.Success
                &&Math.Abs(double.Parse(media.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture)/double.Parse(media.Groups[2].Value,System.Globalization.CultureInfo.InvariantCulture)-16d/9d)<.01
                &&await Viewer().IsVisibleAsync(),"The real Chromium PDF contains exactly two landscape 16:9 pages and restores projection after removing its temporary print document.");
            Require(ui.PdfText.Contains("Recherche :",StringComparison.Ordinal)&&ui.PdfText.Contains("Personnel :",StringComparison.Ordinal)
                &&ui.PdfText.Contains("Filtres :",StringComparison.Ordinal)&&ui.PdfText.Contains("Équipe A",StringComparison.Ordinal),"The actual PDF print document visibly includes source search, personal perimeter and filters rather than leaving them in hover-only metadata.");
            var previewArgs=ui.Operations.Last(op=>op.Operation=="reports.preview").Args;
            var realPreview=await Call(ui.App,"reports.preview",previewArgs);
            var longContext=await page.EvaluateAsync<JsonElement>("""
                async json=>{const preview=JSON.parse(json),words=Array.from({length:300},(_,i)=>`FiltreSynthétique${String(i).padStart(3,'0')}`);
                  preview.context.push({key:'Contexte de validation volumineux',value:words.join(' ')});
                  preview.warnings=[...(preview.warnings||[]),words.join(' ')];
                  preview.context.push({key:'Réserves de validation',value:words.join(' ')});
                  const ids=preview.widgets.map(widget=>widget.id),sources=new Map(ids.map(id=>[id,document.querySelector(`.pilot-widget[data-widget-id="${id}"]`)]));
                  const {presentationPdfDocument}=await import('/js/pages/pilot-presentation.js');
                  window.__longPresentationCleanup=presentationPdfDocument(preview,ids,sources);
                  const root=document.querySelector('.presentation-export-document'),appendices=[...root.querySelectorAll('[data-export-context-page]')],text=appendices.map(node=>node.innerText).join(' ');
                  return {slides:root.querySelectorAll('[data-export-slide]').length,appendices:appendices.length,
                    noEmptyAppendices:appendices.every(node=>!!node.querySelector('pre.presentation-context-text')?.textContent.trim()),
                    allWords:words.every(word=>text.includes(word)),note:[...root.querySelectorAll('.presentation-export-detail')].every(node=>node.innerText.includes('pages de contexte'))};}
                """,realPreview.GetRawText());
            Console.WriteLine("PRESENTATION LONG CONTEXT: "+longContext.GetRawText());
            var longPdf=Path.Combine(_root,"presentation-long-context.pdf");
            try { await ui.Host.PrintPdfAsync(longPdf); }
            finally { await page.EvaluateAsync("()=>{window.__longPresentationCleanup?.();delete window.__longPresentationCleanup;}"); }
            Require(longContext.GetProperty("slides").GetInt32()==2&&longContext.GetProperty("appendices").GetInt32()>0
                &&longContext.GetProperty("noEmptyAppendices").GetBoolean()&&longContext.GetProperty("allWords").GetBoolean()&&longContext.GetProperty("note").GetBoolean()
                &&Regex.Matches(Encoding.Latin1.GetString(await File.ReadAllBytesAsync(longPdf)),@"/Type\s*/Page\b").Count==2+longContext.GetProperty("appendices").GetInt32(),
                "A long synthetic context exercises the real PDF renderer: both composed slides remain intact, every appendix has content, all 300 filter markers are retained, and the actual PDF has exactly those pages.");
            var captures=Array.IndexOf(args,"--screenshots");if(captures>=0&&captures+1<args.Length){File.Copy(html,Path.Combine(args[captures+1],"presentation-slides.html"),true);File.Copy(pdf,Path.Combine(args[captures+1],"presentation-slides.pdf"),true);File.Copy(longPdf,Path.Combine(args[captures+1],"presentation-long-context.pdf"),true);}
            await page.SetViewportSizeAsync(768,800);
            Require(await Viewer().EvaluateAsync<bool>("node=>node.getBoundingClientRect().right<=innerWidth+1&&node.scrollWidth<=innerWidth+1"),"The full 16:9 slide scales into a compact viewport without horizontal page overflow.");
            await ui.Capture(args,"presentation-viewer-compact");await Viewer().GetByRole(AriaRole.Button,new(){Name="Revenir à la préparation",Exact=true}).ClickAsync();await page.SetViewportSizeAsync(1440,1000);
            Require(await Editor().IsVisibleAsync()&&await page.Locator(".workspace-scope-summary").InnerTextAsync()==scoped
                &&ui.Operations.Count(op=>op.Operation=="dashboard.get")==calculations,"Returning from projection retains the original calculation and filtered perimeter without an implicit data reload.");
            await CurrentTitle("Modification locale conservée");
            var newer=savedDeck.Deserialize<DashboardPresentationSettings>(PilotApplication.Json)!;newer.Slides[0].Title="Version concurrente";
            await Call(ui.App,"dashboard.presentation.save",new{runId=ui.Model.GetProperty("runId").GetString(),revision=ui.App.Revision,baseFingerprint=savedFingerprint,deck=newer});
            await Editor().GetByRole(AriaRole.Button,new(){Name="Enregistrer la présentation",Exact=true}).ClickAsync();await Editor().Locator(".presentation-editor-errors:not(:empty)").WaitForAsync();
            Require(await Editor().GetByLabel("Titre de la diapo",new(){Exact=true}).InputValueAsync()=="Modification locale conservée","A concurrent presentation save is rejected without losing the user's unsaved composition.");
            await Editor().Locator(".modal-heading").GetByRole(AriaRole.Button,new(){Name="Fermer",Exact=true}).ClickAsync();
            await page.GetByRole(AriaRole.Dialog,new(){Name="Fermer la préparation",Exact=true}).GetByRole(AriaRole.Button,new(){Name="Fermer sans enregistrer",Exact=true}).ClickAsync();
            var now=await Call(ui.App,"dashboard.presentation.get",new{runId=ui.Model.GetProperty("runId").GetString(),revision=ui.App.Revision});
            await Call(ui.App,"dashboard.presentation.save",new{runId=ui.Model.GetProperty("runId").GetString(),revision=ui.App.Revision,baseFingerprint=now.GetProperty("fingerprint").GetString(),deck=savedDeck});
            Require(ui.Errors.IsEmpty&&ui.ExternalRequests==0,"Composition, mouse manipulation, projection, real exports and stale-save handling complete without JavaScript error or external request.");
        }
        File.Delete(source);
        await using(var ui=await WeatherSelectionUi.CreateAsync(browser))
        {
            await ui.Navigate();await ui.Stock(60);await ui.Page.GetByRole(AriaRole.Button,new(){Name="Présenter",Exact=true}).ClickAsync();
            var editor=ui.Page.GetByRole(AriaRole.Dialog,new(){Name="Préparer la présentation",Exact=true});await editor.Locator("[data-presentation-editor][data-ready=true]").WaitForAsync();
            Require(ui.App.IsCachedSource&&!File.Exists(source)&&await editor.Locator("[data-slide-id]").CountAsync()==2
                &&await editor.GetByLabel("Titre de la diapo",new(){Exact=true}).InputValueAsync()=="Météo et priorités","A new application and empty WebView profile restore the composed pages with the cached source and saved team selection after the CSV is deleted.");
            await editor.GetByRole(AriaRole.Button,new(){Name="Présenter",Exact=true}).ClickAsync();var viewer=ui.Page.Locator(".pilot-presentation");await viewer.WaitForAsync();
            Require(await viewer.GetAttributeAsync("data-presentation-cached")=="true"&&(await viewer.Locator(".presentation-provenance").InnerTextAsync()).Contains("Dernier relevé conservé",StringComparison.Ordinal)
                &&!ui.Operations.Any(op=>op.Operation.StartsWith("collect.",StringComparison.Ordinal)),"The restored presentation explicitly identifies the offline observation and does not launch a collection.");
            await viewer.GetByRole(AriaRole.Button,new(){Name="Revenir à la préparation",Exact=true}).ClickAsync();await editor.Locator(".modal-heading").GetByRole(AriaRole.Button,new(){Name="Fermer",Exact=true}).ClickAsync();
            var normal=await File.ReadAllBytesAsync(AppPaths.SettingsFile);
            await Call(ui.App,"app.demo");await ui.Flush();await ui.Page.ReloadAsync();await ReadyAsync(ui.Page);await ui.Navigate();
            await ui.Page.GetByRole(AriaRole.Button,new(){Name="Présenter",Exact=true}).ClickAsync();await editor.Locator("[data-presentation-editor][data-ready=true]").WaitForAsync();
            await editor.GetByLabel("Titre de la diapo",new(){Exact=true}).FillAsync("Réunion de démonstration");await editor.GetByLabel("Titre de la diapo",new(){Exact=true}).PressAsync("Tab");
            await editor.GetByRole(AriaRole.Button,new(){Name="Enregistrer la présentation",Exact=true}).ClickAsync();await editor.Locator("[data-presentation-editor][data-dirty=false]").WaitForAsync();
            Require(ui.App.IsTestMode&&(await File.ReadAllBytesAsync(AppPaths.SettingsFile)).SequenceEqual(normal),"Saving a different presentation in the fictional demonstration leaves the real normal-profile settings byte-for-byte unchanged.");
            Require(ui.Errors.IsEmpty&&ui.ExternalRequests==0,"Offline restart and isolated demo presentation complete without JavaScript errors or external requests.");
        }
    }
}
