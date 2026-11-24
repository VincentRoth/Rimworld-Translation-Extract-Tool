<#
.SYNOPSIS
    Applies French punctuation rules to the translated texts in out\ (see Dibolic_FrenchModsPack\Conventions.md).

.DESCRIPTION
    For every Languages\French XML file under the given folder, only the text of elements is changed
    (never comments, so the "EN:" lines stay as they are, never "TODO" entries):
      - straight quotes "..." become French quotes with non-breaking spaces;
      - a non-breaking space (U+00A0) is put inside French quotes, before : ; ! ? and before % after a number (a plain space
        there is replaced, a missing one is added);
      - curly apostrophes become straight ones, the ellipsis character becomes "...";
      - JobDef reportString starts with a lowercase letter (the game inserts it mid-sentence), unless the
        first word is an acronym or a symbol;
      - in rule packs (<li>keyword->text</li>) only the part after "->" is changed.
    Left untouched: {symbols}, [symbols], times and ratios (10:30), URLs (://), "?!" runs keep a single space.
    Files are rewritten only when something changed, in UTF-8 with BOM.

.PARAMETER Path
    Folder to process (default: out\ next to this script). A mod folder can be given, e.g. out\ferny.progressioncore.

.PARAMETER WhatIf
    Counts and shows sample changes without writing.

.EXAMPLE
    .\Fix-Typography.ps1 -WhatIf
    .\Fix-Typography.ps1 -Path .\out\aoba.usac.core
#>
[CmdletBinding()]
param(
    [string]$Path,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
# $PSScriptRoot is empty in a param default under "powershell -File" (PS 5.1): resolved here instead
if (-not $Path) { $Path = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'out' }
$nb = [string][char]0x00A0
$lq = [string][char]0x00AB   # opening French quote
$rq = [string][char]0x00BB   # closing French quote

# {symbols}, [symbols] and URLs are replaced by numbered markers (private-use char) while the text is changed.
$mark = [string][char]0xE000
function Protect([string]$s, [System.Collections.Generic.List[string]]$tokens) {
    return [regex]::Replace($s, '\{[^{}]*\}|\[[^\[\]]*\]|https?://\S+', {
        param($m) $tokens.Add($m.Value); return $mark + ($tokens.Count - 1) + $mark
    })
}
function Restore([string]$s, [System.Collections.Generic.List[string]]$tokens) {
    return [regex]::Replace($s, [regex]::Escape($mark) + '(\d+)' + [regex]::Escape($mark), {
        param($m) return $tokens[[int]$m.Groups[1].Value]
    })
}

function Fix-Text([string]$text) {
    $tokens = New-Object System.Collections.Generic.List[string]
    $s = Protect $text $tokens
    # 0. Straight apostrophe and three dots (Conventions.md section 7).
    $s = $s.Replace([string][char]0x2019, "'").Replace([string][char]0x2026, '...')
    # 1. Straight quotes -> French quotes, only when they come in pairs.
    if ((($s.ToCharArray() | Where-Object { $_ -eq '"' }).Count % 2) -eq 0) {
        $s = [regex]::Replace($s, '"\s*([^"]*?)\s*"', { param($m) return $lq + $nb + $m.Groups[1].Value + $nb + $rq })
    }
    # 2. Spaces inside French quotes.
    $s = [regex]::Replace($s, ($lq + '[ ' + $nb + [char]0x202F + ']*'), ($lq + $nb))
    $s = [regex]::Replace($s, ('[ ' + $nb + [char]0x202F + ']*' + $rq), ($nb + $rq))
    # 3. Space before : ; ! ? (not for 10:30, ://, nor at the very start).
    $s = [regex]::Replace($s, '(?<=[!?])[   ]+(?=[!?])', '')                 # "? !" -> "?!"
    $s = [regex]::Replace($s, '(?<=[^\s!?;:])[   ]*(?=[;!?])', $nb)          # word; word! word? (first of a run)
    $s = [regex]::Replace($s, '(?<=[^\s\d!?;:])[   ]*:(?!//)', ($nb + ':'))  # word: -> word :  (10:30 untouched)
    $s = [regex]::Replace($s, '(?<=\d)[  ]+:', ($nb + ':'))                      # "3 :" keeps its space, as nbsp
    # 4. Non-breaking space before % after a number or a symbol ("25%", "{0}%" -> "25 %", "{0} %"); not "%%", "%s".
    $s = [regex]::Replace($s, ('(?<=[\d' + $mark + '])[ ' + [char]0x202F + ']*%(?![\w%])'), ($nb + '%'))
    return (Restore $s $tokens)
}

function Fix-Value([string]$v) {
    if ($v -eq 'TODO' -or -not $v) { return $v }
    $arrow = $v.IndexOf('->')
    if ($arrow -ge 0) { return $v.Substring(0, $arrow + 2) + (Fix-Text $v.Substring($arrow + 2)) }
    return (Fix-Text $v)
}

$files = Get-ChildItem -Path $Path -Recurse -Filter *.xml | Where-Object { $_.FullName -match '\\Languages\\French\\' }
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
$changedFiles = 0; $changedTexts = 0; $samples = @()
foreach ($f in $files) {
    $doc = New-Object System.Xml.XmlDocument
    $doc.PreserveWhitespace = $true
    try { $doc.Load($f.FullName) } catch { Write-Warning "Not loaded: $($f.FullName) ($($_.Exception.Message))"; continue }
    $n = 0
    foreach ($t in $doc.SelectNodes('//text()')) {
        if ($t.ParentNode.NodeType -ne 'Element' -or -not $t.Value.Trim()) { continue }
        if ($t.ParentNode.Name -eq 'path' -and $t.ParentNode.ParentNode.Name -eq 'rep') { continue }   # key, not text
        $new = Fix-Value $t.Value
        if ($f.FullName -match '\\JobDef\\' -and $t.ParentNode.Name -like '*.reportString' -and $new.Length -ge 2 -and
            [char]::IsUpper($new[0]) -and -not [char]::IsUpper($new[1]) -and $new -ne 'TODO') {
            $new = [char]::ToLowerInvariant($new[0]) + $new.Substring(1)
        }
        if ($new -cne $t.Value) {   # -cne: a case change counts
            if ($samples.Count -lt 25) { $samples += "  $($t.Value)`n  => $new" }
            $t.Value = $new; $n++
        }
    }
    if ($n -gt 0) {
        $changedFiles++; $changedTexts += $n
        if (-not $WhatIf) {
            $settings = New-Object System.Xml.XmlWriterSettings
            $settings.Encoding = $utf8Bom
            $settings.NewLineHandling = 'None'
            $w = [System.Xml.XmlWriter]::Create($f.FullName, $settings)
            try { $doc.Save($w) } finally { $w.Close() }
        }
    }
}
$samples | ForEach-Object { Write-Host $_ }
$verb = if ($WhatIf) { 'would change' } else { 'changed' }
Write-Host "$changedTexts texts $verb in $changedFiles files (of $($files.Count))." -ForegroundColor Cyan
