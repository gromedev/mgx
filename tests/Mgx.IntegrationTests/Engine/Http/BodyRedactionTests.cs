using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mgx.Cmdlets.Cmdlets.Batch;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests.Engine.Http;

/// <summary>
/// -Debug body redaction. A pre-authenticated download URL fetches the file bytes with no bearer
/// token, and -Debug output is what users paste into issue reports, so this is a credential
/// control - and drive-content-triage.ps1 tells users it exists.
///
/// It previously had NO test at all: deleting both redaction passes left the entire suite green.
/// These pin both directions, because over-redaction is its own regression - @odata.nextLink is
/// how paging bugs get diagnosed.
/// </summary>
public class BodyRedactionTests
{
    private static string Trace(string body) =>
        GraphRequestTracer.FormatResponse(new HttpResponseMessage(HttpStatusCode.OK), 12, body);

    [Theory]
    // SharePoint / OneDrive for Business: capability in the query string.
    [InlineData("""{"@microsoft.graph.downloadUrl":"https://c-my.sharepoint.com/_layouts/15/download.aspx?UniqueId=1&tempauth=eyJ0.LEAKED.sig&ApiVersion=2.0"}""", "LEAKED")]
    // Consumer OneDrive: capability in the PATH, which a query-only cut would miss entirely.
    [InlineData("""{"@microsoft.graph.downloadUrl":"https://public.bl.files.1drv.com/y4mLEAKED/file.bin"}""", "LEAKED")]
    // The older property name for the same thing.
    [InlineData("""{"@content.downloadUrl":"https://c-my.sharepoint.com/x?tempauth=LEAKED"}""", "LEAKED")]
    // Azure SAS.
    [InlineData("""{"uploadUrl":"https://s.blob.core.windows.net/c/b?sv=2021&sig=LEAKED&se=2026"}""", "LEAKED")]
    // Sharing link.
    [InlineData("""{"link":"https://c.sharepoint.com/g?guestaccesstoken=LEAKED"}""", "LEAKED")]
    // Presigned S3-style.
    [InlineData("""{"url":"https://b.s3.amazonaws.com/k?X-Amz-Signature=LEAKED&X-Amz-Expires=60"}""", "LEAKED")]
    // Nested inside a collection, which is how Graph actually returns driveItems.
    [InlineData("""{"value":[{"id":"a"},{"id":"b","@microsoft.graph.downloadUrl":"https://c-my.sharepoint.com/d?tempauth=LEAKED"}]}""", "LEAKED")]
    public void Redacts_pre_authenticated_urls(string body, string secret)
    {
        var trace = Trace(body);
        Assert.DoesNotContain(secret, trace, StringComparison.Ordinal);
        Assert.Contains("<redacted>", trace);
    }

    [Theory]
    // Paging tokens are diagnostics, not credentials. Losing these would remove the thing
    // pagination bugs are debugged with.
    [InlineData("""{"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=RFNwdAoAAQAAA"}""", "RFNwdAoAAQAAA")]
    [InlineData("""{"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=KEEPME"}""", "KEEPME")]
    // Ordinary URLs on a driveItem.
    [InlineData("""{"webUrl":"https://c.sharepoint.com/Shared%20Documents/KEEPME.xlsx"}""", "KEEPME")]
    [InlineData("""{"siteUrl":"https://c.sharepoint.com/sites/KEEPME"}""", "KEEPME")]
    // The (?<![a-z])sig lookbehind must not eat these.
    [InlineData("""{"u":"https://example.com/x?design=KEEPME"}""", "KEEPME")]
    [InlineData("""{"u":"https://example.com/x?config=KEEPME"}""", "KEEPME")]
    [InlineData("""{"u":"https://example.com/x?assign=KEEPME"}""", "KEEPME")]
    // Relative paths, which the item-url pattern reads: a paging link and a $select naming a
    // property that starts with "sig" are both the thing a trace is read for.
    [InlineData("""{"url":"/users?$skiptoken=KEEPME"}""", "KEEPME")]
    [InlineData("""{"url":"/users?$select=signInActivity,KEEPME"}""", "KEEPME")]
    [InlineData("""{"url":"/me/drive/root:/KEEPME.xlsx:/content"}""", "KEEPME")]
    public void Does_not_redact_diagnostics_or_ordinary_urls(string body, string keep)
    {
        Assert.Contains(keep, Trace(body), StringComparison.Ordinal);
    }

