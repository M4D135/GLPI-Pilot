# Native export DOM checks

These tests load four synthetic fixtures representing the official GLPI 10,
GLPI 11, and legacy export structures described in
`src/GlpiPilot/BrowserScripts/native-export.README.md`.

Every request to the synthetic `http://glpi-native.test` origin is intercepted
by Playwright. HTML comes from the local fixtures; the report URL returns a
tiny simulated CSV attachment. Other origins are blocked. No real GLPI,
corporate SSO, account, or browser profile is contacted.

The checks cover menu discovery without DOM mutation, all-pages proof,
GLPI 11 menus moved to `body`, actual browser download events, legacy browser
GET submission with preserved filters, Select2, explicit table scoping,
duplicate and conflicting export links, permissions-related absence of
controls, wrong origins, current-page-only modes, and exclusion of business
form submissions. The realistic legacy fixture contains six HTML tables,
including two pager tables, and a 16-column result table initially showing
20 of 828 synthetic rows. Its export control lies outside the massive-action
form. Variants exercise the official image submit button, Select2, a real
display-limit POST through an existing option, full-range verification,
conflicting or unrelated pagers, nonzero or missing URL offsets, and diagnostics
that exclude fake private sentinels. No real ticket information is included.

`glpi-cell-popup.html` adds synthetic result cells matching the public GLPI
9.5.13 and 10.0.22 renderers. Long multivalue cells display their first item and
a `fa-comments` marker, with the complete static list in its paired popup;
they do not display a textual ellipsis. The table adapter reads the complete
list only when marker IDs are unique among siblings, marker and popup are
adjacent siblings, the wrapper classes and `fup-popup` structure match,
the list uses direct `br` or `hr` separators, and its first item exactly
matches the whole displayed cell value. Nested hidden user-information
cards are excluded from both the item text and the separator count: their
own `br` elements must not hide subsequent requesters. No tooltip is opened
and no request is issued. Other hidden content, ordinary information/title
tooltips, mismatched IDs or first items, and duplicate sibling IDs remain
excluded.
Later items remain part of the extracted value, so a real difference there
still reaches the C# native-export integrity check.

The run also constructs the official long-text pattern: `span#textN` with
the exact ` (...)` abbreviation suffix, followed by a paired `fa-comments`
popup. Only that span is replaced, and only when the full static popup text
is longer and begins with its entire displayed prefix. Different IDs,
prefixes, absent suffixes, hidden markers, AJAX placeholders and duplicate
sibling IDs cannot authorize expansion. C1 code points remain unchanged.

`Search::showItem` copies the first long-text triplet both into the visible
cell and into the outer multivalue popup. The checks cover these legitimate
cloned IDs for GLPI 9.5 and 10 with both separator types, while retaining
different later values. Associations follow actual adjacent DOM nodes;
they never resolve a tooltip with a global ID lookup. Line breaks inside a
proven preview belong to that value rather than to the outer list. The
specific generated `long_text > p.read_more > span.read_more_button` control
is excluded inside a validated popup; literal ellipses remain in its content.

Both `br` and `hr` preserve a newline boundary in the extracted text. This
does not imitate native CSV punctuation or remove real characters to force
agreement. In particular, GLPI 9.5 can remove an `hr` during CSV cleaning,
whereas GLPI 10 can leave literal `<br>`/`<hr>` delimiters after normalization;
rich-text conversion may also add Markdown-like punctuation. Such differences
still reach the C# comparison. These fixtures prove the DOM extraction cases,
not equivalence for every GLPI export or resolution of an unprovided CSV.

The fixture also checks that status icons do not duplicate their adjacent
labels, priority decoration does not add text, and visible IDs in titles are
preserved. Sources for the rendered structure:

