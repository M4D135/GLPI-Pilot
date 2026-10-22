# Shared, network-free verification for the fixed runtime prepared outside the offline VM.
# Dot-sourcing this file performs no I/O and does not download or execute a runtime.
Set-StrictMode -Version Latest

function Assert-WebView2PlainPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw ('Un lien ou une jonction traverse le chemin du runtime : ' + $current)
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

function Get-WebView2PinnedManifest {
    $path = Join-Path $PSScriptRoot 'WebView2Runtime.json'
    $manifest = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 8
    if ($manifest.version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $manifest.architecture -ne 'x64' -or
        $manifest.sha256 -notmatch '^[a-f0-9]{64}$' -or $manifest.files_sha256 -notmatch '^[a-f0-9]{64}$' -or
        $manifest.bytes -le 0 -or $manifest.bytes -gt 768MB -or $manifest.expanded_bytes -gt 2GB -or
        $manifest.file_count -lt 1 -or $manifest.file_count -gt 20000) { throw 'Manifeste du runtime fixe invalide.' }
    $uri = [Uri]$manifest.url
    $expectedName = 'Microsoft.WebView2.FixedVersionRuntime.' + $manifest.version + '.x64.cab'
    if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'msedge.sf.dl.delivery.mp.microsoft.com' -or
        $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.Port -ne 443 -or
        $uri.AbsolutePath -notmatch '^/filestreamingservice/files/[a-f0-9-]{36}/' -or
        [IO.Path]::GetFileName($uri.AbsolutePath) -ne $expectedName) { throw 'Le runtime ne provient pas de l''URL Microsoft épinglée.' }
    $index = Join-Path $PSScriptRoot 'WebView2Runtime.files.sha256'
    $indexText = [IO.File]::ReadAllText($index).Replace("`r`n", "`n")
    $indexHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($indexText))).ToLowerInvariant()
    if ($indexHash -ne $manifest.files_sha256) {
        throw 'L''inventaire des fichiers du runtime a été modifié.'
    }
    return $manifest
}

function Get-WebView2PinnedFiles {
    $manifest = Get-WebView2PinnedManifest
    $files = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in [IO.File]::ReadAllLines((Join-Path $PSScriptRoot 'WebView2Runtime.files.sha256'))) {
        if ($line -notmatch '^([a-f0-9]{64})  (.+)$') { throw 'Une empreinte de fichier du runtime est invalide.' }
        $hash = $Matches[1]; $relative = $Matches[2]
        if ($relative.Length -gt 200 -or $relative.Contains('\') -or $relative -match '[\x00-\x1f<>:"|?*]' -or
            $relative.StartsWith('/') -or @($relative.Split('/') | Where-Object { $_ -in @('', '.', '..') -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count) {
            throw 'Un chemin de l''inventaire du runtime est invalide.'
        }
        if (-not $files.TryAdd($relative, $hash)) { throw 'Un chemin de l''inventaire du runtime est dupliqué.' }
    }
    if ($files.Count -ne $manifest.file_count -or -not $files.ContainsKey('msedgewebview2.exe')) { throw 'Inventaire du runtime incomplet.' }
    return ,$files
}

function Resolve-WebView2RuntimePath([string]$RuntimePath) {
    if (-not $RuntimePath) {
        if ($env:GLPI_WEBVIEW2_RUNTIME) { $RuntimePath = $env:GLPI_WEBVIEW2_RUNTIME }
        elseif ($IsWindows -and (Test-Path -LiteralPath 'C:\DomeniaDev\cache\webview2\runtime' -PathType Container)) { $RuntimePath = 'C:\DomeniaDev\cache\webview2\runtime' }
        else { throw 'Runtime fixe absent. Indiquez -RuntimePath ; préparez le cache avec build-tools/Get-WebView2Runtime.ps1 sur une machine connectée.' }
    }
    $root = [IO.Path]::GetFullPath($RuntimePath); Assert-WebView2PlainPath $root
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw ('Dossier RuntimePath introuvable : ' + $root) }
    if (Test-Path -LiteralPath (Join-Path $root 'msedgewebview2.exe') -PathType Leaf) { return $root }
    # A cache may contain the CAB's one enclosing directory. Do not search the machine.
    $matches = @(Get-ChildItem -LiteralPath $root -Directory -Force | Where-Object {
        Assert-WebView2PlainPath $_.FullName
        Test-Path -LiteralPath (Join-Path $_.FullName 'msedgewebview2.exe') -PathType Leaf
    })
    if ($matches.Count -ne 1) { throw 'RuntimePath doit contenir directement le runtime, ou un unique sous-dossier de runtime extrait.' }
    return $matches[0].FullName
}

function Test-WebView2Runtime([string]$RuntimePath) {
    $root = Resolve-WebView2RuntimePath $RuntimePath
    $manifest = Get-WebView2PinnedManifest; $expected = Get-WebView2PinnedFiles
    $actual = [Collections.Generic.List[IO.FileInfo]]::new()
    $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push($root); $directories = 0
    while ($pending.Count) {
        $directory = $pending.Pop(); Assert-WebView2PlainPath $directory
        if (++$directories -gt 5000) { throw 'Trop de dossiers dans le runtime.' }
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Lien interdit dans le runtime : ' + $item.FullName) }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
            else { $actual.Add($item); if ($actual.Count -gt $manifest.file_count) { throw 'Le runtime contient des fichiers supplémentaires.' } }
        }
    }
    if ($actual.Count -ne $expected.Count) { throw 'Le runtime fixe est incomplet.' }
    [long]$bytes = 0
    foreach ($file in $actual) {
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
        if (-not $expected.ContainsKey($relative) -or $file.Length -gt 768MB) { throw ('Fichier de runtime inattendu : ' + $relative) }
        if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected[$relative]) {
            throw ('Runtime altéré ou version incorrecte : ' + $relative)
        }
        $bytes += $file.Length
    }
    if ($bytes -ne $manifest.expanded_bytes) { throw 'Taille du runtime fixe incorrecte.' }
    # SHA-256 values cover the entire official x64 CAB, including its PE executable.
    # No certificate revocation service or other network is queried in an offline build.
    return [pscustomobject]@{ Path = $root; Version = $manifest.version; Architecture = 'x64'; Files = $expected.Count; Bytes = $bytes; CabSha256 = $manifest.sha256 }
}