    [Fact]
    public void Still_redacts_credential_named_properties()
    {
        var trace = Trace("""{"passwordCredential":{"secretText":"hunter2"}}""");
        Assert.DoesNotContain("hunter2", trace, StringComparison.Ordinal);
    }

    /// <summary>
    /// The trace and the dead-letter file read one list of credential name fragments
    /// (SensitiveNames), so a field the file hides is missing from -Debug output too. These
    /// three are the fragments the file has that a name-by-name tracer would not: a federated
    /// credential's assertion, a profile passphrase, a connection string.
    /// </summary>
    [Theory]
    // A workload-identity JWT: the value IS the credential.
    [InlineData("""{"clientAssertion":"eyJhbGci.LEAKED.c2ln"}""", "LEAKED")]
    // An imported PFX and a Wi-Fi profile both spell it this way.
    [InlineData("""{"passphrase":"LEAKED"}""", "LEAKED")]
    // A connection string carries its credential inline, so the whole value goes.
    [InlineData("""{"connectionString":"Server=db;User=svc;Password=LEAKED"}""", "LEAKED")]
    public void Redacts_the_names_the_dead_letter_file_redacts(string body, string secret)
    {
        var trace = Trace(body);
        Assert.DoesNotContain(secret, trace, StringComparison.Ordinal);
        Assert.Contains("<redacted>", trace);
    }