- [GLPI 9.5.13 Search::showItem](https://github.com/glpi-project/glpi/blob/9.5.13/inc/search.class.php#L7389-L7406)
- [GLPI 9.5.13 Html::showToolTip](https://github.com/glpi-project/glpi/blob/9.5.13/inc/html.class.php#L3682-L3714)
- [GLPI 10.0.22 Search::showItem](https://github.com/glpi-project/glpi/blob/10.0.22/src/Search.php#L8898-L8918)
- [GLPI 10.0.22 Html::showToolTip](https://github.com/glpi-project/glpi/blob/10.0.22/src/Html.php#L3612-L3645)
- [GLPI 9.5.13 hidden popup CSS](https://github.com/glpi-project/glpi/blob/9.5.13/css/styles.scss#L294-L296)
- [GLPI 10.0.22 hidden popup CSS](https://github.com/glpi-project/glpi/blob/10.0.22/css/legacy/includes/_styles.scss#L131-L133)
- [GLPI 9.5.13 user rendering and nested information cards](https://github.com/glpi-project/glpi/blob/9.5.13/inc/search.class.php#L5621-L5706)
- [GLPI 9.5.13 user-card line breaks](https://github.com/glpi-project/glpi/blob/9.5.13/inc/dbutils.class.php#L1547-L1553)
- [GLPI 9.5.13 long-text rendering](https://github.com/glpi-project/glpi/blob/9.5.13/inc/search.class.php#L6357-L6382)
- [GLPI 10.0.22 long-text rendering](https://github.com/glpi-project/glpi/blob/10.0.22/src/Search.php#L7663-L7682)
- [GLPI 10.0.22 exact abbreviation suffix](https://github.com/glpi-project/glpi/blob/10.0.22/src/Html.php#L282-L289)
- [GLPI 10.0.22 generated read-more control](https://github.com/glpi-project/glpi/blob/10.0.22/src/RichText/RichText.php#L288-L327)
- [GLPI 9.5.13 CSV cleaning order](https://github.com/glpi-project/glpi/blob/9.5.13/inc/search.class.php#L7370-L7375)
- [GLPI 10.0.22 CSV cleaning order](https://github.com/glpi-project/glpi/blob/10.0.22/src/Search.php#L8853-L8857)

## Run on Windows

Install Node.js, then run in PowerShell from this folder:

```powershell
npm install
$env:GLPI_TEST_CHANNEL = "msedge"
node .\run.cjs .\result-local.log
```

Alternatively set an installed browser executable explicitly:

```powershell
$env:GLPI_TEST_CHROMIUM = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
node .\run.cjs .\result-local.log
```

`GLPI_TEST_CHROMIUM` takes precedence over `GLPI_TEST_CHANNEL`. With neither
variable, the Playwright bundled Chromium installation is used; install it
with `npx playwright install chromium` if needed.

CI can instead reuse the driver package restored by the .NET integration tests,
without installing another Node dependency. After building `AutomationChecks`,
run from the repository root in PowerShell:

```powershell
$driver = (Resolve-Path 'tests/AutomationChecks/bin/Release/net10.0/.playwright').Path
$env:GLPI_TEST_PLAYWRIGHT_MODULE = Join-Path $driver 'package'
$node = Join-Path $driver 'node/win32_x64/node.exe'
& $node tests/NativeDomChecks/run.cjs result-native-dom.log
```

Keep `GLPI_TEST_CHROMIUM` set to the test browser. This option uses the exact
Microsoft.Playwright driver version restored by the .NET project. It creates
an ephemeral browser context and never opens the application's persistent profile.

## Run on Linux

```bash
GLPI_TEST_CHROMIUM=/path/to/chromium node tests/NativeDomChecks/run.cjs tests/NativeDomChecks/result-local.log
```

Run that command from the repository root. A raw result from the development
environment for adapter 1.1.1 is included as `result-20261021-v111.log`; it records
the actual runtime versions and 82 successful checks. The earlier adapter's
result remains in `result-20261021.log`. This is fixture validation, not testing
against a real GLPI instance. Browser binaries and `node_modules` are not
included in the source deliverable.
