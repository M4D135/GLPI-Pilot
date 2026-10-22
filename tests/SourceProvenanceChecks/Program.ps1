#requires -Version 7.2
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repository 'build-tools/SourceProvenance.ps1')
$gitPath = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('GLPI-Pilot-Source-Checks-' + [guid]::NewGuid().ToString('N'))
$utf8 = [Text.UTF8Encoding]::new($false)
$savedExpected = $env:GITHUB_SHA
$script:passed = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }; $script:passed++; Write-Host ('PASS ' + $Message)
}
function Reject([scriptblock]$Action, [string]$Expected, [string]$Message) {
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_.Exception.Message }
    if ($null -eq $caught -or -not $caught.Contains($Expected)) { throw ($Message + '; unexpected result: ' + $caught) }
    Check $true $Message
}
function Git([string]$Root, [string[]]$Arguments) {
    $result = Invoke-GlpiBuildGit -GitPath $gitPath -SourceRoot $Root -Arguments $Arguments
    if ($result.Code -ne 0) { throw ('Fixture Git command failed: ' + ($Arguments -join ' ')) }
    return $result.Output.Trim()
}
function Put([string]$Root, [string]$Relative, [string]$Content) {
    $path = Join-Path $Root $Relative
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, $Content, $utf8)
}
function Init-Git([string]$Root) {
    [void](Git $Root @('init', '--quiet'))
    [void](Git $Root @('config', 'user.name', 'Provenance checks'))
    [void](Git $Root @('config', 'user.email', 'provenance-checks@example.invalid'))
    [void](Git $Root @('config', 'core.autocrlf', 'false'))
    [void](Git $Root @('config', 'core.safecrlf', 'false'))
}
function Commit([string]$Root) {
    [void](Git $Root @('add', '--all'))
    [void](Git $Root @('-c', 'commit.gpgsign=false', 'commit', '--quiet', '-m', 'Local provenance fixture'))
}
try {
    $env:GITHUB_SHA = $null
    [void][IO.Directory]::CreateDirectory($temporary)
    foreach ($relative in @('build.ps1', 'build-tools/SourceProvenance.ps1')) {
        $tokens = $null; $errors = $null
        [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $repository $relative), [ref]$tokens, [ref]$errors)
        Check ($errors.Count -eq 0) ('PowerShell parser accepts ' + $relative)
    }
    $source = Join-Path $temporary 'committed'
    Put $source 'README.md' "Local fixture`n"
    Put $source '.gitignore' "bin/`nobj/`nartifacts/`n"
    Put $source 'build.ps1' "# Local fixture`n"
    Put $source 'global.json' '{"sdk":{"version":"10.0.401","rollForward":"latestPatch"}}'
    Put $source 'src/Fichier à espaces.cs' "class Fixture { }`n"
    Init-Git $source; Commit $source
    $original = Resolve-GlpiBuildProvenance -SourceRoot $source
    Check ($original.Origin -ceq 'Git' -and $original.Commit -ceq (Git $source @('rev-parse', 'HEAD')) -and $original.Tree -ceq (Git $source @('rev-parse', 'HEAD^{tree}'))) 'Clean Git sources resolve their own commit and tree'
    Check ($original.Files.Count -eq 5 -and $original.Files.ContainsKey('src/Fichier à espaces.cs')) 'Exact inventory preserves UTF-8 paths and spaces'
    Put $source 'src/bin/generated.dll' 'ignored build output'
    Put $source 'artifacts/generated.log' 'ignored packaging output'
    Check ((Resolve-GlpiBuildProvenance -SourceRoot $source).Files.Count -eq 5) 'Generated build outputs do not contaminate the source inventory'
    Put $source 'src/Fichier à espaces.cs' "class Changed { }`n"
    Reject { Resolve-GlpiBuildProvenance -SourceRoot $source } 'modifiées' 'Edited tracked source cannot claim the existing commit'
    [void](Git $source @('add', '--all'))
    Reject { Resolve-GlpiBuildProvenance -SourceRoot $source } 'modifiées' 'Staged source without a new commit is refused'
    [void](Git $source @('reset', '--hard', 'HEAD'))
    [void](Git $source @('update-index', '--assume-unchanged', 'src/Fichier à espaces.cs'))
    Put $source 'src/Fichier à espaces.cs' "class HiddenChange { }`n"
    Reject { Resolve-GlpiBuildProvenance -SourceRoot $source } 'Source Git modifiée' 'Assume-unchanged cannot hide an edited build source'
    Remove-Item -LiteralPath (Join-Path $source 'src/Fichier à espaces.cs')
    Reject { Resolve-GlpiBuildProvenance -SourceRoot $source } 'Source du commit manquante' 'Assume-unchanged cannot hide a missing source from the committed inventory'
    [void](Git $source @('update-index', '--no-assume-unchanged', 'src/Fichier à espaces.cs'))
    [void](Git $source @('reset', '--hard', 'HEAD'))
    [void](Git $source @('update-index', '--skip-worktree', 'src/Fichier à espaces.cs'))
    Remove-Item -LiteralPath (Join-Path $source 'src/Fichier à espaces.cs')
    Reject { Resolve-GlpiBuildProvenance -SourceRoot $source } 'Source du commit manquante' 'Skip-worktree cannot hide a missing source from the committed inventory'
    [void](Git $source @('update-index', '--no-skip-worktree', 'src/Fichier à espaces.cs'))
    [void](Git $source @('reset', '--hard', 'HEAD'))
    Put $source 'src/Unexpected.cs' 'class Unexpected { }'
    Reject { Resolve-GlpiBuildProvenance -SourceRoot $source } 'Source absente du commit' 'Untracked build input cannot inherit a commit'
    Remove-Item -LiteralPath (Join-Path $source 'src/Unexpected.cs')
    $env:GITHUB_SHA = ('0' * 40)
    Reject { Resolve-GlpiBuildProvenance -SourceRoot $source } 'ne correspond pas' 'GITHUB_SHA must agree with verified Git provenance'
    $env:GITHUB_SHA = 'not-a-commit'
    Reject { Resolve-GlpiBuildProvenance -SourceRoot $source } 'commit valide' 'Malformed GITHUB_SHA cannot silently fall back to Git'
    $env:GITHUB_SHA = $original.Commit.ToUpperInvariant()
    Check ((Resolve-GlpiBuildProvenance -SourceRoot $source).Commit -ceq $original.Commit) 'Matching GITHUB_SHA is normalized without replacing Git evidence'
    $env:GITHUB_SHA = $null
    $export = Join-Path $temporary 'exported'
    Export-GlpiBuildSources -Provenance $original -Destination $export
    $snapshot = Resolve-GlpiBuildProvenance -SourceRoot $export
    Check ($snapshot.Origin -ceq 'Export' -and $snapshot.Commit -ceq $original.Commit -and $snapshot.Tree -ceq $original.Tree -and $snapshot.Files.Count -eq $original.Files.Count -and -not (Test-Path -LiteralPath (Join-Path $export '.git'))) 'Export without .git retains the verified original source identity'
    $savedPath = $env:PATH
    try {
        $env:PATH = ''
        $withoutGit = Resolve-GlpiBuildProvenance -SourceRoot $export
        Check (-not (Get-Command git -CommandType Application -ErrorAction SilentlyContinue) -and $withoutGit.Commit -ceq $original.Commit) 'Verified DEV provenance works when Git is unavailable'
    } finally { $env:PATH = $savedPath }
    $manifestPath = Join-Path $export 'GLPI-Pilot.sources.json'
    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    Check (-not $snapshot.Files.ContainsKey('GLPI-Pilot.sources.json') -and [IO.File]::ReadAllText((Join-Path $export 'global.json')) -ceq [IO.File]::ReadAllText((Join-Path $source 'global.json'))) 'Manifest avoids circular hashing and preserves the pinned SDK'
    $secondExport = Join-Path $temporary 'second-export'
    Export-GlpiBuildSources -Provenance $snapshot -Destination $secondExport
    Check ((Resolve-GlpiBuildProvenance -SourceRoot $secondExport).Commit -ceq $original.Commit) 'Verified exported sources can be exported again without Git metadata'
    $env:GITHUB_SHA = ('0' * 40)
    Reject { Resolve-GlpiBuildProvenance -SourceRoot $export } 'ne correspond pas' 'GITHUB_SHA cannot override an exported source identity'
    $env:GITHUB_SHA = $null
    $missing = Join-Path $temporary 'without-manifest'
    Put $missing 'build.ps1' '# Missing evidence'
    Reject { Resolve-GlpiBuildProvenance -SourceRoot $missing } 'Provenance absente' 'A source ZIP without Git or the DEV manifest is refused'
    Put $export 'src/Fichier à espaces.cs' 'class Tampered { }'
    Reject { Resolve-GlpiBuildProvenance -SourceRoot $export } 'modifiée ou manquante' 'Edited exported source must receive a new commit'
    Copy-Item -LiteralPath (Join-Path $source 'src/Fichier à espaces.cs') -Destination (Join-Path $export 'src/Fichier à espaces.cs') -Force
    Put $export 'injected.props' '<Project />'
    Reject { Resolve-GlpiBuildProvenance -SourceRoot $export } 'ajoutés ou manquants' 'Unexpected files in exported sources are refused'
    Remove-Item -LiteralPath (Join-Path $export 'injected.props')
    Remove-Item -LiteralPath (Join-Path $export 'README.md')
    Reject { Resolve-GlpiBuildProvenance -SourceRoot $export } 'ajoutés ou manquants' 'Missing exported files are refused'
    Copy-Item -LiteralPath (Join-Path $source 'README.md') -Destination $export
    [IO.File]::WriteAllBytes($manifestPath, [byte[]]@(0xff, 0xfe, 0x00))
    Reject { Resolve-GlpiBuildProvenance -SourceRoot $export } 'illisible ou JSON invalide' 'Invalid UTF-8 source manifest is refused'
    [IO.File]::WriteAllBytes($manifestPath, [byte[]]::new(8MB + 1))
    Reject { Resolve-GlpiBuildProvenance -SourceRoot $export } 'trop volumineux' 'Source manifest has a strict size bound'
    foreach ($badPath in @('../escape.cs', '/absolute.cs', 'src\windows.cs', 'GLPI-Pilot.sources.json', 'src/NUL.cs')) {
        $document = $utf8.GetString($manifestBytes) | ConvertFrom-Json -AsHashtable
        $document.files[0].path = $badPath
        [IO.File]::WriteAllText($manifestPath, ($document | ConvertTo-Json -Depth 6), $utf8)
        Reject { Resolve-GlpiBuildProvenance -SourceRoot $export } 'Chemin invalide' ('Unsafe manifest path is refused: ' + $badPath)
    }
    $document = $utf8.GetString($manifestBytes) | ConvertFrom-Json -AsHashtable
    $document.files += @{ path = $document.files[0].path.ToUpperInvariant(); sha256 = $document.files[0].sha256 }
    [IO.File]::WriteAllText($manifestPath, ($document | ConvertTo-Json -Depth 6), $utf8)
    Reject { Resolve-GlpiBuildProvenance -SourceRoot $export } 'Chemin dupliqué' 'Case-ambiguous Windows inventory entries are refused'
    [IO.File]::WriteAllBytes($manifestPath, $manifestBytes)
    Put $export '.git' 'gitdir: missing-repository'
    Reject { Resolve-GlpiBuildProvenance -SourceRoot $export } 'vérifiable avec Git' 'Broken own Git metadata cannot silently fall back to an old export manifest'
    Remove-Item -LiteralPath (Join-Path $export '.git') -Force
    $foreign = Join-Path $temporary 'foreign'
    Put $foreign 'README.md' 'Different project'
    Init-Git $foreign; Commit $foreign
    $foreignCommit = Git $foreign @('rev-parse', 'HEAD')
    $foreignChild = Join-Path $foreign 'child'
    Put $foreignChild 'build.ps1' '# Source with no provenance'
    Reject { Resolve-GlpiBuildProvenance -SourceRoot $foreignChild } 'dossier parent' 'An ancestor repository is never used as the source commit'
    $nested = Join-Path $foreign 'valid-export'
    Export-GlpiBuildSources -Provenance $original -Destination $nested
    $nestedIdentity = Resolve-GlpiBuildProvenance -SourceRoot $nested
    Check ($nestedIdentity.Origin -ceq 'Export' -and $nestedIdentity.Commit -ceq $original.Commit -and $nestedIdentity.Commit -cne $foreignCommit) 'A verified DEV export stays portable inside an unrelated repository'
    $linked = Join-Path $temporary 'linked-export'
    Export-GlpiBuildSources -Provenance $original -Destination $linked
    $linkPath = Join-Path $linked 'src'
    Remove-Item -LiteralPath $linkPath -Recurse -Force
    $linkType = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $linkType -Path $linkPath -Target (Join-Path $source 'src') | Out-Null
    Reject { Resolve-GlpiBuildProvenance -SourceRoot $linked } 'Lien ou jonction interdit' 'Links and junctions cannot replace source folders'
    Remove-Item -LiteralPath $linkPath -Force
    Init-Git $export
    Put $export 'src/Fichier à espaces.cs' 'class NewVersion { }'
    Commit $export
    $newIdentity = Resolve-GlpiBuildProvenance -SourceRoot $export
    Check ($newIdentity.Origin -ceq 'Git' -and $newIdentity.Commit -cne $original.Commit -and $newIdentity.Commit -ceq (Git $export @('rev-parse', 'HEAD'))) 'Edited DEV sources with their own new commit receive the new identity'
    $crlfRoot = Join-Path $temporary 'crlf'
    Put $crlfRoot 'README.md' "Line one`nLine two`n"
    Init-Git $crlfRoot
    [void](Git $crlfRoot @('config', 'core.autocrlf', 'true'))
    Commit $crlfRoot
    Put $crlfRoot 'README.md' "Line one`r`nLine two`r`n"
    $crlfIdentity = Resolve-GlpiBuildProvenance -SourceRoot $crlfRoot
    $crlfExport = Join-Path $temporary 'crlf-export'
    Export-GlpiBuildSources -Provenance $crlfIdentity -Destination $crlfExport
    Check ((Resolve-GlpiBuildProvenance -SourceRoot $crlfExport).Commit -ceq $crlfIdentity.Commit -and [IO.File]::ReadAllText((Join-Path $crlfExport 'README.md')).Contains("`r`n")) 'Clean Git CRLF conversion is accepted and the export retains its exact bytes'
    Put $source 'README.md' 'Changed after the initial provenance snapshot'
    Reject { Export-GlpiBuildSources -Provenance $original -Destination (Join-Path $temporary 'stale-export') } 'modifiées' 'Changed sources are rechecked before copying into DEV'
    Write-Host ($script:passed.ToString() + '/' + $script:passed + ' source provenance checks passed.')
} finally {
    $env:GITHUB_SHA = $savedExpected
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
