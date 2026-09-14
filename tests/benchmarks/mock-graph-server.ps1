# Mock Graph endpoint for the benchmark gauntlets. Started and stopped by the benchmark
# that needs it (06-fault-gauntlet.ps1 and the pathological gauntlets after it).
#
# Division of labor. Two kinds of fault live here and nowhere else:
#   - faults every contender must meet identically. The gauntlets compare mgx against the
#     Graph SDK and against a bare Invoke-RestMethod loop, so a fault injected inside mgx
#     is not a fault the other two can face; it has to be a property of the server.
#   - faults that need a real socket: a connection dropped mid-body, a body trickled over
#     seconds, a body that stops mid-document under an honest Content-Length.
# Everything asserted in-process belongs to the xUnit fakes instead - the handlers, sessions
# and canned collections under tests/Mgx.IntegrationTests/Fakes, which answer a request
# without a socket. Those are cheaper, they run in the unit suite, and they can see the
# request objects. Do not grow this server for a fault one of them already holds.
#
# Routes:
#   GET /(v1.0|beta)/users/{id}   The entity. With no plan in force it carries the default
#       schedule, keyed on the entity id so request order cannot change what a contender
#       meets:
#         id % 100  0-14  -> first attempt gets 429 (Retry-After: 1), then success
#         id % 100 15-17  -> first two attempts get 503 (Retry-After: 1), then success
#         otherwise       -> success
#   GET /(v1.0|beta)/users        The collection. One page of mock users by default; with
#       "pages" in the plan, that many pages joined by a real @odata.nextLink the client
#       has to follow, items shaped like the entity route's.
#   anything else                 A benign empty page. An unknown route is not a fault, so
#       a benchmark that pages one measures nothing rather than failing - which is why a
#       scenario needing a real collection asks for one of the routes above by name. A
#       plan rule can still target it; the empty page is the body it works on.
#
# Control endpoints. Matched before any route, so a plan whose pattern matches every path
# cannot lock the server out:
#   GET  /ping    liveness
#   GET  /reset   clears attempt counters and rule clocks; KEEPS the plan, because 06
#                 resets between contenders and every contender must meet the same plan
#   GET  /stats   server-side truth (below)
#   GET  /plan    the plan in force
#   POST /plan    sets it, replacing whatever was there; {"rules":[]} clears it. Screened
#                 where it is written, not on the request that would have met it: the document's
#                 own top-level fields must be ones the document can have, it must be an object
#                 whose 'rules' is an array, every rule's field names must be ones a rule can
#                 have, the route must compile as a regex, the kind must be one of the seven,
#                 the scope must be path or server, and every numeric field must be a number of
#                 at least zero. A plan failing any of those is refused 400 MockPlanRejected
#                 naming the field, and the plan already in force stays in force.
#
# The plan. POST /plan takes:
#   {
#     "pages":    3,                     # list routes serve this many pages (default 1)
#     "pageSize": 5,                     # items per list page (default 25)
#     "rules": [ {
#       "route":      "^/v1\\.0/users$", # .NET regex over the URL path; the query is not
#                                        #   part of it, so every page of one enumeration
#                                        #   is another attempt on the same route
#       "kind":       "abort",           # one of the kinds below
#       "scope":      "path",            # count attempts per path (default), or "server"
#                                        #   for one counter across every matching path -
#                                        #   what a token expiry or a tenant-wide storm is
#       "from":       3,                 # first attempt it fires on (default 1)
#       "count":      1,                 # how many attempts it fires for (default: no end)
#       "to":         3,                 # the same thing said as a last attempt
#       "seconds":    10,                # and fire only this long after the rule's first
#                                        #   matching request; combines with the attempt
#                                        #   window rather than replacing it
#       "bytes":      64,                # abort, truncatedJson: body bytes to send
#       "bodyMs":     2000,              # slowBody: how long the body takes
#       "chunks":     10,                # slowBody: how many pieces to trickle it in
#       "retryAfter": 1                  # throttleStorm, outage: the header value
#     } ]
#   }
# The first rule whose route matches governs the request, and a route the plan governs does
# not also get the default schedule: outside a rule's window it simply succeeds. Kinds:
#   abort              headers and part of the body, then the connection dies
#   slowBody           the whole body, trickled over bodyMs
#   truncatedJson      a complete response whose JSON stops mid-document
#   expiredToken       401 with WWW-Authenticate, the shape an expired token shows
#   delayedVisibility  404 while the window is open, then the resource appears
#   throttleStorm      429 with Retry-After for every request in the window
#   outage             503 for every attempt; with no window it never recovers
#
# /stats reports the three fields it always did - served, a body the entity route served
# whole, and faulted429/faulted503, which stay the default schedule's own tally - then one
# counter per kind under the kind's own name, then pagesServed, a list page whose body went
# out whole, and serverErrors, a request this server itself failed to answer. Every counter
# is raised after its response has been written, so /stats never affirms a fault that never
# reached the wire. A plan fault counts under its kind only - not served, and not
# pagesServed - so a run's default-schedule numbers stay comparable across releases.
#
# The accept loop is single-threaded, as it always has been: one request is answered
# before the next is read. A slowBody rule therefore holds up whatever else is in flight,
# which is the honest shape of a stalled response on a shared connection - but it means a
# scenario should trickle one body, not two hundred.
param(
    [int] $Port = 8787
)

