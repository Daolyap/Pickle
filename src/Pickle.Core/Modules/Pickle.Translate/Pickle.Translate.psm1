# Pickle.Translate: POSIX-style commands over PowerShell cmdlets. Pickle imports it on Windows with
# -ArgumentList @{ Enabled; Disabled; PreferNativeBinaries; Skip }. Imported any other way (autoload, plain pwsh) it reads
# $global:PickleTranslateOptions, and otherwise only activates on Windows. Shims return objects where that is natural
# (ls, ps, du, df, wc) and strings where POSIX tools print text (grep, cat, head, tail, find -l listings).
param([hashtable]$Options)

Set-StrictMode -Off

if (-not $Options) { $Options = Get-Variable -Name PickleTranslateOptions -Scope Global -ValueOnly -ErrorAction Ignore }
if (-not $Options) { $Options = @{ Enabled = $IsWindows } }

$script:AllShims = @(
    'alias', 'cat', 'chmod', 'clear', 'cp', 'df', 'du', 'env', 'export', 'find', 'grep', 'head', 'history', 'kill',
    'ln', 'ls', 'man', 'mkdir', 'mv', 'open', 'ps', 'rm', 'source', 'tail', 'touch', 'type', 'unalias', 'uname',
    'unset', 'wc', 'which', 'xdg-open'
)

# ───────────────────────────── helpers (not exported) ─────────────────────────────

# Parses POSIX options. $Flags: single-letter switches; $ValueFlags: letters that take a value; $Long: '--name' → letter,
# or '=key' for options with a value. Returns Opt (case-sensitive dictionary), Operands and Error.
function Read-PosixArgs {
    param([string]$Command, [object[]]$Arguments, [string]$Flags = '', [string]$ValueFlags = '', [hashtable]$Long = @{}, [switch]$NumericCount)
    $opt = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $operands = [System.Collections.Generic.List[object]]::new()
    $list = @(foreach ($a in $Arguments) { $a })
    $endOfOptions = $false
    for ($i = 0; $i -lt $list.Count; $i++) {
        $a = $list[$i]
        if ($a -isnot [string] -and $a -isnot [ValueType]) { $operands.Add($a); continue }
        $s = [string]$a
        if ($endOfOptions -or $s -eq '-' -or -not $s.StartsWith('-') -or $s.Length -lt 2) { $operands.Add($s); continue }
        if ($s -eq '--') { $endOfOptions = $true; continue }
        if ($s.StartsWith('--')) {
            $parts = $s.Split('=', 2)
            $target = $Long[$parts[0]]
            if (-not $target) { return [pscustomobject]@{ Opt = $opt; Operands = $operands; Error = "unrecognized option '$($parts[0])'" } }
            if ($target.StartsWith('=')) {
                $value = if ($parts.Count -gt 1) { $parts[1] } else { $i++; if ($i -lt $list.Count) { [string]$list[$i] } }
                $opt[$target.Substring(1)] = $value
            }
            else { $opt[$target] = $true }
            continue
        }
        if ($NumericCount -and $s -match '^-(\d+)$') { $opt['n'] = $Matches[1]; continue }
        for ($j = 1; $j -lt $s.Length; $j++) {
            $c = [string]$s[$j]
            if ($ValueFlags.Contains($c)) {
                $rest = $s.Substring($j + 1)
                if ($rest) { $opt[$c] = $rest }
                else {
                    $i++
                    if ($i -ge $list.Count) { return [pscustomobject]@{ Opt = $opt; Operands = $operands; Error = "option requires an argument -- '$c'" } }
                    $opt[$c] = [string]$list[$i]
                }
                break
            }
            if (-not $Flags.Contains($c)) { return [pscustomobject]@{ Opt = $opt; Operands = $operands; Error = "invalid option -- '$c'" } }
            $opt[$c] = $true
        }
    }
    [pscustomobject]@{ Opt = $opt; Operands = $operands; Error = $null }
}

