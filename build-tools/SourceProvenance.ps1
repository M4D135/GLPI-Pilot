# Local source provenance, independent of Git availability in an exported DEV pack.
# The exported inventory detects changes; it is not a signature or an authenticity proof.
Set-StrictMode -Version Latest

function Assert-GlpiBuildSourcePath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw ('Lien ou jonction interdit dans les sources : ' + $current)
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

function Assert-GlpiBuildRelativePath([string]$Path) {
    if ($Path.Length -gt 400 -or $Path -eq 'GLPI-Pilot.sources.json' -or $Path.Contains('\') -or $Path -match '[\x00-\x1f<>:"|?*]' -or $Path.StartsWith('/') -or
        @($Path.Split('/') | Where-Object { $_ -in @('', '.', '..') -or $_.EndsWith('.') -or $_.EndsWith(' ') -or $_ -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)' }).Count -gt 0) {
        throw 'Chemin invalide dans les sources ou leur manifeste.'
    }
}

function Test-GlpiBuildSourceSelection([string]$Path) {
    $parts = $Path.Split('/')
    if ($parts.Count -eq 1) {
        return ($Path -in @('README.md', 'build.ps1', '.gitignore', 'global.json', 'NuGet.Config', 'nuget.config', 'Directory.Packages.props', 'package.json', 'package-lock.json') -or $Path -like 'Directory.Build.*')
    }
    if ($parts[0] -notin @('src', 'tests', 'web', 'docs', 'support', 'licenses', '.github', 'build-tools')) { return $false }
    foreach ($part in $parts[0..($parts.Count - 2)]) { if ($part -in @('.git', 'bin', 'obj', 'node_modules', 'artifacts', '__pycache__')) { return $false } }
    return ($parts[0] -ine 'build-tools' -or [IO.Path]::GetExtension($Path) -in @('.py', '.ps1', '.sh', '.json', '.sha256'))
}

function Get-GlpiBuildSourceFiles {
    param([Parameter(Mandatory)][string]$SourceRoot, [switch]$WholeSnapshot)
    $root = [IO.Path]::GetFullPath($SourceRoot); Assert-GlpiBuildSourcePath $root
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'Dossier de sources introuvable.' }
    $files = [Collections.Generic.Dictionary[string,IO.FileInfo]]::new([StringComparer]::Ordinal)
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $pending = [Collections.Generic.Stack[string]]::new()
    $folders = @('src', 'tests', 'web', 'docs', 'support', 'licenses', '.github', 'build-tools')
    foreach ($entry in Get-ChildItem -LiteralPath $root -Force) {
        if ($entry.Name -eq 'GLPI-Pilot.sources.json' -or $entry.Name -in @('.git', 'bin', 'obj', 'node_modules', 'artifacts', '__pycache__')) { continue }
        if ($entry.PSIsContainer) {
            if ($WholeSnapshot -or $entry.Name -in $folders) { $pending.Push($entry.FullName) }
        } elseif ($WholeSnapshot -or (Test-GlpiBuildSourceSelection $entry.Name)) {
            Assert-GlpiBuildSourcePath $entry.FullName
            Assert-GlpiBuildRelativePath $entry.Name
            if (-not $names.Add($entry.Name)) { throw 'Chemins de sources ambigus pour Windows.' }
            $files.Add($entry.Name, $entry)
        }
    }
    $directories = 0
    while ($pending.Count) {
        $folder = $pending.Pop(); Assert-GlpiBuildSourcePath $folder
        if (++$directories -gt 5000) { throw 'Trop de dossiers de sources.' }
        foreach ($entry in Get-ChildItem -LiteralPath $folder -Force) {
            if ($entry.PSIsContainer -and $entry.Name -in @('.git', 'bin', 'obj', 'node_modules', 'artifacts', '__pycache__')) { continue }
            Assert-GlpiBuildSourcePath $entry.FullName
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName); continue }
            $relative = [IO.Path]::GetRelativePath($root, $entry.FullName).Replace('\', '/')
            if (-not $WholeSnapshot -and -not (Test-GlpiBuildSourceSelection $relative)) { continue }
            Assert-GlpiBuildRelativePath $relative
            if (-not $names.Add($relative)) { throw 'Chemins de sources ambigus pour Windows.' }
            $files.Add($relative, $entry)
            if ($files.Count -gt 10000) { throw 'Trop de fichiers de sources.' }
        }
    }
    return ,$files
}

function Invoke-GlpiBuildGit {
    param([Parameter(Mandatory)][string]$GitPath, [Parameter(Mandatory)][string]$SourceRoot, [string[]]$Arguments, [string]$InputText)
    $start = [Diagnostics.ProcessStartInfo]::new($GitPath)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $start.Environment['GIT_OPTIONAL_LOCKS'] = '0'
    $start.Environment['GIT_NO_LAZY_FETCH'] = '1'; $start.Environment['GIT_TERMINAL_PROMPT'] = '0'
    foreach ($name in @('GIT_DIR', 'GIT_WORK_TREE', 'GIT_COMMON_DIR', 'GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES')) { [void]$start.Environment.Remove($name) }
    $start.RedirectStandardInput = $PSBoundParameters.ContainsKey('InputText')
    if ($start.RedirectStandardInput) { $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false) }
    $start.ArgumentList.Add('-C'); $start.ArgumentList.Add($SourceRoot)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Impossible de lire la provenance Git.' }
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if ($start.RedirectStandardInput) { $process.StandardInput.Write($InputText); $process.StandardInput.Close() }
        if (-not $process.WaitForExit(30000)) { $process.Kill($true); [void]$process.WaitForExit(5000); throw 'La lecture de la provenance Git a dépassé son délai.' }
        if (-not $stdout.Wait(5000) -or -not $stderr.Wait(5000)) { throw 'La lecture de la provenance Git est incomplète.' }
        return [pscustomobject]@{ Code = $process.ExitCode; Output = $stdout.Result }
    } finally { $process.Dispose() }
}

