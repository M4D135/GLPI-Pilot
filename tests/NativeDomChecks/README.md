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