    /// <summary>
    /// Three items, not two. Sanitize makes two passes that can each match a downloadUrl - one
    /// by property name, one by value - so with only two items a first-match-only regression
    /// was masked: pass one redacted FIRST, pass two redacted SECOND, and the test stayed green
    /// while every third and subsequent capability URL leaked.
    /// </summary>
    /// <summary>
    /// The dead-letter writer's walk over the same body the tracer is given. It reads parsed
    /// JSON rather than text, so the two cannot be one function, but the fragment, the
    /// parameters and the cut are one definition in SensitiveNames - and a URL the trace hides
    /// and the file writes is a live credential on disk, which is the worse half of the pair.
    ///
    /// Serialized the way the writer serializes it: the default encoder escapes an ampersand,
    /// and the query string a caller reads off the line has to be the one that was sent.
    /// </summary>
    private static string FileLine(string body)
    {
        var node = JsonNode.Parse(body);
        node = InvokeMgxBatchRequest.RedactSensitiveFields(node);
        return node!.ToJsonString(new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    [Theory]
    // Intune's content-file body. The SAS is the credential and neither name says so, so only
    // the value rule reaches it; what survives the cut says which blob and which parameter.
    [InlineData("""{"azureStorageUri":"https://s.blob.core.windows.net/c/b?sv=2021-08-06&sig=LEAKEDSAS&se=2026-12-31"}""",
        "LEAKEDSAS", "https://s.blob.core.windows.net/c/b?sv=2021-08-06&sig=")]
    [InlineData("""{"uploadUrl":"https://s.blob.core.windows.net/c/b?sv=2021&sig=LEAKEDSAS"}""",
        "LEAKEDSAS", "https://s.blob.core.windows.net/c/b?sv=2021&sig=")]
    [InlineData("""{"u":"https://c-my.sharepoint.com/x?tempauth=LEAKED&ApiVersion=2.0"}""",
        "LEAKED", "https://c-my.sharepoint.com/x?tempauth=")]
    // The capability sits in the path, so the property name is the only thing that reaches it.
    [InlineData("""{"@microsoft.graph.downloadUrl":"https://public.bl.files.1drv.com/y4mLEAKED/file.bin"}""",
        "LEAKED", null)]
    [InlineData("""{"value":[{"@content.downloadUrl":"https://c-my.sharepoint.com/x?tempauth=LEAKED"}]}""",
        "LEAKED", null)]
    // An array element has no property name at all: the value rule is what is left.
    [InlineData("""{"links":["https://c.sharepoint.com/d?guestaccesstoken=LEAKED"]}""",
        "LEAKED", "https://c.sharepoint.com/d?guestaccesstoken=")]
    // The capability sits in the property NAME rather than in any value: the tracer matches
    // any quoted string in the text alike, key or value, but the file's walk used to read
    // only values, so this key reached the file whole.
    [InlineData("""{"https://h/f?sig=LEAKED":"v"}""",
        "LEAKED", "https://h/f?sig=")]
    // A capability URL used as a key whose text ALSO carries a SensitiveNames fragment -
    // "guestaccesstoken" contains "token", "authkey" contains "key", "passwordreset" contains
    // "password". The fragment check used to run first, take the branch that only clears the
    // value beside the key, and leave the key - secret included - verbatim; the trace's own
    // first pass has this same fragment-matches-the-key case, but its later, URL-only pass
    // still finds and cuts the key as text, which is why the trace never leaked here even
    // though the file did.
    [InlineData("""{"https://h/f?guestaccesstoken=LEAKED":"v"}""",
        "LEAKED", "https://h/f?guestaccesstoken=")]
    [InlineData("""{"https://h/f?authkey=LEAKED":"v"}""",
        "LEAKED", "https://h/f?authkey=")]
    [InlineData("""{"https://h/passwordreset?sig=LEAKED":"v"}""",
        "LEAKED", "https://h/passwordreset?sig=")]
    // A capability URL as a key takes the value beside it, in the trace as in the file: the key
    // IS the credential, so what a body files under one is what that URL was minted for. The
    // trace's URL pass cuts such a key as text but stops at the key's closing quote, so a value
    // the file cleared stood beside a key both of them had cut - and only where the key's own
    // text carried no fragment, which is the signature deciding.
    [InlineData("""{"https://h/f?sig=abc":"KEEPME"}""",
        "KEEPME", "https://h/f?sig=")]
    // The whole body is one string. There is no property and no element for the walk to step
    // into, so it returned having read nothing and the URL reached the file entire; the tracer
    // matches a quoted string wherever it stands and had cut this one all along.
    [InlineData("\"https://s.blob.core.windows.net/c/b?sv=2021&sig=LEAKEDSAS\"",
        "LEAKEDSAS", "https://s.blob.core.windows.net/c/b?sv=2021&sig=")]
    [InlineData("\"https://c-my.sharepoint.com/x?tempauth=LEAKED\"",
        "LEAKED", "https://c-my.sharepoint.com/x?tempauth=")]
    // A capability URL with its scheme and host gone, which is the form a caller reaches for
    // when they hand a batch item a URL Graph gave them relative - the item's own url, and any
    // body value beside it. The tracer reads that form; the file's rule was anchored on the
    // scheme, so the same value was cut in -Debug output and written to disk entire.
    [InlineData("""{"u":"/drives/d/items/i/content?tempauth=LEAKEDJWT"}""",
        "LEAKEDJWT", "/drives/d/items/i/content?tempauth=")]
    [InlineData("""{"uploadUrl":"/c/b?sv=2021&sig=LEAKEDSAS&se=2026"}""",
        "LEAKEDSAS", "/c/b?sv=2021&sig=")]
    [InlineData("""{"links":["/d?guestaccesstoken=LEAKED"]}""",
        "LEAKED", "/d?guestaccesstoken=")]
    // The same form used as a property name, which the key rule reads through the same cut.
    [InlineData("""{"/f?authkey=LEAKED":"v"}""",
        "LEAKED", "/f?authkey=")]
    public void The_dead_letter_file_hides_what_the_trace_hides(string body, string secret, string? kept)
    {
        var trace = Trace(body);
        var line = FileLine(body);

        Assert.DoesNotContain(secret, trace, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, line, StringComparison.Ordinal);

        if (kept != null)
        {
            Assert.Contains(kept, trace, StringComparison.Ordinal);
            Assert.Contains(kept, line, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The other direction, on the side that had no value rule at all. A paging link is a URL
    /// the file is read for, and over-redacting it costs the same thing over-redacting a trace
    /// costs.
    /// </summary>
    [Theory]
    [InlineData("""{"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=RFNwdAoAAQAAA"}""", "RFNwdAoAAQAAA")]
    [InlineData("""{"webUrl":"https://c.sharepoint.com/Shared%20Documents/KEEPME.xlsx"}""", "KEEPME")]
    [InlineData("""{"u":"https://example.com/x?design=KEEPME"}""", "KEEPME")]
    // The relative form the rule above now reads, on the other side of it: a paging link, a
    // $select naming a property that starts with "sig", and a path with neither.
    [InlineData("""{"url":"/users?$skiptoken=KEEPME"}""", "KEEPME")]
    [InlineData("""{"url":"/users?$select=signInActivity,KEEPME"}""", "KEEPME")]
    [InlineData("""{"url":"/me/drive/root:/KEEPME.xlsx:/content"}""", "KEEPME")]
    public void The_dead_letter_file_keeps_the_urls_the_trace_keeps(string body, string keep)
    {
        Assert.Contains(keep, Trace(body), StringComparison.Ordinal);
        Assert.Contains(keep, FileLine(body), StringComparison.Ordinal);
    }

    /// <summary>
    /// A $batch item's own url, as the envelope carries it. Invoke-MgxBatchRequest normalizes
    /// each url to a relative path before the POST - that is the form Graph's batch format takes
    /// - so a pre-authenticated URL a caller passed as an item URL has lost the scheme both URL
    /// patterns anchor on by the time the request is traced, and the tempauth JWT stood in
    /// -Debug output beside a dead-letter line that cut the same URL.
    ///
    /// Traced as a request, which is what a batch envelope is.
    /// </summary>
    [Theory]
    [InlineData("""{"requests":[{"id":"1","method":"PUT","url":"/x?tempauth=LEAKEDJWT"}]}""",
        "LEAKEDJWT", "/x?tempauth=")]
    [InlineData("""{"requests":[{"id":"1","method":"PUT","url":"/c/b?sv=2021&sig=LEAKEDSAS&se=2026"}]}""",
        "LEAKEDSAS", "/c/b?sv=2021&sig=")]
    [InlineData("""{"requests":[{"id":"1","method":"GET","url":"/d?guestaccesstoken=LEAKED"}]}""",
        "LEAKED", "/d?guestaccesstoken=")]
    public void Redacts_a_capability_url_that_has_lost_its_scheme(string envelope, string secret, string kept)
    {
        var trace = GraphRequestTracer.FormatRequest(
            new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/$batch"),
            System.Text.Encoding.UTF8.GetBytes(envelope), 1);

        Assert.DoesNotContain(secret, trace, StringComparison.Ordinal);
        // Which host is gone with the scheme, but which kind of URL still reads off the line.
        Assert.Contains(kept, trace, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacts_every_url_in_a_body_not_just_the_first()
    {
        var trace = Trace("""{"value":[{"@microsoft.graph.downloadUrl":"https://a.sharepoint.com/x?tempauth=FIRST"},{"@microsoft.graph.downloadUrl":"https://b.sharepoint.com/y?tempauth=SECOND"},{"@microsoft.graph.downloadUrl":"https://c.sharepoint.com/z?tempauth=THIRD"}]}""");
        Assert.DoesNotContain("FIRST", trace, StringComparison.Ordinal);
        Assert.DoesNotContain("SECOND", trace, StringComparison.Ordinal);
        Assert.DoesNotContain("THIRD", trace, StringComparison.Ordinal);
    }

    /// <summary>
    /// A capability-URL key whose only SensitiveNames fragment lies in the secret after the
    /// capability parameter: "abc.token.def" carries "token", and "https://h/f?tempauth=" on
    /// its own carries none. The cut takes the fragment away with the secret, so a fragment
    /// check reading only the cut key found nothing and left the value beside the key whole -
    /// a value under a pre-authenticated URL is as much a credential as one under
    /// "guestaccesstoken", whose fragment happens to survive its own cut. The trace never had
    /// the gap: its name pass reads the key as written, before its URL pass cuts anything.
    /// </summary>
    [Fact]
    public void A_key_whose_fragment_is_only_in_the_secret_still_clears_the_value_beside_it()
    {
        const string body = """{"https://h/f?tempauth=abc.token.def":"SECRETVALUE"}""";

        var trace = Trace(body);
        Assert.DoesNotContain("abc.token.def", trace, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETVALUE", trace, StringComparison.Ordinal);

        var line = FileLine(body);
        Assert.DoesNotContain("abc.token.def", line, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETVALUE", line, StringComparison.Ordinal);

        var cutKey = $"https://h/f?tempauth={InvokeMgxBatchRequest.RedactionMarker}";
        var obj = JsonNode.Parse(line)!.AsObject();
        Assert.True(obj.ContainsKey(cutKey), line);
        Assert.Equal(InvokeMgxBatchRequest.RedactionMarker, obj[cutKey]!.GetValue<string>());
        // Which host and which kind of URL still reads off the line, as with any other cut.
        Assert.Contains("https://h/f?tempauth=", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A capability-URL key whose text carries no SensitiveNames fragment at all. The cut on the
    /// key is what says the property is a credential - a pre-authenticated URL as a field name
    /// makes whatever sits beside it the thing that URL was minted for - but the value beside it
    /// was decided separately, on whether the key including its secret happened to read as a
    /// credential name. So "?sig=abc" kept its value and "?sig=abcsecretdef" cleared it, and
    /// which of the two a caller gets is the signature's spelling rather than the property's
    /// meaning.
    /// </summary>
    [Fact]
    public void A_capability_url_key_clears_the_value_beside_it_whatever_its_secret_spells()
    {
        var cutKey = $"https://h/f?sig={InvokeMgxBatchRequest.RedactionMarker}";

        var line = FileLine("""{"https://h/f?sig=abc":"KEEPME"}""");
        var obj = JsonNode.Parse(line)!.AsObject();
        Assert.True(obj.ContainsKey(cutKey), line);
        Assert.Equal(InvokeMgxBatchRequest.RedactionMarker, obj[cutKey]!.GetValue<string>());
        Assert.DoesNotContain("KEEPME", line, StringComparison.Ordinal);

        // The same key with a fragment inside the signature, which was the only spelling that
        // used to take the value with it.
        var withFragment = JsonNode.Parse(
            FileLine("""{"https://h/f?sig=abcsecretdef":"KEEPME"}"""))!.AsObject();
        Assert.Equal(InvokeMgxBatchRequest.RedactionMarker, withFragment[cutKey]!.GetValue<string>());
    }

    /// <summary>
    /// Two keys that cut to the identical redacted string - both are "?sig=", the same
    /// capability parameter, differing only in the secret after it. JsonObject's indexer
    /// replaces an existing key rather than refusing it, so re-adding the second under the
    /// same cut name would silently drop the first property from the file. Both have to
    /// survive, which means they cannot end up with the same name.
    /// </summary>
    [Fact]
    public void Two_keys_that_redact_to_the_same_string_both_survive()
    {
        var line = FileLine("""{"https://h/f?sig=AAA":"1","https://h/f?sig=BBB":"2"}""");
        var obj = JsonNode.Parse(line)!.AsObject();

        Assert.DoesNotContain("AAA", line, StringComparison.Ordinal);
        Assert.DoesNotContain("BBB", line, StringComparison.Ordinal);
        Assert.Equal(2, obj.Count);
        Assert.True(obj.ContainsKey($"https://h/f?sig={InvokeMgxBatchRequest.RedactionMarker}"));
        Assert.True(obj.ContainsKey($"https://h/f?sig={InvokeMgxBatchRequest.RedactionMarker}#2"));
    }
}