function Read-GlpiSourceManifest {
    param([Parameter(Mandatory)][string]$SourceRoot)
    $path = Join-Path $SourceRoot 'GLPI-Pilot.sources.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Provenance absente : utilisez un dépôt Git propre ou les sources DEV avec leur manifeste intact.' }
    Assert-GlpiBuildSourcePath $path
    if ((Get-Item -LiteralPath $path).Length -gt 8MB) { throw 'Manifeste de sources trop volumineux.' }
    try { $document = [Text.UTF8Encoding]::new($false, $true).GetString([IO.File]::ReadAllBytes($path)) | ConvertFrom-Json -AsHashtable -Depth 8 -ErrorAction Stop }
    catch { throw 'Manifeste de sources illisible ou JSON invalide.' }
    $required = @('format', 'application', 'repository', 'source_commit', 'source_tree', 'files')
    if ($document -isnot [Collections.IDictionary] -or $document.Count -ne $required.Count -or @($required | Where-Object { -not $document.Contains($_) }).Count -gt 0) { throw 'Structure du manifeste de sources invalide.' }
    if ($document.format -ne 1 -or $document.application -cne 'GLPI Pilot' -or $document.repository -cne 'M4D135/GLPI-Pilot' -or
        $document.source_commit -isnot [string] -or $document.source_commit -cnotmatch '^[a-f0-9]{40}$' -or
        $document.source_tree -isnot [string] -or $document.source_tree -cnotmatch '^[a-f0-9]{40}$' -or
        $document.files -isnot [array] -or $document.files.Count -lt 1 -or $document.files.Count -gt 10000) { throw 'Provenance du manifeste de sources invalide.' }
    $inventory = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $document.files) {
        if ($entry -isnot [Collections.IDictionary] -or $entry.Count -ne 2 -or -not $entry.Contains('path') -or -not $entry.Contains('sha256') -or $entry.path -isnot [string] -or $entry.sha256 -isnot [string]) { throw 'Entrée du manifeste de sources invalide.' }
        $name = $entry.path
        Assert-GlpiBuildRelativePath $name
        if ($entry.sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Empreinte invalide dans le manifeste de sources.' }
        if (-not $names.Add($name)) { throw 'Chemin dupliqué dans le manifeste de sources.' }
        $inventory.Add($name, $entry.sha256)
    }
    $actual = Get-GlpiBuildSourceFiles -SourceRoot $SourceRoot -WholeSnapshot
    if ($actual.Count -ne $inventory.Count) { throw 'Les sources exportées contiennent des fichiers ajoutés ou manquants.' }
    foreach ($name in $inventory.Keys) {
        if (-not $actual.ContainsKey($name) -or (Get-FileHash -LiteralPath $actual[$name].FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $inventory[$name]) {
            throw ('Source exportée modifiée ou manquante : ' + $name + '. Créez un nouveau commit pour distribuer vos changements.')
        }
    }
    return [pscustomobject]@{ Root = [IO.Path]::GetFullPath($SourceRoot); Commit = $document.source_commit; Tree = $document.source_tree; Origin = 'Export'; Files = $inventory }
}

function Resolve-GlpiBuildProvenance {
    param([Parameter(Mandatory)][string]$SourceRoot, [string]$ExpectedCommit = $env:GITHUB_SHA)
    $root = [IO.Path]::GetFullPath($SourceRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    Assert-GlpiBuildSourcePath $root
    if ($ExpectedCommit -and $ExpectedCommit -notmatch '^[a-fA-F0-9]{40}$') { throw 'GITHUB_SHA ne contient pas un commit valide.' }
    $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    $top = $null
    if ($git) { $top = Invoke-GlpiBuildGit -GitPath $git.Source -SourceRoot $root -Arguments @('rev-parse', '--show-toplevel') }
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if ($top -and $top.Code -eq 0 -and [IO.Path]::GetFullPath($top.Output.Trim()).TrimEnd([IO.Path]::DirectorySeparatorChar).Equals($root, $comparison)) {
        $clean = Invoke-GlpiBuildGit -GitPath $git.Source -SourceRoot $root -Arguments @('diff', '--no-ext-diff', '--no-textconv', '--quiet', 'HEAD', '--')
        if ($clean.Code -ne 0) { throw 'Sources Git modifiées ou non validables : créez un commit avant de construire une distribution.' }
        $head = Invoke-GlpiBuildGit -GitPath $git.Source -SourceRoot $root -Arguments @('rev-parse', 'HEAD')
        $tree = Invoke-GlpiBuildGit -GitPath $git.Source -SourceRoot $root -Arguments @('rev-parse', 'HEAD^{tree}')
        $tracked = Invoke-GlpiBuildGit -GitPath $git.Source -SourceRoot $root -Arguments @('ls-tree', '-r', '-z', 'HEAD')
        if ($head.Code -ne 0 -or $tree.Code -ne 0 -or $tracked.Code -ne 0 -or $head.Output.Trim() -cnotmatch '^[a-f0-9]{40}$' -or $tree.Output.Trim() -cnotmatch '^[a-f0-9]{40}$') { throw 'Commit ou arbre Git indisponible.' }
        $trackedBlobs = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
        foreach ($record in $tracked.Output.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
            $separator = $record.IndexOf([char]9)
            if ($separator -lt 0) { throw 'Inventaire Git invalide.' }
            $name = $record.Substring($separator + 1)
            if (-not (Test-GlpiBuildSourceSelection $name)) { continue }
            Assert-GlpiBuildRelativePath $name
            if ($record.Substring(0, $separator) -cnotmatch '^100(?:644|755) blob ([a-f0-9]{40})$') { throw ('Type Git non pris en charge dans les sources : ' + $name) }
            $trackedBlobs.Add($name, $Matches[1])
        }
        $actual = Get-GlpiBuildSourceFiles -SourceRoot $root
        if ($actual.Count -eq 0) { throw 'Aucune source distribuable.' }
        foreach ($name in $trackedBlobs.Keys) {
            if (-not $actual.ContainsKey($name)) { throw ('Source du commit manquante : ' + $name + '. Restaurez-la ou créez un nouveau commit.') }
        }
        $paths = @($actual.Keys | Sort-Object -CaseSensitive)
        # Hash the actual worktree instead of trusting index stat/assume-unchanged
        # flags. Git applies the checkout's text filters, including CRLF rules.
        $blobs = Invoke-GlpiBuildGit -GitPath $git.Source -SourceRoot $root -Arguments @('hash-object', '--stdin-paths') -InputText (($paths -join "`n") + "`n")
        $actualBlobs = @($blobs.Output.TrimEnd("`r", "`n").Split("`n") | ForEach-Object { $_.TrimEnd("`r") })
        if ($blobs.Code -ne 0 -or $actualBlobs.Count -ne $paths.Count) { throw 'Impossible de comparer les sources au commit Git.' }
        $inventory = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
        for ($index = 0; $index -lt $paths.Count; $index++) {
            $name = $paths[$index]
            if (-not $trackedBlobs.ContainsKey($name)) { throw ('Source absente du commit : ' + $name + '. Ajoutez-la à un nouveau commit avant la distribution.') }
            if ($actualBlobs[$index] -cne $trackedBlobs[$name]) { throw ('Source Git modifiée : ' + $name + '. Créez un nouveau commit avant la distribution.') }
            $inventory.Add($name, (Get-FileHash -LiteralPath $actual[$name].FullName -Algorithm SHA256).Hash.ToLowerInvariant())
        }
        $value = [pscustomobject]@{ Root = $root; Commit = $head.Output.Trim(); Tree = $tree.Output.Trim(); Origin = 'Git'; Files = $inventory }
    } else {
        if (Test-Path -LiteralPath (Join-Path $root '.git')) { throw 'Le dépôt de sources doit être vérifiable avec Git avant construction.' }
        # A valid export is portable even inside an unrelated repository. Its
        # manifest is checked independently; the ancestor commit is never used.
        if ($top -and $top.Code -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $root 'GLPI-Pilot.sources.json'))) { throw 'Le dépôt Git trouvé appartient à un dossier parent, pas à ces sources. Manifeste DEV requis.' }
        $value = Read-GlpiSourceManifest -SourceRoot $root
    }
    if ($ExpectedCommit -and $value.Commit -cne $ExpectedCommit.ToLowerInvariant()) { throw 'GITHUB_SHA ne correspond pas à la provenance vérifiée des sources.' }
    return $value
}

