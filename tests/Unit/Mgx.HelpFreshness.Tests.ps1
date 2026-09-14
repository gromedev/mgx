<#
    The compiled MAML must say what its markdown source says.

    module/en-US/Mgx.Cmdlets.dll-Help.xml is generated from module/help/*.md, and for two
    releases nothing regenerated it. Get-Help went on describing PSObjects and an ODataType
    property that 2.0.0 replaced with hashtables, denied that Sync-MgxDelta has -CheckpointPath
    while that was a headline 2.1.0 feature, and gave -BatchChunkConcurrency a range the cmdlet
    rejects. The existing surface tests compare example COUNT and parameter PRESENCE, so none of
    it registered.

    This compares the text. Every piece of prose is compared whole - the synopsis, the
    description, each example's prose and every parameter description - normalized for what the
    generator does to it: it reflows prose and it renders a markdown link as its text followed by
    its target. Runs of whitespace then collapse to a single space on both sides, and MAML's
    separate <maml:para> elements join with a space rather than nothing, so a real edit inside
    the prose still tells the two sides apart - only where a paragraph happens to break is out of
    the comparison, because a break reads the same as an ordinary space once joined. The one
    further allowance is the space after a rendered link's closing ")": platyPS drops it there
    when it folds the paragraph that follows a link into the same one. Normalize marks that one
    ")" - on the markdown side where it renders the link, and on the MAML side where the link
    already reads as its rendered "text (target)" - with a sentinel no help text contains, so
    the fold finds exactly that position and nowhere else; an ordinary parenthetical's ")" is
    never marked and keeps its space. Measured with New-ExternalHelp (0.14.2): a plain
    list item compiles to its own <maml:para>, a wrapped continuation folds into the para of the
    item it continues, and whether a break lands before the next bullet depends on whether the
    line before it was indented - none of which is where a documentation edit lives, so that
    shape is pinned separately, para for para, rather than compared here.
#>

BeforeAll {
    $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:Maml = Join-Path $repo 'module/en-US/Mgx.Cmdlets.dll-Help.xml'
    $script:HelpDir = Join-Path $repo 'module/help'

    # The mark Normalize leaves on a rendered link's closing ")" - a private-use code point no
    # help text contains - so FoldLinkSpace can find that one position later and nothing else.
    $script:LinkMark = [char]0xE000
    $script:LinkMarkPattern = [regex]::Escape($script:LinkMark)

    function Normalize([string] $t) {
        if (-not $t) { return '' }
        # markdown link -> text followed by its target, the way the generator renders one; code
        # ticks and whitespace collapse. The target stays in the comparison, so a link retargeted
        # in the markdown is a difference and not a wash. The link's closing ")" is marked so
        # FoldLinkSpace can find exactly that position later.
        $t = [regex]::Replace($t, '\[([^\]]+)\]\(([^)]+)\)', ('$1 ($2)' + $script:LinkMark))
        # The MAML side never carries that markdown syntax - by the time its text reaches here
        # platyPS has already rendered the link to "text (target)" - so the same mark goes on a
        # parenthetical whose target is itself a URL, which is what every markdown link in
        # module/help points at. The lookahead skips a ")" the line above already marked, so a
        # link is never marked twice.
        $t = $t -replace "(\(https?://[^)]*\))(?!$script:LinkMarkPattern)", ('$1' + $script:LinkMark)
        $t = $t -replace '`', ''
        ($t -replace '\s+', ' ').Trim()
    }

    # Drops the whitespace after the ")" Normalize marked as a rendered link's, and the mark
    # itself - the fold platyPS performs when it merges the paragraph after a link into the one
    # the link is in. A ")" with no mark - every ordinary parenthetical - is untouched, so a real
    # edit that drops its own following space still reads as a difference.
    function FoldLinkSpace([string] $t) {
        $t -replace "$script:LinkMarkPattern\s*", ''
    }

    # An excerpt of $t from $from, marked where it is cut.
    function Excerpt([string] $t, [int] $from, [int] $length = 80) {
        if ($from -ge $t.Length) { return '<nothing further>' }
        $lead  = if ($from -gt 0) { '...' } else { '' }
        $take  = [Math]::Min($length, $t.Length - $from)
        $trail = if ($t.Length - $from -gt $length) { '...' } else { '' }
        "$lead$($t.Substring($from, $take))$trail"
    }

    # Both sides from where they part company: an appended sentence differs only at the end, and
    # a message quoting the two openings would print the same words twice.
    function Divergence([string] $want, [string] $got) {
        $shared = 0
        $common = [Math]::Min($want.Length, $got.Length)
        while ($shared -lt $common -and $want[$shared] -eq $got[$shared]) { $shared++ }
        $from = [Math]::Max(0, $shared - 20)
        "markdown says '$(Excerpt $want $from)' MAML says '$(Excerpt $got $from)'"
    }

    # A section's prose with whitespace collapsed to a single space, so a paragraph break folds
    # the same way an ordinary space between words does and where the generator happens to break
    # or fold one never enters the comparison - only the sequence of prose, marker and
    # punctuation characters does. Where that break lands does not track markdown's blank lines:
    # a plain list item gets its own <maml:para>, a wrapped continuation folds into the para of
    # the item it continues, and a lead-in line folds into the first item that follows it - that
    # shape is pinned separately, below, rather than depended on here. The one further fold is the
    # space after a rendered link's ")": platyPS drops it there when the paragraph that follows a
    # link joins the one the link is in. Normalize already marked that ")", so FoldLinkSpace finds
    # only that position - an ordinary parenthetical was never marked and keeps its space.
    function Paragraphs([string[]] $lines) {
        FoldLinkSpace (Normalize ($lines -join ' '))
    }

    # The paragraphs a MAML node carries at $xpath, joined with a space and folded the way
    # Paragraphs folds markdown's. local-name() throughout: the document declares three
    # namespaces and the prefixes are not worth registering to read a <para>.
    function MamlParas($node, [string] $xpath) {
        FoldLinkSpace ((@($node.SelectNodes($xpath) | ForEach-Object { Normalize $_.InnerText } |
            Where-Object { $_ })) -join ' ')
    }

    # The same paragraphs MamlParas joins, kept apart instead - for pinning the LIST a MAML node
    # carries, para for para, rather than the one string MamlParas flattens them to.
    function MamlParaList($node, [string] $xpath) {
        @($node.SelectNodes($xpath) | ForEach-Object { Normalize $_.InnerText } | Where-Object { $_ } |
            ForEach-Object { FoldLinkSpace $_ })
    }

    # The markdown's own list of paragraphs, grouped the way New-ExternalHelp (0.14.2) groups
    # them into <maml:para> elements - which does not track markdown's blank lines. A bullet line
    # ("-" or "*" then a space, however far indented - a nested item under another still opens
    # one) opens a new paragraph, unless the line before it was an indented continuation, in which
    # case the break that would otherwise land before a bullet does not. An indented line with no
    # bullet marker is always a continuation and always joins the paragraph before it. A lead-in
    # line - unindented prose before the first bullet has appeared - joins the paragraph of the
    # first bullet after it. Each group is folded the way Paragraphs folds one, so the result
    # lines up against MamlParaList's, position for position.
    function MarkdownParaList([string[]] $lines) {
        $groups = [System.Collections.Generic.List[System.Collections.Generic.List[string]]]::new()
        $sawBullet = $false
        $prevContinuation = $false
        $prevLeadIn = $false
        foreach ($line in $lines) {
            $bullet = $line -match '^\s*[-*]\s'
            $continuation = -not $bullet -and $line -match '^\s+\S'
            $suppress = $continuation -or $prevContinuation -or ($prevLeadIn -and -not $sawBullet)
            if ($groups.Count -eq 0 -or -not $suppress) {
                $groups.Add([System.Collections.Generic.List[string]]::new())
            }
            $groups[$groups.Count - 1].Add($line)
            if ($bullet) { $sawBullet = $true }
            $prevContinuation = $continuation
            $prevLeadIn = -not $bullet -and -not $continuation
        }
        @($groups | ForEach-Object { Paragraphs $_ })
    }

    # A <description> node carrying one <para> per string in $paras, so a pinned MAML shape can
    # be read back through MamlParas itself rather than a second copy of what it does.
    function AsMamlParas([string[]] $paras) {
        $xml = [xml]'<description></description>'
        foreach ($p in $paras) {
            $el = $xml.CreateElement('para')
            $el.InnerText = $p
            [void]$xml.DocumentElement.AppendChild($el)
        }
        $xml.DocumentElement
    }

    # The lines of one '## <heading>' section, up to the next heading of any level. A line
    # inside a ``` fence never ends a section: a fenced example's own '# comment' opens with
    # the same character a heading does, so fence state is tracked and heading detection is
    # suspended for as long as one is open - the fenced lines themselves are code, not prose,
    # so they are dropped rather than carried into the section like MarkdownExamples drops them.
    function SectionLines([string[]] $lines, [string] $heading) {
        $out = [System.Collections.Generic.List[string]]::new()
        $in = $false
        $fenced = $false
        foreach ($line in $lines) {
            if ($line -match "^## $heading\s*$") { $in = $true; continue }
            if (-not $in) { continue }
            if ($line -match '^```') { $fenced = -not $fenced; continue }
            if ($fenced) { continue }
            if ($line -match '^#{1,3} ') { break }
            $out.Add($line)
        }
        $out
    }

    # Every '### Example' heading with its title and the prose around its code fences. Prose
    # stops at the second fence, the way the generator's <maml:remarks> does: a fence count
    # resets with each example, and once it reaches two, nothing further joins the prose - only
    # the code between the fences was ever remarks, not whatever trails the second one.
    function MarkdownExamples([string[]] $lines) {
        $examples = [System.Collections.Generic.List[object]]::new()
        $title  = $null
        $prose  = [System.Collections.Generic.List[string]]::new()
        $fenced = $false
        $fences = 0
        foreach ($line in $lines) {
            # The fence is read before anything else, and nothing inside one is read as markdown:
            # a PowerShell comment opens with the '#' a heading opens with, and most of these
            # examples carry one.
            if ($line -match '^```') {
                $fenced = -not $fenced
                if ($fenced) { $fences++ }
                continue
            }
            if ($fenced) { continue }
            if ($line -match '^### Example') {
                if ($title) { $examples.Add([pscustomobject]@{ Title = $title; Prose = Paragraphs $prose }) }
                $title = Normalize ($line -replace '^### ', '')
                $prose.Clear()
                $fences = 0
                continue
            }
            if (-not $title) { continue }
            if ($line -match '^#{1,3} ') {
                $examples.Add([pscustomobject]@{ Title = $title; Prose = Paragraphs $prose })
                $title = $null
                continue
            }
            if ($fences -ge 2) { continue }
            $prose.Add($line)
        }
        if ($title) { $examples.Add([pscustomobject]@{ Title = $title; Prose = Paragraphs $prose }) }
        $examples
    }

    # Every '### <TypeName>' entry under OUTPUTS, with the prose beneath it - the type from the
    # heading and the prose from the lines under it, read the same way MarkdownExamples reads an
    # example: entering and leaving on '## ' headings of its own, a '### ' line starting a new
    # entry rather than ending the section, and a fence dropped rather than read as markdown.
    function MarkdownOutputs([string[]] $lines) {
        $outputs = [System.Collections.Generic.List[object]]::new()
        $in     = $false
        $type   = $null
        $prose  = [System.Collections.Generic.List[string]]::new()
        $fenced = $false
        foreach ($line in $lines) {
            if ($line -match '^## OUTPUTS\s*$') { $in = $true; continue }
            if (-not $in) { continue }
            if ($line -match '^```') { $fenced = -not $fenced; continue }
            if ($fenced) { continue }
            if ($line -match '^## ') { break }
            if ($line -match '^### (.+)$') {
                if ($type) { $outputs.Add([pscustomobject]@{ Type = $type; Prose = Paragraphs $prose }) }
                $type = $Matches[1].Trim()
                $prose.Clear()
                continue
            }
            if (-not $type) { continue }
            $prose.Add($line)
        }
        if ($type) { $outputs.Add([pscustomobject]@{ Type = $type; Prose = Paragraphs $prose }) }
        $outputs
    }

    $script:MamlXml = if (Test-Path $script:Maml) { [xml](Get-Content $script:Maml -Raw) } else { $null }
}

Describe 'Compiled help matches its markdown source' {
    It 'the MAML exists' {
        Test-Path $script:Maml | Should -BeTrue
    }

    It 'carries no description the markdown has replaced' {
        # Phrases that were true of an older release and are now wrong. Each one shipped in
        # Get-Help while the markdown already said otherwise.
        $stale = @(
            @{ Text = 'ODataType';  Why = '2.0.0 replaced PSObjects with hashtables; @odata.type is a key now' }
            @{ Text = 'instead of PSObjects'; Why = '-Raw returns raw JSON instead of hashtables' }
            @{ Text = 'ephemeral and deleted on success'; Why = 'Sync-MgxDelta gained -CheckpointPath in 2.1.0' }
            @{ Text = 'belongs to a chunk that was refused'; Why = 'after a chunk failure the batch-level retry pass is skipped for every candidate, including ones a cleanly answered chunk left failing' }
            @{ Text = 'its request went out and may have been applied'; Why = 'a refusal status also covers a POST an open circuit or a rate limiter stopped before it left' }
        )
        $raw = Get-Content $script:Maml -Raw
        $hits = $stale | Where-Object { $raw -match [regex]::Escape($_.Text) }
        ($hits | ForEach-Object { "$($_.Text) -- $($_.Why)" }) -join '; ' |
            Should -BeNullOrEmpty -Because 'the MAML is generated from module/help; run ./build.ps1 to regenerate it'
    }

    It 'carries the synopsis, description, outputs and examples its markdown gives' {
        # The parameter check below was the whole guard, so a SYNOPSIS saying the wrong thing
        # entirely, a replaced OUTPUTS section, or a renamed example all shipped green. Comparing
        # a section by its opening sentence closed only half of that: the opening survives a
        # rewrite of everything under it, so a DESCRIPTION whose remaining paragraphs said
        # "deletes every user in the tenant" passed, and so did an example whose prose described
        # something its code does not do, and so did a mutated OUTPUTS type. Prose is compared
        # whole, including each OUTPUTS entry's - a cmdlet can document more than one return type,
        # and each carries its own type and its own prose.
        $mismatches = [System.Collections.Generic.List[string]]::new()

        foreach ($md in Get-ChildItem $script:HelpDir -Filter '*.md') {
            $cmdlet = $md.BaseName
            $lines  = Get-Content $md.FullName
            $node = $script:MamlXml.helpItems.command |
                Where-Object { $_.details.name.Trim() -eq $cmdlet } | Select-Object -First 1
            if (-not $node) { $mismatches.Add("$cmdlet : absent from MAML"); continue }

            $wantSyn = Paragraphs (SectionLines $lines 'SYNOPSIS')
            $gotSyn  = MamlParas $node "*[local-name()='details']/*[local-name()='description']/*[local-name()='para']"
            if ($wantSyn -ne $gotSyn) {
                $mismatches.Add("$cmdlet SYNOPSIS : $(Divergence $wantSyn $gotSyn)")
            }

            $wantDesc = Paragraphs (SectionLines $lines 'DESCRIPTION')
            $gotDesc  = MamlParas $node "*[local-name()='description']/*[local-name()='para']"
            if ($wantDesc -ne $gotDesc) {
                $mismatches.Add("$cmdlet DESCRIPTION : $(Divergence $wantDesc $gotDesc)")
            }

            $mdOutputs   = @(MarkdownOutputs $lines)
            $mamlOutputs = @($node.SelectNodes("*[local-name()='returnValues']/*[local-name()='returnValue']"))
            for ($i = 0; $i -lt [Math]::Min($mdOutputs.Count, $mamlOutputs.Count); $i++) {
                $wantType = Normalize $mdOutputs[$i].Type
                $gotType  = Normalize $mamlOutputs[$i].SelectSingleNode("*[local-name()='type']/*[local-name()='name']").InnerText
                if ($wantType -ne $gotType) {
                    $mismatches.Add("$cmdlet OUTPUTS $($i + 1) type : markdown '$wantType' MAML '$gotType'")
                }

                $wantOutProse = $mdOutputs[$i].Prose
                $gotOutProse  = MamlParas $mamlOutputs[$i] "*[local-name()='description']/*[local-name()='para']"
                if ($wantOutProse -ne $gotOutProse) {
                    $mismatches.Add("$cmdlet OUTPUTS $($i + 1) prose : $(Divergence $wantOutProse $gotOutProse)")
                }
            }
            if ($mdOutputs.Count -ne $mamlOutputs.Count) {
                $mismatches.Add("$cmdlet OUTPUTS : markdown names $($mdOutputs.Count) type(s), MAML carries $($mamlOutputs.Count)")
            }

            $mdExamples   = @(MarkdownExamples $lines)
            $mamlExamples = @($node.SelectNodes("*[local-name()='examples']/*[local-name()='example']"))
            for ($i = 0; $i -lt [Math]::Min($mdExamples.Count, $mamlExamples.Count); $i++) {
                # The generator pads a title out to a fixed width with dashes, so the title is
                # the one thing here compared by containment rather than whole.
                $wantTitle = $mdExamples[$i].Title
                $gotTitle  = Normalize $mamlExamples[$i].SelectSingleNode("*[local-name()='title']").InnerText
                if ($gotTitle -notlike "*$wantTitle*") {
                    $mismatches.Add("$cmdlet example $($i + 1) : markdown '$wantTitle' MAML '$gotTitle'")
                }

                $wantProse = $mdExamples[$i].Prose
                $gotProse  = MamlParas $mamlExamples[$i] "*[local-name()='remarks']/*[local-name()='para']"
                if ($wantProse -ne $gotProse) {
                    $mismatches.Add("$cmdlet example $($i + 1) prose : $(Divergence $wantProse $gotProse)")
                }
            }
        }

        ($mismatches -join "`n") | Should -BeNullOrEmpty -Because 'run ./build.ps1 to regenerate the compiled help'
    }

    It 'reads a list the way the generator compiles it, seams and all' {
        # No compared section carries a list today, so nothing above exercises this. Each shape
        # pins the para LIST New-ExternalHelp (0.14.2) actually compiled its markdown to on a
        # scratch help file: a plain item gets its own para, a wrapped continuation folds into
        # the para of the item it continues, and a lead-in line folds into the first item after
        # it - a different break position per shape. MarkdownParaList rebuilds that same list from
        # the markdown side independently, so the two are compared para for para - count and each
        # para's text - rather than joined into one string where a fixture that merged or split a
        # shape's paras would still read as a match.
        $shapes = @(
            @{ Name = 'plain two items'; Markdown = @('- Item one', '- Item two')
                Maml = @('- Item one', '- Item two') }
            @{ Name = 'plain three items'; Markdown = @('- Item one', '- Item two', '- Item three')
                Maml = @('- Item one', '- Item two', '- Item three') }
            @{ Name = '* items'; Markdown = @('* Item one', '* Item two')
                Maml = @('* Item one', '* Item two') }
            @{ Name = 'a nested item'
                Markdown = @('- Item one', '- Item two', '  - Nested under two')
                Maml     = @('- Item one', '- Item two', '  - Nested under two') }
            @{ Name = 'a wrapped item'
                Markdown = @('- Item one', '  continued here.', '- Item two')
                Maml     = @('- Item one   continued here. - Item two') }
            @{ Name = 'a wrapped first item'
                Markdown = @('- Item one', '  continued here.', '- Item two', '- Item three')
                Maml     = @('- Item one   continued here. - Item two', '- Item three') }
            @{ Name = 'a lead-in line'
                Markdown = @('Lead prose.', '- Item one', '- Item two')
                Maml     = @('Lead prose. - Item one', '- Item two') }
        )

        foreach ($shape in $shapes) {
            $want = @(MarkdownParaList $shape.Markdown)
            $got  = @(MamlParaList (AsMamlParas $shape.Maml) "*[local-name()='para']")
            $got.Count | Should -Be $want.Count -Because "the $($shape.Name) shape should compile to $($want.Count) para(s)"
            for ($i = 0; $i -lt $want.Count; $i++) {
                $got[$i] | Should -Be $want[$i] -Because "the $($shape.Name) shape's para $($i + 1) should match para for para"
            }
        }
    }

    It 'still catches a changed word once the seams are out of the comparison' {
        # Stripping whitespace must not blind the comparison to the prose itself - only to where
        # the generator chose to break it.
        $want = Paragraphs @('- Item one', '- Item two')
        $got  = MamlParas (AsMamlParas @('- Item one', '- Item deux')) "*[local-name()='para']"
        $want | Should -Not -Be $got
    }

    It 'still catches a parenthetical that lost its own space' {
        # module/help/Enable-MgxResilience.md, measured: nothing about "(Get-MgUser, Get-MgGroup,
        # etc.)" tells platyPS to fold the sentence after it into the same paragraph, so a MAML
        # that lost the space there is a real drift, not the one platyPS itself produces.
        $want = Paragraphs @('After calling this cmdlet, all SDK cmdlets (Get-MgUser, Get-MgGroup, etc.) automatically gain resilience with zero script changes.')
        $got  = MamlParas (AsMamlParas @('After calling this cmdlet, all SDK cmdlets (Get-MgUser, Get-MgGroup, etc.)automatically gain resilience with zero script changes.')) "*[local-name()='para']"
        $want | Should -Not -Be $got -Because 'only a rendered link''s own ")" may lose its following space'
    }

    It 'tolerates the space a rendered link''s fold actually drops' {
        # module/help/Invoke-MgxBatchRequest.md DESCRIPTION, measured with New-ExternalHelp
        # (0.14.2): the paragraph after a link folds into the link's own <maml:para> with no
        # space at the seam, so the markdown's two paragraphs and the MAML's one must still
        # compare equal.
        $want = Paragraphs @('Source: [Combine multiple HTTP requests using JSON batching](https://learn.microsoft.com/en-us/graph/json-batching)', '', 'Pipeline input can be string URLs.')
        $got  = MamlParas (AsMamlParas @('Source: Combine multiple HTTP requests using JSON batching (https://learn.microsoft.com/en-us/graph/json-batching)Pipeline input can be string URLs.')) "*[local-name()='para']"
        $want | Should -Be $got -Because "platyPS's own fold at a link boundary is not a drift"
    }

    It 'documents every parameter with the description its markdown gives' {
        # Whole descriptions, not their openings. A comparison of the first 60 characters passes
        # every edit that appends to a parameter which already said something true - a widened
        # range, a sentence naming what a gate covers, a sentence naming what a redaction reaches
        # - and those are most of what a help edit is.
        $mismatches = [System.Collections.Generic.List[string]]::new()

        foreach ($md in Get-ChildItem $script:HelpDir -Filter '*.md') {
            $cmdlet = $md.BaseName
            $lines = Get-Content $md.FullName

            # ### -Name  followed by prose until the ```yaml block
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -notmatch '^### -(\w+)$') { continue }
                $param = $Matches[1]
                $desc = [System.Collections.Generic.List[string]]::new()
                for ($j = $i + 1; $j -lt $lines.Count; $j++) {
                    # The yaml block ends the description. A fence that is not it belongs to the
                    # description and is compared with it: the generator renders a fenced block
                    # verbatim and eats the asterisks of anything written into prose, so a
                    # description naming a literal like ***REDACTED*** has to fence it.
                    if ($lines[$j] -match '^```yaml' -or $lines[$j] -match '^### ' -or $lines[$j] -match '^## ') { break }
                    if ($lines[$j].Trim()) { $desc.Add($lines[$j]) }
                }
                $want = Normalize ($desc -join ' ')
                if (-not $want) { continue }

                $node = $script:MamlXml.helpItems.command |
                    Where-Object { $_.details.name.Trim() -eq $cmdlet } |
                    ForEach-Object { $_.parameters.parameter } |
                    Where-Object { $_.name -eq $param } |
                    Select-Object -First 1
                if (-not $node) { $mismatches.Add("$cmdlet -$param : absent from MAML"); continue }

                $got = Normalize (($node.description.para | ForEach-Object { $_ }) -join ' ')
                if ($got -ne $want) {
                    $mismatches.Add("$cmdlet -$param : $(Divergence $want $got)")
                }
            }
        }

        ($mismatches -join "`n") | Should -BeNullOrEmpty -Because 'run ./build.ps1 to regenerate the compiled help'
    }

    It 'shows every parameter of the cmdlet in its SYNTAX block' {
        # The checks above compare sections and parameter descriptions, and a parameter can have
        # a section of its own while the SYNTAX block does not list it - which is the block a
        # reader looks at first, and the only place that says which parameters go together.
        # -BatchChunkConcurrency had a full section and a range the cmdlet enforces, and was
        # missing from the line above it.
        #
        # -ProgressAction is off both sides. PowerShell 7.4 made it a common parameter, so
        # Get-Command reports it among those, while platyPS 0.14.2 does not know it and writes it
        # into the block as an ordinary one: comparing it would say every block is wrong.
        $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        Import-Module "$repo/module/mgx.psd1" -Force
        $common = [System.Management.Automation.PSCmdlet]::CommonParameters
        $mismatches = [System.Collections.Generic.List[string]]::new()

        foreach ($md in Get-ChildItem $script:HelpDir -Filter '*.md') {
            $cmdInfo = Get-Command -Name $md.BaseName -ErrorAction Stop
            $cmdInfo | Should -Not -BeNullOrEmpty -Because "$($md.BaseName) must resolve for this comparison to mean anything"

            # Each fenced block under ## SYNTAX, keyed by the parameter set the heading above it
            # names. A single-set cmdlet has no heading, and Get-Command calls that set
            # __AllParameterSets.
            $blocks = @{}
            $inSyntax = $false
            $inFence = $false
            $setName = $null
            $buffer = $null
            foreach ($line in Get-Content $md.FullName) {
                if ($line -match '^## SYNTAX\s*$') { $inSyntax = $true; continue }
                if ($inSyntax -and $line -match '^## ') { break }
                if (-not $inSyntax) { continue }
                if ($line -match '^### (.+)$') {
                    $setName = ($Matches[1] -replace '\s*\(Default\)\s*$', '').Trim()
                    continue
                }
                if ($line -match '^```') {
                    if ($inFence) {
                        $key = if ($setName) { $setName } else { '__AllParameterSets' }
                        $blocks[$key] = $buffer -join ' '
                        $inFence = $false
                        $buffer = $null
                    }
                    else { $inFence = $true; $buffer = @() }
                    continue
                }
                if ($inFence) { $buffer += $line }
            }

            if ($blocks.Count -eq 0) {
                $mismatches.Add("$($md.BaseName) : no SYNTAX block")
                continue
            }

            foreach ($key in $blocks.Keys) {
                $set = $cmdInfo.ParameterSets | Where-Object { $_.Name -eq $key }
                if (-not $set) {
                    $mismatches.Add("$($md.BaseName) SYNTAX [$key] : the cmdlet has no such parameter set (it has $(($cmdInfo.ParameterSets.Name) -join ', '))")
                    continue
                }

                # A '-' that opens a parameter follows a '[' or a space, which is what keeps the
                # one in the cmdlet's own name out of the list.
                $shown = @([regex]::Matches($blocks[$key], '(?:^|[\s\[])-([A-Za-z]\w*)') |
                    ForEach-Object { $_.Groups[1].Value } |
                    Where-Object { $_ -ne 'ProgressAction' } | Sort-Object -Unique)
                $takes = @($set.Parameters.Name |
                    Where-Object { $_ -notin $common -and $_ -ne 'ProgressAction' } |
                    Sort-Object -Unique)

                $absent = @($takes | Where-Object { $_ -notin $shown })
                $extra = @($shown | Where-Object { $_ -notin $takes })
                if ($absent) { $mismatches.Add("$($md.BaseName) SYNTAX [$key] : absent $($absent -join ', ')") }
                if ($extra) { $mismatches.Add("$($md.BaseName) SYNTAX [$key] : names $($extra -join ', '), which the cmdlet does not take") }
            }
        }

        ($mismatches -join "`n") | Should -BeNullOrEmpty -Because 'a SYNTAX block should list the parameters its cmdlet takes'
    }

    It 'gives every parameter one yaml block naming the type its cmdlet declares' {
        # The block under a parameter's heading is the only place the markdown states the type,
        # the parameter sets, the default and the pipeline binding, and platyPS builds both the
        # compiled parameter entry and the SYNTAX line out of it. A section with no block leaves
        # Get-Help reporting no type at all - -ConsistencyLevel had none, and the block that
        # belonged to it sat under -ThrottlePriority's, where nothing reads it - and a section
        # with two makes the second dead text. The checks above compare prose, so neither showed.
        #
        # Type: is the .NET type's short name, what Type.Name gives: Int32, String,
        # SwitchParameter, ActionPreference, Hashtable, and an array keeps its brackets, String[].
        # Never the namespace-qualified name.
        $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        Import-Module "$repo/module/mgx.psd1" -Force
        $mismatches = [System.Collections.Generic.List[string]]::new()

        foreach ($md in Get-ChildItem $script:HelpDir -Filter '*.md') {
            $cmdInfo = Get-Command -Name $md.BaseName -ErrorAction Stop
            $lines = Get-Content $md.FullName

            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -notmatch '^### -(\w+)$') { continue }
                $param = $Matches[1]

                # Only a ```yaml fence opens one. A description may carry a fence of its own -
                # the generator eats the asterisks of a literal written into prose - and its
                # closing fence is bare, so counting openings keeps the two apart.
                $blocks = 0
                $inYaml = $false
                $types = [System.Collections.Generic.List[string]]::new()
                for ($j = $i + 1; $j -lt $lines.Count; $j++) {
                    if ($lines[$j] -match '^### ' -or $lines[$j] -match '^## ') { break }
                    if ($lines[$j] -match '^```yaml\s*$') { $blocks++; $inYaml = $true; continue }
                    if ($inYaml -and $lines[$j] -match '^```\s*$') { $inYaml = $false; continue }
                    if ($inYaml -and $lines[$j] -match '^Type:\s*(.+)$') { $types.Add($Matches[1].Trim()) }
                }

                if ($blocks -ne 1) {
                    $mismatches.Add("$($md.BaseName) -$param : $blocks yaml blocks under the heading, not 1")
                    continue
                }
                if (-not $cmdInfo.Parameters.ContainsKey($param)) {
                    $mismatches.Add("$($md.BaseName) -$param : the cmdlet takes no such parameter")
                    continue
                }

                $declared = $cmdInfo.Parameters[$param].ParameterType.Name
                if ($types.Count -ne 1) {
                    $mismatches.Add("$($md.BaseName) -$param : the yaml block names no Type; the cmdlet declares $declared")
                }
                elseif ($types[0] -ne $declared) {
                    $mismatches.Add("$($md.BaseName) -$param : the yaml block says Type: $($types[0]), the cmdlet declares $declared")
                }
            }
        }

        ($mismatches -join "`n") | Should -BeNullOrEmpty -Because "a parameter's yaml block should name the type its cmdlet declares"
    }
}

Describe 'Documented output types match the cmdlets' {
    # The guard above keeps the MAML matching its markdown. It cannot see markdown that is simply
    # wrong about the code - both files said PSObject while the cmdlets declared and emitted
    # their own types. This compares the documentation against [OutputType].
    It 'every cmdlet documents the type it declares' {
        $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        Import-Module "$repo/module/mgx.psd1" -Force
        $wrong = [System.Collections.Generic.List[string]]::new()

        foreach ($cmd in Get-Command -Module mgx -CommandType Cmdlet) {
            $declared = @($cmd.OutputType | ForEach-Object { $_.Type.FullName } | Where-Object { $_ })
            if (-not $declared) { continue }

            $md = Join-Path $repo "module/help/$($cmd.Name).md"
            if (-not (Test-Path $md)) { continue }

            $documented = $null
            $inOutputs = $false
            foreach ($line in Get-Content $md) {
                if ($line -match '^## OUTPUTS\s*$') { $inOutputs = $true; continue }
                if ($inOutputs -and $line -match '^## ')      { break }
                if ($inOutputs -and $line -match '^### (.+)$') { $documented = $Matches[1].Trim(); break }
            }
            if (-not $documented) { continue }

            if ($declared -notcontains $documented) {
                $wrong.Add("$($cmd.Name): documents '$documented', declares '$($declared -join ", ")'")
            }
        }

        ($wrong -join "`n") | Should -BeNullOrEmpty -Because 'module/help OUTPUTS should name the type the cmdlet emits'
    }
}