function Get-FullPath([string]$Path) {
    if ($Path -eq '~' -or $Path.StartsWith('~/') -or $Path.StartsWith('~\')) { $Path = $HOME + $Path.Substring(1) }
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

# Existing paths for an operand: the literal path first, then wildcard expansion. Empty when nothing matches.
function Resolve-PosixPath([object]$Path) {
    if ($Path -is [System.IO.FileSystemInfo]) { return $Path.FullName }
    $p = [string]$Path
    if ($p -eq '~' -or $p.StartsWith('~/') -or $p.StartsWith('~\')) { $p = $HOME + $p.Substring(1) }
    if (Test-Path -LiteralPath $p) { return (Resolve-Path -LiteralPath $p).ProviderPath }
    if ([System.Management.Automation.WildcardPattern]::ContainsWildcardCharacters($p)) {
        @(Resolve-Path -Path $p -ErrorAction Ignore | ForEach-Object { $_.ProviderPath })
    }
}

function Get-DisplayPath([string]$FullPath) {
    $relative = [System.IO.Path]::GetRelativePath($PWD.ProviderPath, $FullPath)
    if ($relative.StartsWith('..')) { $FullPath } else { $relative }
}

function Format-Size([double]$Bytes) {
    $units = 'B', 'K', 'M', 'G', 'T', 'P'
    $i = 0
    while ($Bytes -ge 1024 -and $i -lt $units.Count - 1) { $Bytes /= 1024; $i++ }
    $inv = [System.Globalization.CultureInfo]::InvariantCulture
    if ($i -eq 0) { return [string][long]$Bytes }
    if ($Bytes -lt 10) { return [string]::Format($inv, '{0:0.0}{1}', [math]::Ceiling($Bytes * 10) / 10, $units[$i]) }
    [string]::Format($inv, '{0:0}{1}', [math]::Ceiling($Bytes), $units[$i])
}

function Test-SamePath([string]$A, [string]$B) {
    $cmp = if ($IsWindows -or $IsMacOS) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    [string]::Equals($A.TrimEnd('\', '/'), $B.TrimEnd('\', '/'), $cmp)
}

# Why a path must never be deleted (a filesystem root, the home directory or one of its ancestors), or $null.
function Get-ProtectedReason([string]$FullPath) {
    $root = [System.IO.Path]::GetPathRoot($FullPath)
    $trimmed = $FullPath.TrimEnd('\', '/')
    if (-not $trimmed -or ($root -and (Test-SamePath $FullPath $root))) { return 'it is a filesystem root' }
    $userHome = [Environment]::GetFolderPath('UserProfile')
    if (-not $userHome) { $userHome = $HOME }
    if ($userHome) {
        if (Test-SamePath $FullPath $userHome) { return 'it is your home directory' }
        $cmp = if ($IsWindows -or $IsMacOS) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
        $sep = [System.IO.Path]::DirectorySeparatorChar
        if ($userHome.TrimEnd('\', '/').StartsWith($trimmed + $sep, $cmp) -or $userHome.StartsWith($trimmed + '/', $cmp)) {
            return 'it contains your home directory'
        }
    }
    $null
}

# grep's default (basic) regex: + ? | ( ) { } are literal unless escaped. Convert to .NET syntax.
function ConvertFrom-BasicRegex([string]$Pattern) {
    $sb = [System.Text.StringBuilder]::new()
    for ($i = 0; $i -lt $Pattern.Length; $i++) {
        $c = $Pattern[$i]
        if ($c -eq '\' -and $i + 1 -lt $Pattern.Length) {
            $n = $Pattern[$i + 1]
            if ('+?|(){}'.Contains($n)) { [void]$sb.Append($n) } else { [void]$sb.Append($c).Append($n) }
            $i++
        }
        elseif ('+?|(){}'.Contains($c)) { [void]$sb.Append('\').Append($c) }
        else { [void]$sb.Append($c) }
    }
    $sb.ToString()
}

function Get-NativeCommand([string]$Name) {
    Get-Command -Name $Name -CommandType Application -ErrorAction Ignore | Select-Object -First 1
}

# ───────────────────────────── files ─────────────────────────────

function ls {
    $o = Read-PosixArgs -Command ls -Arguments $args -Flags 'lahRtSr1dAFG' -Long @{
        '--all' = 'a'; '--almost-all' = 'A'; '--human-readable' = 'h'; '--recursive' = 'R'; '--reverse' = 'r'; '--directory' = 'd'; '--color' = '=color'
    }
    if ($o.Error) { return Write-Error $o.Error }
    $opt = $o.Opt
    $paths = if ($o.Operands.Count) { $o.Operands } else { @('.') }
    $force = $opt.ContainsKey('a') -or $opt.ContainsKey('A')
    $items = [System.Collections.Generic.List[object]]::new()
    foreach ($p in $paths) {
        $resolved = @(Resolve-PosixPath $p)
        if (-not $resolved) { Write-Error "cannot access '$p': No such file or directory"; continue }
        foreach ($r in $resolved) {
            $item = Get-Item -LiteralPath $r -Force
            if ($opt.ContainsKey('d') -or -not $item.PSIsContainer) { $items.Add($item) }
            else { foreach ($child in Get-ChildItem -LiteralPath $r -Force:$force -Recurse:($opt.ContainsKey('R'))) { $items.Add($child) } }
        }
    }
    $sorted = if ($opt.ContainsKey('t')) { $items | Sort-Object -Property LastWriteTime -Descending:(-not $opt.ContainsKey('r')) }
    elseif ($opt.ContainsKey('S')) { $items | Sort-Object -Property @{ Expression = { if ($_.PSIsContainer) { 0 } else { $_.Length } } } -Descending:(-not $opt.ContainsKey('r')) }
    elseif ($opt.ContainsKey('r')) { $items | Sort-Object -Property $(if ($opt.ContainsKey('R')) { 'FullName' } else { 'Name' }) -Descending }
    elseif ($opt.ContainsKey('R')) { $items | Sort-Object -Property FullName }
    else { $items | Sort-Object -Property Name }

    if ($opt.ContainsKey('l')) {
        $inv = [System.Globalization.CultureInfo]::InvariantCulture
        foreach ($item in $sorted) {
            $mode = if ($IsWindows) { $item.Mode } else { $item.UnixMode }
            $size = if ($item.PSIsContainer) { 0 } else { $item.Length }
            $sizeText = if ($opt.ContainsKey('h')) { Format-Size $size } else { [string]$size }
            $when = $item.LastWriteTime
            $date = if ($when -lt (Get-Date).AddMonths(-6)) { $when.ToString('MMM dd  yyyy', $inv) } else { $when.ToString('MMM dd HH:mm', $inv) }
            $name = if ($opt.ContainsKey('R')) { Get-DisplayPath $item.FullName } else { $item.Name }
            if ($item.LinkTarget) { $name += " -> $($item.LinkTarget)" }
            elseif ($item.PSIsContainer) { $name += [System.IO.Path]::DirectorySeparatorChar }
            '{0} {1,8} {2} {3}' -f $mode, $sizeText, $date, $name
        }
    }
    elseif ($opt.ContainsKey('1')) {
        foreach ($item in $sorted) { if ($opt.ContainsKey('R')) { Get-DisplayPath $item.FullName } else { $item.Name } }
    }
    else { $sorted }
}

function cat {
    $o = Read-PosixArgs -Command cat -Arguments $args -Flags 'nbsAE' -Long @{ '--number' = 'n' }
    if ($o.Error) { return Write-Error $o.Error }
    $number = $o.Opt.ContainsKey('n') -or $o.Opt.ContainsKey('b')
    $n = 0
    $sources = if ($o.Operands.Count) { $o.Operands } else { @('-') }
    foreach ($source in $sources) {
        if ($source -eq '-') {
            foreach ($line in $input) { if ($number) { $n++; '{0,6}{1}{2}' -f $n, "`t", $line } else { $line } }
            continue
        }
        $resolved = @(Resolve-PosixPath $source)
        if (-not $resolved) { Write-Error "${source}: No such file or directory"; continue }
        foreach ($file in $resolved) {
            if (Test-Path -LiteralPath $file -PathType Container) { Write-Error "${source}: Is a directory"; continue }
            foreach ($line in Get-Content -LiteralPath $file) { if ($number) { $n++; '{0,6}{1}{2}' -f $n, "`t", $line } else { $line } }
        }
    }
}

function rm {
    $o = Read-PosixArgs -Command rm -Arguments $args -Flags 'rRfivd' -Long @{ '--recursive' = 'r'; '--force' = 'f'; '--verbose' = 'v'; '--interactive' = 'i'; '--dir' = 'd' }
    if ($o.Error) { return Write-Error $o.Error }
    $opt = $o.Opt
    $recursive = $opt.ContainsKey('r') -or $opt.ContainsKey('R')
    $force = $opt.ContainsKey('f')
    if (-not $o.Operands.Count) { if (-not $force) { Write-Error 'missing operand' }; return }
    foreach ($operand in $o.Operands) {
        $name = [string]$operand
        if ($name -in '.', '..' -or $name.EndsWith('/.') -or $name.EndsWith('/..')) { Write-Error "refusing to remove '.' or '..' directory: skipping '$name'"; continue }
        $resolved = @(Resolve-PosixPath $operand)
        if (-not $resolved) { if (-not $force) { Write-Error "cannot remove '$name': No such file or directory" }; continue }
        foreach ($path in $resolved) {
            $reason = Get-ProtectedReason $path
            if ($reason) { Write-Error "refusing to remove '$path': $reason"; continue }
            $item = Get-Item -LiteralPath $path -Force
            $isDir = $item.PSIsContainer -and -not $item.LinkTarget
            if ($isDir -and -not $recursive) {
                $empty = -not (Get-ChildItem -LiteralPath $path -Force | Select-Object -First 1)
                if (-not ($opt.ContainsKey('d') -and $empty)) { Write-Error "cannot remove '$name': Is a directory"; continue }
            }
            if ($opt.ContainsKey('i')) {
                $answer = Read-Host "rm: remove $(if ($isDir) { 'directory' } else { 'file' }) '$(Get-DisplayPath $path)'?"
                if ($answer -notmatch '^(y|yes)$') { continue }
            }
            Remove-Item -LiteralPath $path -Recurse:$isDir -Force
            if ($opt.ContainsKey('v')) { "removed $(if ($isDir) { 'directory ' })'$(Get-DisplayPath $path)'" }
        }
    }
}

function cp {
    $o = Read-PosixArgs -Command cp -Arguments $args -Flags 'rRfvnia' -Long @{ '--recursive' = 'r'; '--force' = 'f'; '--verbose' = 'v'; '--no-clobber' = 'n' }
    if ($o.Error) { return Write-Error $o.Error }
    $opt = $o.Opt
    if ($o.Operands.Count -lt 2) { return Write-Error 'missing destination file operand' }
    $recursive = $opt.ContainsKey('r') -or $opt.ContainsKey('R') -or $opt.ContainsKey('a')
    $dest = Get-FullPath ([string]$o.Operands[-1])
    $destIsDir = Test-Path -LiteralPath $dest -PathType Container
    $sources = $o.Operands | Select-Object -SkipLast 1
    if (@($sources).Count -gt 1 -and -not $destIsDir) { return Write-Error "target '$($o.Operands[-1])' is not a directory" }
    foreach ($source in $sources) {
        $resolved = @(Resolve-PosixPath $source)
        if (-not $resolved) { Write-Error "cannot stat '$source': No such file or directory"; continue }
        foreach ($path in $resolved) {
            $item = Get-Item -LiteralPath $path -Force
            if ($item.PSIsContainer -and -not $recursive) { Write-Error "-r not specified; omitting directory '$source'"; continue }
            $target = if ($destIsDir) { Join-Path $dest $item.Name } else { $dest }
            if ($opt.ContainsKey('n') -and (Test-Path -LiteralPath $target)) { continue }
            if ($opt.ContainsKey('i') -and (Test-Path -LiteralPath $target)) {
                if ((Read-Host "cp: overwrite '$(Get-DisplayPath $target)'?") -notmatch '^(y|yes)$') { continue }
            }
            if ($item.PSIsContainer -and (Test-Path -LiteralPath $target -PathType Container)) {
                Get-ChildItem -LiteralPath $path -Force | Copy-Item -Destination $target -Recurse -Force
            }
            else {
                Copy-Item -LiteralPath $path -Destination $target -Recurse:($item.PSIsContainer) -Force
            }
            if ($opt.ContainsKey('v')) { "'$(Get-DisplayPath $path)' -> '$(Get-DisplayPath $target)'" }
        }
    }
}

function mv {
    $o = Read-PosixArgs -Command mv -Arguments $args -Flags 'fvni' -Long @{ '--force' = 'f'; '--verbose' = 'v'; '--no-clobber' = 'n' }
    if ($o.Error) { return Write-Error $o.Error }
    $opt = $o.Opt
    if ($o.Operands.Count -lt 2) { return Write-Error 'missing destination file operand' }
    $dest = Get-FullPath ([string]$o.Operands[-1])
    $destIsDir = Test-Path -LiteralPath $dest -PathType Container
    $sources = $o.Operands | Select-Object -SkipLast 1
    if (@($sources).Count -gt 1 -and -not $destIsDir) { return Write-Error "target '$($o.Operands[-1])' is not a directory" }
    foreach ($source in $sources) {
        $resolved = @(Resolve-PosixPath $source)
        if (-not $resolved) { Write-Error "cannot stat '$source': No such file or directory"; continue }
        foreach ($path in $resolved) {
            $reason = Get-ProtectedReason $path
            if ($reason) { Write-Error "refusing to move '$path': $reason"; continue }
            $target = if ($destIsDir) { Join-Path $dest (Split-Path -Leaf $path) } else { $dest }
            $exists = Test-Path -LiteralPath $target
            if ($exists -and $opt.ContainsKey('n')) { continue }
            if ($exists -and $opt.ContainsKey('i') -and (Read-Host "mv: overwrite '$(Get-DisplayPath $target)'?") -notmatch '^(y|yes)$') { continue }
            Move-Item -LiteralPath $path -Destination $target -Force
            if ($opt.ContainsKey('v')) { "renamed '$(Get-DisplayPath $path)' -> '$(Get-DisplayPath $target)'" }
        }
    }
}

function mkdir {
    $o = Read-PosixArgs -Command mkdir -Arguments $args -Flags 'pv' -ValueFlags 'm' -Long @{ '--parents' = 'p'; '--verbose' = 'v'; '--mode' = '=m' }
    if ($o.Error) { return Write-Error $o.Error }
    if (-not $o.Operands.Count) { return Write-Error 'missing operand' }
    foreach ($dir in $o.Operands) {
        $path = Get-FullPath ([string]$dir)
        if (Test-Path -LiteralPath $path) {
            if (-not $o.Opt.ContainsKey('p')) { Write-Error "cannot create directory '$dir': File exists" }
            continue
        }
        $parent = Split-Path -Parent $path
        if (-not $o.Opt.ContainsKey('p') -and $parent -and -not (Test-Path -LiteralPath $parent -PathType Container)) {
            Write-Error "cannot create directory '$dir': No such file or directory"
            continue
        }
        $null = [System.IO.Directory]::CreateDirectory($path)
        if ($o.Opt.ContainsKey('v')) { "mkdir: created directory '$dir'" }
    }
}

function touch {
    $o = Read-PosixArgs -Command touch -Arguments $args -Flags 'cam' -ValueFlags 'dr' -Long @{ '--no-create' = 'c'; '--date' = '=d'; '--reference' = '=r' }
    if ($o.Error) { return Write-Error $o.Error }
    if (-not $o.Operands.Count) { return Write-Error 'missing file operand' }
    $when = Get-Date
    if ($o.Opt.ContainsKey('d')) {
        try { $when = [datetime]::Parse($o.Opt['d'], [System.Globalization.CultureInfo]::InvariantCulture) }
        catch { return Write-Error "invalid date format '$($o.Opt['d'])'" }
    }
    elseif ($o.Opt.ContainsKey('r')) { $when = (Get-Item -LiteralPath (Get-FullPath $o.Opt['r']) -Force).LastWriteTime }
    foreach ($file in $o.Operands) {
        $path = Get-FullPath ([string]$file)
        if (-not (Test-Path -LiteralPath $path)) {
            if ($o.Opt.ContainsKey('c')) { continue }
            $parent = Split-Path -Parent $path
            if ($parent -and -not (Test-Path -LiteralPath $parent -PathType Container)) { Write-Error "cannot touch '$file': No such file or directory"; continue }
            [System.IO.File]::Create($path).Dispose()
        }
        $item = Get-Item -LiteralPath $path -Force
        if (-not $o.Opt.ContainsKey('a') -or $o.Opt.ContainsKey('m')) { $item.LastWriteTime = $when }
        if (-not $o.Opt.ContainsKey('m') -or $o.Opt.ContainsKey('a')) { $item.LastAccessTime = $when }
    }
}

function ln {
    $o = Read-PosixArgs -Command ln -Arguments $args -Flags 'sfvn' -Long @{ '--symbolic' = 's'; '--force' = 'f'; '--verbose' = 'v' }
    if ($o.Error) { return Write-Error $o.Error }
    if ($o.Operands.Count -lt 1) { return Write-Error 'missing file operand' }
    $target = [string]$o.Operands[0]
    $link = if ($o.Operands.Count -ge 2) { Get-FullPath ([string]$o.Operands[1]) } else { Join-Path $PWD.ProviderPath (Split-Path -Leaf $target) }
    if (Test-Path -LiteralPath $link -PathType Container) { $link = Join-Path $link (Split-Path -Leaf $target) }
    if (Test-Path -LiteralPath $link) {
        if (-not $o.Opt.ContainsKey('f')) { return Write-Error "failed to create link '$link': File exists" }
        Remove-Item -LiteralPath $link -Force
    }
    if ($o.Opt.ContainsKey('s')) { $null = New-Item -ItemType SymbolicLink -Path $link -Target $target }
    else { $null = New-Item -ItemType HardLink -Path $link -Target (Get-FullPath $target) }
    if ($o.Opt.ContainsKey('v')) { "'$(Get-DisplayPath $link)' -> '$target'" }
}

function chmod {
    if (-not $IsWindows) {
        $native = Get-NativeCommand chmod
        if ($native) { & $native @args; return }
        return Write-Error 'chmod: no native chmod found'
    }
    $o = Read-PosixArgs -Command chmod -Arguments $args -Flags 'Rvfc' -Long @{ '--recursive' = 'R'; '--verbose' = 'v' }
    if ($o.Error) { return Write-Error $o.Error }
    if ($o.Operands.Count -lt 2) { return Write-Error 'missing operand' }
    $mode = [string]$o.Operands[0]
    $files = $o.Operands | Select-Object -Skip 1
    if ($mode -match '^[0-7]?([0-7])00$') {
        $perm = switch ($Matches[1]) { '7' { '(F)' } '6' { '(R,W)' } '5' { '(RX)' } '4' { '(R)' } default { $null } }
        if (-not $perm) { return Write-Host "chmod: mode $mode has no useful Windows equivalent; nothing changed." }
        $user = if ($env:USERDOMAIN) { "$env:USERDOMAIN\$env:USERNAME" } else { $env:USERNAME }
        foreach ($f in $files) {
            foreach ($path in @(Resolve-PosixPath $f)) {
                & icacls.exe $path '/inheritance:r' '/grant:r' "${user}:$perm" | Out-Null
                if ($LASTEXITCODE -eq 0) { Write-Host "chmod: '$(Get-DisplayPath $path)' is now accessible only to $user $perm (icacls /inheritance:r /grant:r)" }
                else { Write-Error "icacls failed for '$path' (exit $LASTEXITCODE)" }
            }
        }
        return
    }
    if ($mode -match 'x') {
        return Write-Host 'chmod: Windows has no executable bit; files run based on their extension (.exe, .cmd, .ps1). Nothing changed.'
    }
    Write-Host "chmod: '$mode' has no Windows equivalent; nothing changed. (600/700 restrict a file to your user account.)"
}

function find {
    $paths = [System.Collections.Generic.List[string]]::new()
    $i = 0
    while ($i -lt $args.Count -and -not ([string]$args[$i]).StartsWith('-')) { $paths.Add([string]$args[$i]); $i++ }
    if (-not $paths.Count) { $paths.Add('.') }
    $namePattern = $null; $type = $null; $maxDepth = [int]::MaxValue; $minDepth = 0
    for (; $i -lt $args.Count; $i++) {
        $predicate = [string]$args[$i]
        $value = if ($i + 1 -lt $args.Count) { [string]$args[$i + 1] } else { $null }
        switch -CaseSensitive ($predicate) {
            '-name' { $namePattern = [System.Management.Automation.WildcardPattern]::new($value, [System.Management.Automation.WildcardOptions]::None); $i++ }
            '-iname' { $namePattern = [System.Management.Automation.WildcardPattern]::new($value, [System.Management.Automation.WildcardOptions]::IgnoreCase); $i++ }
            '-type' { $type = $value; $i++ }
            '-maxdepth' { $maxDepth = [int]$value; $i++ }
            '-mindepth' { $minDepth = [int]$value; $i++ }
            '-print' { }
            default { return Write-Error "unknown predicate '$predicate'" }
        }
        if ($predicate -in '-name', '-iname', '-type', '-maxdepth', '-mindepth' -and $null -eq $value) { return Write-Error "missing argument to '$predicate'" }
    }
    $sep = [System.IO.Path]::DirectorySeparatorChar
    $enumeration = [System.IO.EnumerationOptions]@{ IgnoreInaccessible = $true; AttributesToSkip = 0 }
    foreach ($start in $paths) {
        $full = Get-FullPath $start
        if (-not (Test-Path -LiteralPath $full)) { Write-Error "'$start': No such file or directory"; continue }
        $stack = [System.Collections.Generic.Stack[object]]::new()
        $stack.Push(@((Get-Item -LiteralPath $full -Force), 0, $start))
        while ($stack.Count) {
            $entry, $depth, $display = $stack.Pop()
            $isDir = $entry -is [System.IO.DirectoryInfo]
            $isLink = [bool]$entry.LinkTarget
            $typeOk = switch ($type) { 'f' { -not $isDir -and -not $isLink } 'd' { $isDir -and -not $isLink } 'l' { $isLink } $null { $true } default { $false } }
            if ($depth -ge $minDepth -and $typeOk -and (-not $namePattern -or $namePattern.IsMatch($entry.Name))) { $display }
            if ($isDir -and -not $isLink -and $depth -lt $maxDepth) {
                $children = try { @($entry.EnumerateFileSystemInfos('*', $enumeration)) } catch { Write-Error "'$display': $($_.Exception.Message)"; @() }
                $prefix = if ($display.EndsWith('/') -or $display.EndsWith('\')) { $display } else { $display + $sep }
                foreach ($child in ($children | Sort-Object -Property Name -Descending)) { $stack.Push(@($child, ($depth + 1), ($prefix + $child.Name))) }
            }
        }
    }
}

function du {
    $o = Read-PosixArgs -Command du -Arguments $args -Flags 'shcak' -ValueFlags 'd' -Long @{ '--summarize' = 's'; '--human-readable' = 'h'; '--max-depth' = '=d'; '--all' = 'a'; '--total' = 'c' }
    if ($o.Error) { return Write-Error $o.Error }
    $opt = $o.Opt
    $maxDepth = if ($opt.ContainsKey('s')) { 0 } elseif ($opt.ContainsKey('d')) { [int]$opt['d'] } else { [int]::MaxValue }
    $human = $opt.ContainsKey('h')
    $enumeration = [System.IO.EnumerationOptions]@{ IgnoreInaccessible = $true; AttributesToSkip = [System.IO.FileAttributes]::ReparsePoint }
    $rows = [System.Collections.Generic.List[object]]::new()
    $row = { param($bytes, $path) [pscustomobject]@{ Size = $(if ($human) { Format-Size $bytes } else { [long][math]::Ceiling($bytes / 1024) }); Path = $path; Bytes = [long]$bytes } }
    # Post-order walk so each directory's total includes its children, printed after them like du.
    $measure = {
        param([System.IO.DirectoryInfo]$dir, [int]$depth, [string]$display)
        $total = 0L
        foreach ($file in $dir.EnumerateFiles('*', $enumeration)) {
            $total += $file.Length
            if ($opt.ContainsKey('a') -and $depth + 1 -le $maxDepth) { $rows.Add((& $row $file.Length (Join-Path $display $file.Name))) }
        }
        foreach ($sub in $dir.EnumerateDirectories('*', $enumeration)) { $total += & $measure $sub ($depth + 1) (Join-Path $display $sub.Name) }
        if ($depth -le $maxDepth) { $rows.Add((& $row $total $display)) }
        $total
    }
    $grand = 0L
    $targets = if ($o.Operands.Count) { $o.Operands } else { @('.') }
    foreach ($target in $targets) {
        $full = Get-FullPath ([string]$target)
        if (Test-Path -LiteralPath $full -PathType Container) { $grand += & $measure ([System.IO.DirectoryInfo]::new($full)) 0 ([string]$target) }
        elseif (Test-Path -LiteralPath $full) { $len = (Get-Item -LiteralPath $full -Force).Length; $grand += $len; $rows.Add((& $row $len ([string]$target))) }
        else { Write-Error "cannot access '$target': No such file or directory" }
    }
    if ($opt.ContainsKey('c')) { $rows.Add((& $row $grand 'total')) }
    $rows
}

function df {
    $o = Read-PosixArgs -Command df -Arguments $args -Flags 'hkTP' -Long @{ '--human-readable' = 'h' }
    if ($o.Error) { return Write-Error $o.Error }
    $human = $o.Opt.ContainsKey('h')
    $drives = @([System.IO.DriveInfo]::GetDrives() | Where-Object { try { $_.IsReady -and $_.TotalSize -gt 0 } catch { $false } })
    if ($o.Operands.Count) {
        $drives = foreach ($p in $o.Operands) {
            $full = Get-FullPath ([string]$p)
            $drives | Where-Object { $full.StartsWith($_.RootDirectory.FullName, [StringComparison]::OrdinalIgnoreCase) } |
                Sort-Object -Property { $_.RootDirectory.FullName.Length } -Descending | Select-Object -First 1
        }
    }
    foreach ($d in $drives) {
        $used = $d.TotalSize - $d.TotalFreeSpace
        $fmt = { param($b) if ($human) { Format-Size $b } else { [long][math]::Ceiling($b / 1024) } }
        [pscustomobject]@{
            Filesystem = $d.Name
            Type       = $d.DriveFormat
            Size       = & $fmt $d.TotalSize
            Used       = & $fmt $used
            Avail      = & $fmt $d.AvailableFreeSpace
            'Use%'     = '{0}%' -f [math]::Ceiling(100 * $used / [math]::Max([long]1, [long]$d.TotalSize))
            MountedOn  = $d.RootDirectory.FullName
        }
    }
}

# ───────────────────────────── text ─────────────────────────────

function grep {
    begin {
        $o = Read-PosixArgs -Command grep -Arguments $args -Flags 'rRnivlcEFwqHhos' -ValueFlags 'e' -Long @{
            '--recursive' = 'r'; '--line-number' = 'n'; '--ignore-case' = 'i'; '--invert-match' = 'v'; '--files-with-matches' = 'l'
            '--count' = 'c'; '--extended-regexp' = 'E'; '--fixed-strings' = 'F'; '--word-regexp' = 'w'; '--quiet' = 'q'; '--silent' = 'q'
            '--only-matching' = 'o'; '--no-messages' = 's'; '--include' = '=include'; '--exclude' = '=exclude'; '--color' = '=color'; '--colour' = '=color'
        }
        $failed = [bool]$o.Error
        if ($failed) { Write-Error $o.Error; return }
        $opt = $o.Opt
        $operands = [System.Collections.Generic.List[object]]::new($o.Operands)
        if ($opt.ContainsKey('e')) { $pattern = $opt['e'] }
        elseif ($operands.Count) { $pattern = [string]$operands[0]; $operands.RemoveAt(0) }
        else { $failed = $true; Write-Error 'usage: grep [OPTION]... PATTERNS [FILE]...'; return }
        $regexText = if ($opt.ContainsKey('F')) { [regex]::Escape($pattern) } elseif ($opt.ContainsKey('E')) { $pattern } else { ConvertFrom-BasicRegex $pattern }
        if ($opt.ContainsKey('w')) { $regexText = "(?<![\w])(?:$regexText)(?![\w])" }
        $regexOptions = if ($opt.ContainsKey('i')) { [System.Text.RegularExpressions.RegexOptions]::IgnoreCase } else { [System.Text.RegularExpressions.RegexOptions]::None }
        try { $regex = [regex]::new($regexText, $regexOptions) }
        catch { $failed = $true; Write-Error "invalid pattern '$pattern': $($_.Exception.Message)"; return }
        $invert = $opt.ContainsKey('v')
        $quiet = $opt.ContainsKey('q')
        $script:grepMatched = $false
        # Emits output for one line; returns whether it counted as a match.
        $test = {
            param([string]$line, [string]$prefix, [int]$number)
            $m = $regex.Match($line)
            if ($m.Success -eq $invert) { return $false }
            $script:grepMatched = $true
            if ($quiet -or $opt.ContainsKey('l') -or $opt.ContainsKey('c')) { return $true }
            $lead = $prefix + $(if ($opt.ContainsKey('n')) { "${number}:" })
            if ($opt.ContainsKey('o') -and -not $invert) { while ($m.Success) { $lead + $m.Value; $m = $m.NextMatch() } }
            else { $lead + $line }
            $true
        }
        $stream = $MyInvocation.ExpectingInput -and -not $operands.Count
        if ($stream) {
            $formatter = { Out-String -Stream -Width 4096 }.GetSteppablePipeline()
            $formatter.Begin($true)
            $lineNumber = 0
            $count = 0
        }
    }
    process {
        if ($failed -or -not $stream) { return }
        foreach ($line in $formatter.Process($_)) {
            $lineNumber++
            foreach ($out in & $test $line '' $lineNumber) { if ($out -is [bool]) { if ($out) { $count++ } } else { $out } }
        }
    }
    end {
        if ($failed) { return }
        if ($stream) {
            foreach ($line in $formatter.End()) {
                $lineNumber++
                foreach ($out in & $test $line '' $lineNumber) { if ($out -is [bool]) { if ($out) { $count++ } } else { $out } }
            }
            if (-not $quiet) {
                if ($opt.ContainsKey('c')) { $count }
                elseif ($opt.ContainsKey('l') -and $count) { '(standard input)' }
            }
            $global:LASTEXITCODE = if ($script:grepMatched) { 0 } else { 1 }
            return
        }
        $recursive = $opt.ContainsKey('r') -or $opt.ContainsKey('R')
        if (-not $operands.Count) {
            if (-not $recursive) { Write-Error 'no input files (pipe text in or name files)'; $global:LASTEXITCODE = 2; return }
            $operands.Add('.')
        }
        $files = [System.Collections.Generic.List[string]]::new()
        foreach ($operand in $operands) {
            $resolved = @(Resolve-PosixPath $operand)
            if (-not $resolved) { if (-not $opt.ContainsKey('s')) { Write-Error "${operand}: No such file or directory" }; continue }
            foreach ($path in $resolved) {
                if (Test-Path -LiteralPath $path -PathType Container) {
                    if (-not $recursive) { if (-not $opt.ContainsKey('s')) { Write-Error "${operand}: Is a directory" }; continue }
                    foreach ($f in Get-ChildItem -LiteralPath $path -File -Recurse -Force -ErrorAction SilentlyContinue) {
                        if ($opt.ContainsKey('include') -and $f.Name -notlike $opt['include']) { continue }
                        if ($opt.ContainsKey('exclude') -and $f.Name -like $opt['exclude']) { continue }
                        $files.Add($f.FullName)
                    }
                }
                else { $files.Add($path) }
            }
        }
        $showName = ($recursive -or $files.Count -gt 1 -or $opt.ContainsKey('H')) -and -not $opt.ContainsKey('h')
        foreach ($file in $files) {
            $display = Get-DisplayPath $file
            $prefix = if ($showName) { "${display}:" } else { '' }
            $count = 0
            $number = 0
            try {
                foreach ($line in [System.IO.File]::ReadLines($file)) {
                    $number++
                    foreach ($out in & $test $line $prefix $number) { if ($out -is [bool]) { if ($out) { $count++ } } else { $out } }
                    if ($count -and ($opt.ContainsKey('l') -or $quiet)) { break }
                }
            }
            catch { if (-not $opt.ContainsKey('s')) { Write-Error "${display}: $($_.Exception.Message)" }; continue }
            if ($quiet) { if ($count) { break } else { continue } }
            if ($opt.ContainsKey('l')) { if ($count) { $display } }
            elseif ($opt.ContainsKey('c')) { if ($showName) { "${display}:$count" } else { $count } }
        }
        $global:LASTEXITCODE = if ($script:grepMatched) { 0 } else { 1 }
    }
}

function Read-LineCount([object]$Value, [int]$Default = 10) {
    if ($null -eq $Value) { return @{ Count = $Default; FromStart = $false } }
    $s = [string]$Value
    @{ Count = [int]$s.TrimStart('+'); FromStart = $s.StartsWith('+') }
}

function head {
    begin {
        $o = Read-PosixArgs -Command head -Arguments $args -Flags 'qv' -ValueFlags 'nc' -Long @{ '--lines' = '=n'; '--bytes' = '=c' } -NumericCount
        $failed = [bool]$o.Error
        if ($failed) { Write-Error $o.Error; return }
        $n = (Read-LineCount $o.Opt['n']).Count
        $bytes = if ($o.Opt.ContainsKey('c')) { [int]$o.Opt['c'] } else { $null }
        $stream = $MyInvocation.ExpectingInput -and -not $o.Operands.Count
        $seen = 0
        $held = [System.Collections.Generic.Queue[object]]::new()
        $text = [System.Text.StringBuilder]::new()
    }
    process {
        if ($failed -or -not $stream) { return }
        if ($null -ne $bytes) { [void]$text.AppendLine([string]$_); return }
        if ($n -ge 0) { if ($seen -lt $n) { $seen++; $_ } return }
        $held.Enqueue($_)
        if ($held.Count -gt -$n) { $held.Dequeue() }
    }
    end {
        if ($failed) { return }
        if ($stream) {
            if ($null -ne $bytes) { $t = $text.ToString(); $t.Substring(0, [math]::Min($bytes, $t.Length)) }
            return
        }
        $many = $o.Operands.Count -gt 1
        foreach ($file in $o.Operands) {
            $path = @(Resolve-PosixPath $file)[0]
            if (-not $path) { Write-Error "cannot open '$file' for reading: No such file or directory"; continue }
            if ($many) { "==> $file <==" }
            if ($null -ne $bytes) {
                $all = [System.IO.File]::ReadAllBytes($path)
                [System.Text.Encoding]::UTF8.GetString($all, 0, [math]::Min($bytes, $all.Length))
            }
            elseif ($n -ge 0) { Get-Content -LiteralPath $path -TotalCount $n }
            else { Get-Content -LiteralPath $path | Select-Object -SkipLast (-$n) }
        }
    }
}

function tail {
    begin {
        $o = Read-PosixArgs -Command tail -Arguments $args -Flags 'fFqv' -ValueFlags 'nc' -Long @{ '--lines' = '=n'; '--bytes' = '=c'; '--follow' = 'f' } -NumericCount
        $failed = [bool]$o.Error
        if ($failed) { Write-Error $o.Error; return }
        $spec = Read-LineCount $o.Opt['n']
        $bytes = if ($o.Opt.ContainsKey('c')) { [int]$o.Opt['c'] } else { $null }
        $stream = $MyInvocation.ExpectingInput -and -not $o.Operands.Count
        if ($stream) {
            $select = if ($spec.FromStart) { [scriptblock]::Create("Select-Object -Skip $([math]::Max(0, $spec.Count - 1))") } else { [scriptblock]::Create("Select-Object -Last $($spec.Count)") }
            $pipeline = $select.GetSteppablePipeline()
            $pipeline.Begin($true)
        }
    }
    process {
        if (-not $failed -and $stream) { $pipeline.Process($_) }
    }
    end {
        if ($failed) { return }
        if ($stream) { $pipeline.End(); return }
        $many = $o.Operands.Count -gt 1
        $follow = $o.Opt.ContainsKey('f') -or $o.Opt.ContainsKey('F')
        foreach ($file in $o.Operands) {
            $path = @(Resolve-PosixPath $file)[0]
            if (-not $path) { Write-Error "cannot open '$file' for reading: No such file or directory"; continue }
            if ($many) { "==> $file <==" }
            if ($null -ne $bytes) {
                $all = [System.IO.File]::ReadAllBytes($path)
                $start = [math]::Max(0, $all.Length - $bytes)
                [System.Text.Encoding]::UTF8.GetString($all, $start, $all.Length - $start)
            }
            elseif ($follow) { Get-Content -LiteralPath $path -Tail $spec.Count -Wait }
            elseif ($spec.FromStart) { Get-Content -LiteralPath $path | Select-Object -Skip ([math]::Max(0, $spec.Count - 1)) }
            else { Get-Content -LiteralPath $path -Tail $spec.Count }
        }
    }
}

function wc {
    $o = Read-PosixArgs -Command wc -Arguments $args -Flags 'lwcm' -Long @{ '--lines' = 'l'; '--words' = 'w'; '--bytes' = 'c'; '--chars' = 'm' }
    if ($o.Error) { return Write-Error $o.Error }
    $selected = @(foreach ($k in 'l', 'w', 'c', 'm') { if ($o.Opt.ContainsKey($k)) { $k } })
    if (-not $selected) { $selected = 'l', 'w', 'c' }
    $words = [regex]::new('\S+')
    $measure = {
        param($lines)
        $l = 0L; $w = 0L; $c = 0L; $m = 0L
        foreach ($line in $lines) {
            if ($line -is [string]) {
                foreach ($part in $line.Split("`n")) {
                    $l++
                    $w += $words.Matches($part).Count
                    $c += [System.Text.Encoding]::UTF8.GetByteCount($part) + 1
                    $m += $part.Length + 1
                }
            }
            else {
                $text = [string]$line
                $l++
                $w += $words.Matches($text).Count
                $c += [System.Text.Encoding]::UTF8.GetByteCount($text) + 1
                $m += $text.Length + 1
            }
        }
        @{ l = $l; w = $w; c = $c; m = $m }
    }
    $results = [System.Collections.Generic.List[object]]::new()
    if (-not $o.Operands.Count) { $results.Add(@{ Counts = (& $measure @($input)); Name = $null }) }
    foreach ($file in $o.Operands) {
        $path = @(Resolve-PosixPath $file)[0]
        if (-not $path) { Write-Error "${file}: No such file or directory"; continue }
        if (Test-Path -LiteralPath $path -PathType Container) { Write-Error "${file}: Is a directory"; continue }
        $counts = & $measure @(Get-Content -LiteralPath $path)
        $counts.c = (Get-Item -LiteralPath $path).Length
        $results.Add(@{ Counts = $counts; Name = [string]$file })
    }
    if ($results.Count -gt 1) {
        $total = @{ l = 0L; w = 0L; c = 0L; m = 0L }
        foreach ($r in $results) { foreach ($k in 'l', 'w', 'c', 'm') { $total[$k] += $r.Counts[$k] } }
        $results.Add(@{ Counts = $total; Name = 'total' })
    }
    $names = @{ l = 'Lines'; w = 'Words'; c = 'Bytes'; m = 'Chars' }
    foreach ($r in $results) {
        if ($selected.Count -eq 1 -and $results.Count -eq 1) { $r.Counts[$selected[0]]; continue }
        $obj = [ordered]@{}
        foreach ($k in $selected) { $obj[$names[$k]] = $r.Counts[$k] }
        if ($r.Name) { $obj['File'] = $r.Name }
        [pscustomobject]$obj
    }
}

# ───────────────────────────── processes & system ─────────────────────────────

function ps {
    $text = ($args | ForEach-Object { [string]$_ }) -join ' '
    $detailed = $text -match '(^|\s)-?(aux|ef|e|A|ax|u|f|ely)\b' -or $text -match '-[a-zA-Z]*[efly]'
    $ids = if ($text -match '-p\s*([\d,\s]+)') { $Matches[1] -split '[,\s]+' | Where-Object { $_ } | ForEach-Object { [int]$_ } }
    $procs = if ($ids) { Get-Process -Id $ids -ErrorAction SilentlyContinue } else { Get-Process }
    $procs = $procs | Sort-Object -Property Id
    if (-not $detailed) { return $procs }
    $cim = @{}
    if ($IsWindows) {
        foreach ($p in Get-CimInstance -ClassName Win32_Process -ErrorAction Ignore) { $cim[[int]$p.ProcessId] = $p }
    }
    foreach ($p in $procs) {
        $info = $cim[[int]$p.Id]
        $ppid = if ($info) { $info.ParentProcessId } else { try { $p.Parent.Id } catch { $null } }
        $command = if ($info) { $info.CommandLine } else { try { $p.CommandLine } catch { $null } }
        $start = try { $p.StartTime } catch { $null }
        [pscustomobject]@{
            PID       = $p.Id
            PPID      = $ppid
            'CPU(s)'  = [math]::Round([double]$p.CPU, 1)
            'Mem(MB)' = [math]::Round($p.WorkingSet64 / 1MB, 1)
            Started   = $start
            Command   = if ($command) { $command } elseif ($p.Path) { $p.Path } else { $p.ProcessName }
        }
    }
}

function kill {
    if (-not $args.Count) { return Write-Error 'usage: kill [-s SIGNAL | -SIGNAL] PID|%JOB ...' }
    $signal = 'TERM'
    $targets = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $args.Count; $i++) {
        $a = [string]$args[$i]
        if ($a -eq '-l' -or $a -eq '-L') { return 'HUP INT QUIT KILL TERM STOP CONT' }
        if ($a -eq '-s' -or $a -eq '-n') { $i++; $signal = [string]$args[$i]; continue }
        if ($a -match '^-(SIG)?([A-Za-z]+|\d+)$') { $signal = $Matches[2]; continue }
        $targets.Add($a)
    }
    $signal = $signal.ToUpperInvariant()
    if (-not $IsWindows -and $signal -notin 'KILL', '9') {
        $native = Get-NativeCommand kill
        if ($native) { & $native "-$signal" @($targets | Where-Object { -not $_.StartsWith('%') }); }
        foreach ($t in $targets | Where-Object { $_.StartsWith('%') }) { Stop-Job -Id ([int]$t.Substring(1)) }
        return
    }
    foreach ($t in $targets) {
        if ($t.StartsWith('%')) { Stop-Job -Id ([int]$t.Substring(1)); continue }
        if ($t -notmatch '^\d+$') { Write-Error "${t}: arguments must be process or job IDs"; continue }
        Stop-Process -Id ([int]$t) -Force:($signal -in 'KILL', '9') -ErrorAction Continue
    }
}

function which {
    $o = Read-PosixArgs -Command which -Arguments $args -Flags 'as'
    if ($o.Error) { return Write-Error $o.Error }
    $missing = $false
    foreach ($name in $o.Operands) {
        $found = @(Get-Command -Name $name -All:($o.Opt.ContainsKey('a')) -ErrorAction Ignore)
        if (-not $o.Opt.ContainsKey('a')) { $found = @($found | Select-Object -First 1) }
        if (-not $found) { $missing = $true; Write-Error "no $name in PATH"; continue }
        foreach ($c in $found) {
            switch ($c.CommandType) {
                'Application' { $c.Source }
                'ExternalScript' { $c.Source }
                'Alias' { "${name}: aliased to $($c.Definition)" }
                'Cmdlet' { "${name}: cmdlet ($($c.ModuleName))" }
                default { "${name}: $($c.CommandType.ToString().ToLowerInvariant())$(if ($c.ModuleName) { " ($($c.ModuleName))" })" }
            }
        }
    }
    $global:LASTEXITCODE = if ($missing) { 1 } else { 0 }
}

function type {
    $o = Read-PosixArgs -Command type -Arguments $args -Flags 'atpP'
    if ($o.Error) { return Write-Error $o.Error }
    foreach ($name in $o.Operands) {
        $found = @(Get-Command -Name $name -All:($o.Opt.ContainsKey('a')) -ErrorAction Ignore)
        if (-not $found) {
            # cmd.exe habit: `type file.txt` prints the file.
            $path = @(Resolve-PosixPath $name)[0]
            if ($path -and (Test-Path -LiteralPath $path -PathType Leaf)) { Get-Content -LiteralPath $path; continue }
            Write-Error "${name}: not found"
            continue
        }
        if (-not $o.Opt.ContainsKey('a')) { $found = @($found[0]) }
        foreach ($c in $found) {
            $kind = switch ($c.CommandType) { 'Application' { 'file' } 'ExternalScript' { 'file' } 'Alias' { 'alias' } 'Cmdlet' { 'cmdlet' } default { 'function' } }
            if ($o.Opt.ContainsKey('t')) { $kind; continue }
            if ($o.Opt.ContainsKey('p') -or $o.Opt.ContainsKey('P')) { if ($kind -eq 'file') { $c.Source }; continue }
            switch ($kind) {
                'file' { "$name is $($c.Source)" }
                'alias' { "$name is aliased to ``$($c.Definition)'" }
                'cmdlet' { "$name is a cmdlet ($($c.ModuleName))" }
                default { "$name is a function$(if ($c.ModuleName) { " ($($c.ModuleName))" })" }
            }
        }
    }
}

function uname {
    $o = Read-PosixArgs -Command uname -Arguments $args -Flags 'asnrvmpio' -Long @{ '--all' = 'a' }
    if ($o.Error) { return Write-Error $o.Error }
    $runtime = [System.Runtime.InteropServices.RuntimeInformation]
    $kernel = if ($IsWindows) { 'Windows_NT' } elseif ($IsMacOS) { 'Darwin' } else { 'Linux' }
    $machine = switch ($runtime::OSArchitecture.ToString()) { 'X64' { 'x86_64' } 'Arm64' { 'aarch64' } 'X86' { 'i686' } default { $_.ToLowerInvariant() } }
    $fields = [ordered]@{
        s = $kernel
        n = [Environment]::MachineName
        r = [Environment]::OSVersion.Version.ToString()
        v = $runtime::OSDescription
        m = $machine
        o = if ($IsWindows) { 'Windows' } elseif ($IsMacOS) { 'Darwin' } else { 'GNU/Linux' }
    }
    $wanted = if ($o.Opt.ContainsKey('a')) { $fields.Keys } else { @($fields.Keys | Where-Object { $o.Opt.ContainsKey($_) }) }
    if (-not $wanted) { $wanted = @('s') }
    ($wanted | ForEach-Object { $fields[$_] }) -join ' '
}

function man {
    $name = @($args | Where-Object { [string]$_ -notmatch '^\d+$' -and -not ([string]$_).StartsWith('-') })[-1]
    if (-not $name) { return Write-Error 'What manual page do you want?' }
    $command = Get-Command -Name $name -ErrorAction Ignore | Select-Object -First 1
    if (-not $command) { return Write-Error "No manual entry for $name" }
    if ($command.CommandType -eq 'Application') {
        $native = if (-not $IsWindows) { Get-NativeCommand man }
        if ($native) { & $native @args } else { & $command.Source --help }
        return
    }
    Get-Help -Name $name -Full
}

function clear { Clear-Host }

function history {
    if ($args -contains '-c') { Clear-History; return }
    $count = @($args | Where-Object { [string]$_ -match '^\d+$' })[0]
    if ($count) { Get-History -Count ([int]$count) } else { Get-History }
}

function open {
    if (-not $args.Count) { return Write-Error 'usage: open <file|url>...' }
    foreach ($target in $args) {
        $t = [string]$target
        if ($t -match '^[A-Za-z][A-Za-z0-9+.-]*://') { Start-Process -FilePath $t; continue }
        $paths = @(Resolve-PosixPath $t)
        if (-not $paths) { Write-Error "The file $t does not exist."; continue }
        foreach ($p in $paths) { Invoke-Item -LiteralPath $p }
    }
}

function xdg-open { open @args }

# ───────────────────────────── environment & aliases ─────────────────────────────

function env {
    $assignments = [ordered]@{}
    $unset = [System.Collections.Generic.List[string]]::new()
    $i = 0
    for (; $i -lt $args.Count; $i++) {
        $a = [string]$args[$i]
        if ($a -eq '-u') { $i++; $unset.Add([string]$args[$i]); continue }
        if ($a -eq '-i' -or $a -eq '-') { return Write-Error 'env -i (empty environment) is not supported' }
        if ($a -match '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') { $assignments[$Matches[1]] = $Matches[2]; continue }
        break
    }
    $command = @($args | Select-Object -Skip $i)
    if (-not $command.Count) {
        $vars = @{}
        foreach ($e in Get-ChildItem -Path Env:) { $vars[$e.Name] = $e.Value }
        foreach ($k in $unset) { $vars.Remove($k) }
        foreach ($k in $assignments.Keys) { $vars[$k] = $assignments[$k] }
        foreach ($k in $vars.Keys | Sort-Object) { "$k=$($vars[$k])" }
        return
    }
    $saved = @{}
    foreach ($k in @($assignments.Keys) + $unset) { $saved[$k] = [Environment]::GetEnvironmentVariable($k) }
    try {
        foreach ($k in $unset) { [Environment]::SetEnvironmentVariable($k, $null) }
        foreach ($k in $assignments.Keys) { [Environment]::SetEnvironmentVariable($k, $assignments[$k]) }
        $rest = @($command | Select-Object -Skip 1)
        & $command[0] @rest
    }
    finally {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }
}

function export {
    if (-not $args.Count -or $args[0] -eq '-p') {
        foreach ($e in Get-ChildItem -Path Env: | Sort-Object -Property Name) { "declare -x $($e.Name)=`"$($e.Value)`"" }
        return
    }
    foreach ($a in $args) {
        $s = [string]$a
        if ($s -match '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2]) }
        elseif ($s -match '^[A-Za-z_][A-Za-z0-9_]*$') {
            $value = Get-Variable -Name $s -Scope Global -ValueOnly -ErrorAction Ignore
            if ($null -ne $value) { [Environment]::SetEnvironmentVariable($s, [string]$value) }
        }
        else { Write-Error "'$s': not a valid identifier" }
    }
}

function unset {
    $functions = $false
    foreach ($a in $args) {
        $s = [string]$a
        if ($s -eq '-f') { $functions = $true; continue }
        if ($s -eq '-v') { $functions = $false; continue }
        if ($functions) { Remove-Item -LiteralPath "Function:\$s" -ErrorAction Ignore; continue }
        [Environment]::SetEnvironmentVariable($s, $null)
        Remove-Variable -Name $s -Scope Global -ErrorAction Ignore
    }
}

function source {
    if (-not $args.Count) { return Write-Error 'usage: source FILE [ARGS...]' }
    $path = @(Resolve-PosixPath $args[0])[0]
    if (-not $path) { return Write-Error "$($args[0]): No such file or directory" }
    # A function can't dot-source into its caller; at the prompt Pickle rewrites `source x` to `. x` instead.
    # Environment variables set by the script still apply (they are process-wide).
    Write-Warning "source: functions/variables defined by '$($args[0])' stay inside this call; use '. $($args[0])' in scripts."
    $rest = @($args | Select-Object -Skip 1)
    . $path @rest
}

function alias {
    if (-not $args.Count) {
        foreach ($a in Get-PickleAlias) { "alias $($a.Name)='$($a.Body.Replace("'", "'\''"))'" }
        return
    }
    foreach ($a in $args) {
        $s = [string]$a
        if ($s -eq '-p') { alias; continue }
        if ($s -match '^([^=\s]+)=(.*)$') {
            Set-PickleAlias -Name $Matches[1] -Body $Matches[2]
        }
        else {
            $existing = Get-PickleAlias -Name $s
            if ($existing) { "alias $($existing.Name)='$($existing.Body)'" } else { Write-Error "${s}: not found" }
        }
    }
}

function unalias {
    if ($args -contains '-a') { Get-PickleAlias | ForEach-Object { Remove-PickleAlias -Name $_.Name }; return }
    foreach ($a in $args) { Remove-PickleAlias -Name ([string]$a) }
}

# ───────────────────────────── activation ─────────────────────────────

$enabled = [bool]$Options.Enabled
$skip = @(@($Options.Disabled) + @($Options.Skip) | Where-Object { $_ })
$export = @(if ($enabled) { $script:AllShims | Where-Object { $_ -notin $skip } })
if ($enabled -and $Options.PreferNativeBinaries) {
    # System32 tools (find.exe, sort.exe…) are not the POSIX tools users mean; only defer to real ports (Git, MSYS…).
    $systemRoot = $env:SystemRoot
    $export = @($export | Where-Object {
            $native = Get-Command -Name $_ -CommandType Application -ErrorAction Ignore |
                Where-Object { -not $systemRoot -or -not $_.Source.StartsWith($systemRoot, [StringComparison]::OrdinalIgnoreCase) } |
                Select-Object -First 1
            -not $native
        })
}

# Built-in aliases (ls → Get-ChildItem, cat → Get-Content…) resolve before functions, so remove them while loaded and
# put them (and replaced built-in functions like mkdir) back when the module is removed.
$script:SavedAliases = [System.Collections.Generic.List[object]]::new()
$script:SavedFunctions = @{}
foreach ($name in $export) {
    $existing = Get-Alias -Name $name -Scope Global -ErrorAction Ignore
    if ($existing) {
        $script:SavedAliases.Add([pscustomobject]@{ Name = $name; Definition = $existing.Definition; Options = $existing.Options })
        Remove-Alias -Name $name -Force -ErrorAction Ignore
        Remove-Alias -Name $name -Scope Global -Force -ErrorAction Ignore
    }
    $function = Get-Item -LiteralPath "Function:\$name" -ErrorAction Ignore
    if ($function -and -not $function.Module) { $script:SavedFunctions[$name] = $function.ScriptBlock }
}

# Pickle's unload script reads $script:SavedAliases/$script:SavedFunctions and restores them after Remove-Module.

Export-ModuleMember -Function $export