function Export-GlpiBuildSources {
    param([Parameter(Mandatory)]$Provenance, [Parameter(Mandatory)][string]$Destination)
    $fresh = Resolve-GlpiBuildProvenance -SourceRoot $Provenance.Root -ExpectedCommit $Provenance.Commit
    if ($fresh.Tree -cne $Provenance.Tree -or $fresh.Files.Count -ne $Provenance.Files.Count) { throw 'Les sources ont changé pendant la construction.' }
    foreach ($name in $Provenance.Files.Keys) {
        if (-not $fresh.Files.ContainsKey($name) -or $fresh.Files[$name] -cne $Provenance.Files[$name]) { throw 'Les sources ont changé pendant la construction.' }
    }
    Assert-GlpiBuildSourcePath $Destination
    if (Test-Path -LiteralPath $Destination) { throw 'Le dossier des sources exportées doit être nouveau.' }
    [void][IO.Directory]::CreateDirectory($Destination)
    $entries = [Collections.Generic.List[object]]::new()
    foreach ($name in @($Provenance.Files.Keys | Sort-Object -CaseSensitive)) {
        $target = Join-Path $Destination $name
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        [IO.File]::Copy((Join-Path $Provenance.Root $name), $target, $false)
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Provenance.Files[$name]) { throw ('Copie de source altérée : ' + $name) }
        $entries.Add([ordered]@{ path = $name; sha256 = $Provenance.Files[$name] })
    }
    $manifest = [ordered]@{ format = 1; application = 'GLPI Pilot'; repository = 'M4D135/GLPI-Pilot'; source_commit = $Provenance.Commit; source_tree = $Provenance.Tree; files = $entries.ToArray() }
    [IO.File]::WriteAllText((Join-Path $Destination 'GLPI-Pilot.sources.json'), (($manifest | ConvertTo-Json -Depth 6) + [char]10), [Text.UTF8Encoding]::new($false))
    [void](Read-GlpiSourceManifest -SourceRoot $Destination)
}