$ErrorActionPreference = 'Stop'
# Retry-After and duration values are rendered into headers and JSON, so the process must
# not be able to print 1,5 for 1.5 on a comma-decimal machine.
[System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
Write-Host "mock graph listening on http://localhost:$Port/"

$attempts  = @{}                                    # default schedule: entity id -> attempts
$ruleState = @{}                                    # plan: "<rule>|<key>" -> @{ n; first }
$plan      = @{ pages = 1; pageSize = 25; rules = @() }
$kinds     = 'abort', 'slowBody', 'truncatedJson', 'expiredToken',
             'delayedVisibility', 'throttleStorm', 'outage'
$stats     = [ordered]@{
    served = 0; faulted429 = 0; faulted503 = 0
    abort = 0; slowBody = 0; truncatedJson = 0; expiredToken = 0
    delayedVisibility = 0; throttleStorm = 0; outage = 0
    pagesServed = 0; serverErrors = 0
}

# The numeric plan fields, and what each must convert to. Every one of them is cast where it
# is used, deep inside a response a client is already waiting on, so they are screened here
# instead - see Test-PlanNumber.
$ruleNumbers = [ordered]@{
    from = 'int'; count = 'int'; to = 'int'; bytes = 'int'
    bodyMs = 'int'; chunks = 'int'; retryAfter = 'int'; seconds = 'double'
}
# Every field a rule may carry: the numeric ones above, plus the three structural fields no
# kind owns. Built from $ruleNumbers rather than listed again by hand, so a numeric field
# added there is automatically a known field here too.
$ruleFieldNames = @($ruleNumbers.Keys) + @('route', 'kind', 'scope')
# The plan document's own top-level fields. A name outside this list - "pagez" for
# "pageSize" - is not an error Get-Field can see: it just looks like a field the document
# does not set, and the default is used in its place. Screened the same way a rule's own
# fields are, so a typo here fails the same way a typo there does.
$planFieldNames = @('rules', 'pages', 'pageSize')

function Send-Json($ctx, [int]$status, [string]$json, [hashtable]$headers) {
    $ctx.Response.StatusCode = $status
    if ($headers) { foreach ($k in $headers.Keys) { $ctx.Response.AddHeader($k, $headers[$k]) } }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $ctx.Response.ContentType = 'application/json'
    $ctx.Response.ContentLength64 = $bytes.Length
    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $ctx.Response.Close()
}

# The honest length is announced and only part of the body delivered, then the connection
# is killed: the client sees the transport end, not a status code.
function Send-Aborted($ctx, [string]$json, [int]$bytes) {
    $full = [System.Text.Encoding]::UTF8.GetBytes($json)
    if ($bytes -le 0 -or $bytes -ge $full.Length) { $bytes = [math]::Max(1, [int]($full.Length / 2)) }
    $ctx.Response.StatusCode = 200
    $ctx.Response.ContentType = 'application/json'
    $ctx.Response.ContentLength64 = $full.Length
    $ctx.Response.OutputStream.Write($full, 0, $bytes)
    $ctx.Response.OutputStream.Flush()
    $ctx.Response.Abort()
}

# A complete, well-formed HTTP response - Content-Length matches what is sent, the
# connection closes cleanly - whose JSON stops mid-document. The client fails parsing,
# not reading.
function Send-Truncated($ctx, [string]$json, [int]$bytes) {
    $full = [System.Text.Encoding]::UTF8.GetBytes($json)
    if ($bytes -le 0 -or $bytes -ge $full.Length) { $bytes = [math]::Max(1, [int]($full.Length / 2)) }
    $ctx.Response.StatusCode = 200
    $ctx.Response.ContentType = 'application/json'
    $ctx.Response.ContentLength64 = $bytes
    $ctx.Response.OutputStream.Write($full, 0, $bytes)
    $ctx.Response.Close()
}

# The whole body, delivered in $chunks pieces spaced to spend about $ms total between the
# first and the last. One chunk is still a body the client waited $ms for, but the loop below
# only sleeps in the gap between writes - and there is no gap when there is one write - so
# that case sleeps before it instead.
function Send-Slow($ctx, [string]$json, [int]$ms, [int]$chunks) {
    $full = [System.Text.Encoding]::UTF8.GetBytes($json)
    if ($chunks -lt 1) { $chunks = 10 }
    if ($chunks -gt $full.Length) { $chunks = $full.Length }
    $ctx.Response.StatusCode = 200
    $ctx.Response.ContentType = 'application/json'
    $ctx.Response.ContentLength64 = $full.Length
    if ($chunks -eq 1) {
        if ($ms -gt 0) { Start-Sleep -Milliseconds $ms }
        $ctx.Response.OutputStream.Write($full, 0, $full.Length)
        $ctx.Response.OutputStream.Flush()
        $ctx.Response.Close()
        return
    }
    $size = [math]::Max(1, [int][math]::Ceiling($full.Length / $chunks))
    $gap  = [math]::Max(1, [int]($ms / [math]::Max(1, $chunks - 1)))
    $off  = 0
    while ($off -lt $full.Length) {
        $n = [math]::Min($size, $full.Length - $off)
        $ctx.Response.OutputStream.Write($full, $off, $n)
        $ctx.Response.OutputStream.Flush()
        $off += $n
        if ($off -lt $full.Length) { Start-Sleep -Milliseconds $gap }
    }
    $ctx.Response.Close()
}

# [ordered], not a plain hashtable: a hashtable hands ConvertTo-Json its keys in whatever
# order that process happened to hash them, so the same id answered by two servers is two
# different bodies - and a server whose point is determinism cannot serve those.
function New-MockUser([int]$id) {
    [ordered]@{
        id                = "00000000-0000-0000-0000-{0:D12}" -f $id
        displayName       = "Mock User $id"
        userPrincipalName = "mock.u$id@mock.local"
        mail              = "mock.u$id@mock.local"
        department        = 'Mockestration'
        jobTitle          = 'Test Subject'
    }
}

function Get-ListPage([string]$version, [string]$origin, [int]$page) {
    $size  = if ($plan.pageSize -gt 0) { [int]$plan.pageSize } else { 25 }
    $pages = if ($plan.pages -gt 0)    { [int]$plan.pages }    else { 1 }
    if ($page -lt 1) { $page = 1 }
    $first = ($page - 1) * $size + 1
    $doc = [ordered]@{
        '@odata.context' = "$origin/$version/`$metadata#users"
        value            = @($first..($first + $size - 1) | ForEach-Object { New-MockUser $_ })
    }
    if ($page -lt $pages) {
        $doc['@odata.nextLink'] = "$origin/$version/users?`$skiptoken=p$($page + 1)"
    }
    ConvertTo-Json -InputObject $doc -Depth 5 -Compress
}

function Get-Field($obj, [string]$name, $default) {
    if ($null -eq $obj) { return $default }
    $p = $obj.PSObject.Properties[$name]
    if ($p -and $null -ne $p.Value) { return $p.Value }
    $default
}

# A numeric plan field that is not a number throws where it is cast, which is halfway
# through answering a request: the client is left holding an empty 200 and the counter for a
# fault that never went out. Screened here, the same typo is a 400 naming the field.
function Test-PlanNumber($container, [string]$name, [string]$type, [string]$where) {
    $value = Get-Field $container $name $null
    if ($null -eq $value) { return }
    try { $number = if ($type -eq 'double') { [double]$value } else { [int]$value } }
    catch { throw "$where '$name' must be a number, not '$value'" }
    if ($number -lt 0 -or -not [double]::IsFinite([double]$number)) {
        throw "$where '$name' must be a number of at least 0, not '$value'"
    }
}

# from, to, count and seconds describe a firing window, and Test-RuleFires treats a zero on
# any of them as no bound rather than a bound at zero: an explicit from: 0 clears the lower
# bound instead of matching nothing, and to/count/seconds: 0 clears the upper bound the same
# way leaving the field out does - so a rule meant to fire briefly fires forever instead.
# Called after Test-PlanNumber has already confirmed the value is a number of at least 0, so
# the only thing left to refuse here is exactly 0.
function Test-PlanWindowField($container, [string]$name, [int]$ruleNumber) {
    $value = Get-Field $container $name $null
    if ($null -eq $value -or [double]$value -ge 1) { return }
    throw "rule ${ruleNumber}: '$name' must be at least 1, got $value"
}

# True when this attempt falls inside the rule's window. The attempt window always
# applies; a "seconds" duration narrows it further, measured from the rule's first
# matching request so a scenario's clock starts when its traffic does, not when the plan
# was posted.
function Test-RuleFires([int]$index, $rule, [string]$path) {
    $key = if ([string](Get-Field $rule 'scope' 'path') -eq 'server') { "$index|*" } else { "$index|$path" }
    if (-not $ruleState.ContainsKey($key)) { $ruleState[$key] = @{ n = 0; first = [DateTime]::UtcNow } }
    $st = $ruleState[$key]
    $st.n++

    $from = [int](Get-Field $rule 'from' 1)
    $to   = [int](Get-Field $rule 'to' 0)
    if ($to -le 0) {
        $count = [int](Get-Field $rule 'count' 0)
        if ($count -gt 0) { $to = $from + $count - 1 }
    }
    if ($st.n -lt $from) { return $false }
    if ($to -gt 0 -and $st.n -gt $to) { return $false }

    $seconds = [double](Get-Field $rule 'seconds' 0)
    if ($seconds -gt 0 -and ([DateTime]::UtcNow - $st.first).TotalSeconds -ge $seconds) { return $false }
    $true
}

# Returns 'whole' when the route's own body went out complete (trickled counts: all of it
# arrives), 'partial' when a fault cut it short, 'error' when a status fault replaced it, and
# $null when no rule governs this path - in which case the caller carries on with the default
# schedule. A caller counting a served body - pagesServed, served - counts 'whole' and
# nothing else; see Test-DispositionServed.
function Invoke-PlanRule($ctx, [string]$path, [string]$body) {
    if ($plan.rules.Count -eq 0) { return $null }
    $index = -1
    for ($i = 0; $i -lt $plan.rules.Count; $i++) {
        if ($path -match [string]$plan.rules[$i].route) { $index = $i; break }
    }
    if ($index -lt 0) { return $null }

    $rule = $plan.rules[$index]
    if (-not (Test-RuleFires $index $rule $path)) { Send-Json $ctx 200 $body $null; return 'whole' }

    $retry = [string][int](Get-Field $rule 'retryAfter' 1)
    switch ([string]$rule.kind) {
        'abort' {
            Send-Aborted $ctx $body ([int](Get-Field $rule 'bytes' 0))
            $stats.abort++
            return 'partial'
        }
        'slowBody' {
            Send-Slow $ctx $body ([int](Get-Field $rule 'bodyMs' 1000)) ([int](Get-Field $rule 'chunks' 10))
            $stats.slowBody++
            return 'whole'
        }
        'truncatedJson' {
            Send-Truncated $ctx $body ([int](Get-Field $rule 'bytes' 0))
            $stats.truncatedJson++
            return 'partial'
        }
        'expiredToken' {
            Send-Json $ctx 401 '{"error":{"code":"InvalidAuthenticationToken","message":"Access token has expired."}}' `
                @{ 'WWW-Authenticate' = 'Bearer realm="", error="invalid_token", error_description="The access token has expired."' }
            $stats.expiredToken++
            return 'error'
        }
        'delayedVisibility' {
            Send-Json $ctx 404 '{"error":{"code":"Request_ResourceNotFound","message":"Resource does not exist or one of its queried reference-property objects are not present."}}' $null
            $stats.delayedVisibility++
            return 'error'
        }
        'throttleStorm' {
            Send-Json $ctx 429 '{"error":{"code":"TooManyRequests","message":"mock throttle storm"}}' @{ 'Retry-After' = $retry }
            $stats.throttleStorm++
            return 'error'
        }
        'outage' {
            Send-Json $ctx 503 '{"error":{"code":"ServiceUnavailable","message":"mock outage"}}' @{ 'Retry-After' = $retry }
            $stats.outage++
            return 'error'
        }
        default {
            # Unreachable while Set-Plan screens kinds, and loud rather than benign if that
            # ever stops being true: a scenario must not report success against a fault
            # that never fired.
            Send-Json $ctx 500 (@{ error = @{ code = 'MockPlanError'
                                              message = "unknown fault kind '$($rule.kind)'" } } |
                                ConvertTo-Json -Compress -Depth 3) $null
            return 'error'
        }
    }
}

# The predicate every disposition-counting caller shares: only 'whole' is a body this route
# actually served. 'partial' and 'error' reached the wire too, but under a fault's own shape,
# and are counted under that fault's kind instead - never here as well.
function Test-DispositionServed([string]$disposition) {
    $disposition -eq 'whole'
}

function Set-Plan($ctx) {
    $raw = [System.IO.StreamReader]::new($ctx.Request.InputStream, [System.Text.Encoding]::UTF8).ReadToEnd()
    try {
        # -NoEnumerate, or a one-element array document - [{"rules":[]}] - comes back out of
        # ConvertFrom-Json as that one element, not the array it was: PSCustomObject passes the
        # shape check below on the object the array held, and a document that is not the
        # documented shape is accepted as though it were.
        $new = if ($raw.Trim()) { $raw | ConvertFrom-Json -NoEnumerate } else { $null }
        # A document that is not an object at all (an array, a bare string, a number) has no
        # properties to read 'rules' off, and Get-Field's empty default for a missing property
        # is indistinguishable from {"rules":[]} - the documented way to clear the plan on
        # purpose. So the shape is checked directly, on the object graph ConvertFrom-Json
        # already built, before anything reads a field out of it.
        if ($new -isnot [System.Management.Automation.PSCustomObject]) {
            $shape = if ($null -eq $new) { 'empty' } else { $new.GetType().Name }
            throw "plan document must be a JSON object with a 'rules' array, not $shape"
        }
        # $rulesProp.Value is read by direct member access everywhere below, never assigned
        # through an if/else branch: passing an array through a script block's trailing
        # expression - even just to pick a default - re-enumerates it onto the pipeline, and a
        # one-rule plan comes back out the other side as that one rule instead of an array of
        # it. @() on the way into the foreach would paper over the read, not fix it.
        $rulesProp = $new.PSObject.Properties['rules']
        if (-not $rulesProp -or $rulesProp.Value -isnot [array]) {
            $shape = if (-not $rulesProp -or $null -eq $rulesProp.Value) { 'missing' } else { $rulesProp.Value.GetType().Name }
            throw "plan 'rules' must be an array, not $shape"
        }
        $rules = $rulesProp.Value
        # Screened here, not on the first request that would have matched: a scenario whose
        # plan does not mean what it says must fail where it is written. Nothing below is
        # replaced until the whole document has passed, so a refused plan leaves the one in
        # force untouched rather than half of it.
        foreach ($name in $new.PSObject.Properties.Name) {
            if ($name -notin $script:planFieldNames) {
                throw "plan has an unknown field '$name'; expected one of $($script:planFieldNames -join ', ')"
            }
        }
        Test-PlanNumber $new 'pages' 'int' 'plan'
        Test-PlanNumber $new 'pageSize' 'int' 'plan'
        for ($i = 0; $i -lt $rules.Count; $i++) {
            $r = $rules[$i]
            if (-not $r.route -or -not $r.kind) { throw 'every rule needs a route and a kind' }
            $null = [regex]::new([string]$r.route)
            if ([string]$r.kind -notin $script:kinds) {
                throw "unknown fault kind '$($r.kind)'; expected one of $($script:kinds -join ', ')"
            }
            foreach ($name in $r.PSObject.Properties.Name) {
                if ($name -notin $script:ruleFieldNames) {
                    throw "rule '$($r.kind)' has an unknown field '$name'; expected one of $($script:ruleFieldNames -join ', ')"
                }
            }
            $scope = [string](Get-Field $r 'scope' 'path')
            if ($scope -notin 'path', 'server') { throw "scope must be path or server, not '$scope'" }
            foreach ($field in $script:ruleNumbers.Keys) {
                Test-PlanNumber $r $field $script:ruleNumbers[$field] "rule '$($r.kind)'"
            }
            foreach ($field in 'from', 'to', 'count', 'seconds') {
                Test-PlanWindowField $r $field ($i + 1)
            }
        }
        $plan.rules    = $rules
        $plan.pages    = [int](Get-Field $new 'pages' 1)
        $plan.pageSize = [int](Get-Field $new 'pageSize' 25)
        $ruleState.Clear()
        Send-Json $ctx 200 (@{ ok = $true; rules = $rules.Count } | ConvertTo-Json -Compress) $null
    }
    catch {
        Send-Json $ctx 400 (@{ error = @{ code = 'MockPlanRejected'; message = $_.Exception.Message } } | ConvertTo-Json -Compress) $null
    }
}

while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    $path = $ctx.Request.Url.AbsolutePath

    try {
        switch -Regex ($path) {
            '^/ping$'  { Send-Json $ctx 200 '{"ok":true}' $null; continue }
            '^/reset$' { $attempts.Clear(); $ruleState.Clear(); Send-Json $ctx 200 '{"reset":true}' $null; continue }
            '^/stats$' { Send-Json $ctx 200 (ConvertTo-Json -InputObject $stats -Compress) $null; continue }
            '^/plan$'  {
                if ($ctx.Request.HttpMethod -in 'POST', 'PUT') { Set-Plan $ctx }
                else { Send-Json $ctx 200 (ConvertTo-Json -InputObject $plan -Depth 5 -Compress) $null }
                continue
            }
            '^/(v1\.0|beta)/users/(?<id>[^/?]+)$' {
                $id = [int]($Matches['id'] -replace '\D', '0')
                $user = New-MockUser $id | ConvertTo-Json -Compress
                $disposition = Invoke-PlanRule $ctx $path $user
                if ($null -ne $disposition) {
                    # A plan fault does not count as served, same predicate as the list
                    # route's pagesServed: only a 'whole' disposition is a body this route
                    # actually served.
                    if (Test-DispositionServed $disposition) { $stats.served++ }
                    continue
                }

                if (-not $attempts.ContainsKey($id)) { $attempts[$id] = 0 }
                $attempts[$id]++
                $n = $attempts[$id]
                $bucket = $id % 100

                if ($bucket -lt 15 -and $n -eq 1) {
                    Send-Json $ctx 429 '{"error":{"code":"TooManyRequests","message":"mock throttle"}}' @{ 'Retry-After' = '1' }
                    $stats.faulted429++
                }
                elseif ($bucket -ge 15 -and $bucket -lt 18 -and $n -le 2) {
                    Send-Json $ctx 503 '{"error":{"code":"ServiceUnavailable","message":"mock outage"}}' @{ 'Retry-After' = '1' }
                    $stats.faulted503++
                }
                else {
                    Send-Json $ctx 200 $user $null
                }
                $stats.served++
                continue
            }
            '^/(v1\.0|beta)/users$' {
                # The list route counts under pagesServed and nowhere else: served is the
                # entity route's tally, and a 2.1.4 run has to stay comparable with this one.
                $version = $Matches[1]
                $origin = $ctx.Request.Url.GetLeftPart([System.UriPartial]::Authority)
                $page = if ($ctx.Request.Url.Query -match '\$skiptoken=p(\d+)') { [int]$Matches[1] } else { 1 }
                $body = Get-ListPage $version $origin $page
                $disposition = Invoke-PlanRule $ctx $path $body
                if ($null -eq $disposition) { Send-Json $ctx 200 $body $null; $disposition = 'whole' }
                # A page cut off mid-body is not a page served; it is counted by the kind
                # that cut it.
                if (Test-DispositionServed $disposition) { $stats.pagesServed++ }
                continue
            }
            default {
                # Anything else (org probe, unimplemented endpoints): benign empty page
                $empty = '{"value":[]}'
                if ($null -eq (Invoke-PlanRule $ctx $path $empty)) { Send-Json $ctx 200 $empty $null }
            }
        }
    }
    catch {
        # A closed response nothing was written to reaches the client as 200 with an empty
        # body, which a benchmark reads as an answer. A harness bug says so instead.
        $stats.serverErrors++
        try {
            Send-Json $ctx 500 (@{ error = @{ code = 'MockServerError'
                                              message = $_.Exception.Message } } |
                                ConvertTo-Json -Compress -Depth 3) $null
        }
        catch { try { $ctx.Response.Close() } catch { } }
    }
}
