"""Logs into the throwaway GLPI 9.5 and saves the HTML of the pages the ticket library reads."""
import json, pathlib, sys
from playwright.sync_api import sync_playwright

base, out = sys.argv[1].rstrip("/"), pathlib.Path(sys.argv[2])
out.mkdir(parents=True, exist_ok=True)
notes = {}

def save(page, name):
    page.wait_for_load_state("networkidle")
    (out / f"{name}.html").write_text(page.content(), encoding="utf-8")
    page.screenshot(path=str(out / f"{name}.png"), full_page=True)
    notes[name] = page.url

with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page(viewport={"width": 1440, "height": 1000}, locale="fr-FR")
    page.goto(f"{base}/index.php")
    save(page, "login")
    page.fill("#login_name", "glpi"); page.fill("#login_password", "glpi")
    page.click("[name=submit]"); page.wait_for_load_state("networkidle")
    page.goto(f"{base}/front/ticket.php"); save(page, "list-default")
    page.goto(f"{base}/front/ticket.php?reset=reset"); save(page, "list-reset")
    all_url = (f"{base}/front/ticket.php?is_deleted=0&as_map=0&criteria%5B0%5D%5Blink%5D=AND&criteria%5B0%5D%5Bfield%5D=12"
               "&criteria%5B0%5D%5Bsearchtype%5D=equals&criteria%5B0%5D%5Bvalue%5D=all&search=Rechercher&itemtype=Ticket&start=0")
    page.goto(all_url); save(page, "list-all-start0")
    page.goto(all_url.replace("start=0", "start=10")); save(page, "list-all-start10")
    page.goto(all_url.replace("start=0", "start=20")); save(page, "list-all-start20")
    for ticket in (6, 12):
        page.goto(f"{base}/front/ticket.form.php?id={ticket}"); save(page, f"ticket-{ticket}-main")
        tabs = page.locator("#tabspanel a, .ui-tabs-nav a, ul.ui-tabs-nav li a")
        names = []
        for index in range(tabs.count()):
            tab = tabs.nth(index); label = (tab.inner_text() or "").strip(); href = tab.get_attribute("href") or ""
            names.append({"label": label, "href": href})
        notes[f"ticket-{ticket}-tabs"] = names
        for index, entry in enumerate(names):
            try:
                tabs.nth(index).click(); page.wait_for_timeout(1200); save(page, f"ticket-{ticket}-tab{index:02d}")
            except Exception as error:
                notes[f"ticket-{ticket}-tab{index:02d}-error"] = str(error)
    browser.close()
(out / "capture-notes.json").write_text(json.dumps(notes, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps({k: v for k, v in notes.items() if not k.endswith("tabs")}, indent=1))
