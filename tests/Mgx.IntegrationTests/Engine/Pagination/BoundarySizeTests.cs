using System.Management.Automation;
using System.Net;
using System.Text;
using System.Text.Json;
using Mgx.Engine.Http;
using Mgx.Engine.Pagination;
using Mgx.IntegrationTests.Fakes;

namespace Mgx.IntegrationTests;

/// <summary>
/// Collection sizes that sit on a page seam: exactly full, one over, and the 999/1000/1001
/// band around Graph's largest page. An off-by-one in the follow-the-nextLink loop drops the
/// last page or the last item, and only these sizes expose it.
/// (Corpus: M365DSC-7274, silent truncation.)
/// </summary>
[Collection("Pipeline")]
public class BoundarySizeTests
{
    /// <summary>One page of <paramref name="count"/> items, with a nextLink unless it is last.</summary>
    private static string Page(int firstIndex, int count, int pageNumber, bool isLast)
    {
        var json = new StringBuilder("""{"value":[""");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) json.Append(',');
            json.Append($$"""{"id":"user{{firstIndex + i}}"}""");
        }
        json.Append(']');
        if (!isLast)
        {
            json.Append(""","@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=page""");
            json.Append(pageNumber + 1).Append('"');
        }
        json.Append('}');
        return json.ToString();
    }

    /// <summary>
    /// What a complete enumeration means, whatever armed the wire: every item, each exactly
    /// once, from the first id to the last, over exactly the pages the nextLink chain describes.
    /// Count alone does not say it - a loop that re-fetches a page it has already read arrives
    /// at the right total whenever the last page is short by as much as it repeated.
    /// </summary>
    private static async Task AssertWholeCollection(
        MockHttpHandler handler, string initialUrl, int total, int expectedPages)
    {
        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        var ids = new List<string>(total);
        await foreach (var item in iterator.StreamAllWithCountAsync(initialUrl, maxItems: 0, onCount: null))
        {
            ids.Add(item.GetProperty("id").GetString()!);
        }

        Assert.Equal(total, ids.Count);
        Assert.Distinct(ids);
        Assert.Equal("user1", ids[0]);
        Assert.Equal($"user{total}", ids[^1]);
        Assert.Equal(expectedPages, handler.RequestCount);
        Assert.All(handler.Requests.Skip(1).Select((r, i) => (r, i)),
            x => Assert.Equal($"https://graph.microsoft.com/v1.0/users?$skiptoken=page{x.i + 2}",
                x.r.RequestUri!.ToString()));
    }

    /// <summary>
    /// <paramref name="trailingEmptyPage"/> is the shape Graph actually returns when the
    /// collection divides evenly: the last full page still carries a nextLink, and following
    /// it yields an empty page. Without it a full page is simply the end, which is a
    /// single-request enumeration that crosses no seam at all.
    /// </summary>
    [Theory]
    [InlineData(100, 100, false)]   // exactly one full page, ended by its own absent nextLink
    [InlineData(100, 100, true)]    // the same size, ended by an empty page behind a nextLink
    [InlineData(101, 100, false)]   // one item spills into a second page
    [InlineData(999, 999, false)]   // exactly Graph's largest page
    [InlineData(999, 999, true)]    // and the same, with the empty page Graph appends
    [InlineData(1000, 999, false)]  // one item past it
    [InlineData(1001, 999, false)]  // two items past it
    public async Task Every_item_comes_back_at_a_page_boundary_size(int total, int pageSize, bool trailingEmptyPage)
    {
        var handler = new MockHttpHandler();
        var expectedPages = 0;
        for (var sent = 0; sent < total; sent += pageSize)
        {
            var count = Math.Min(pageSize, total - sent);
            handler.QueueResponse(HttpStatusCode.OK,
                Page(sent + 1, count, expectedPages + 1, isLast: sent + count == total && !trailingEmptyPage));
            expectedPages++;
        }
        if (trailingEmptyPage)
        {
            handler.QueueResponse(HttpStatusCode.OK, Page(total + 1, 0, expectedPages + 1, isLast: true));
            expectedPages++;
        }

        // MockHttpHandler answers in queue order regardless of URL, so the counts hold even for
        // an iterator that re-sent the initial URL. The URL assertion is what pins the chain
        // here; the generated cases below key the answer on the URL instead, so there a repeat
        // hands back a page already seen and the distinctness assertion is what catches it.
        await AssertWholeCollection(handler,
            $"https://graph.microsoft.com/v1.0/users?$top={pageSize}", total, expectedPages);
    }

    /// <summary>
    /// The same seams over pages computed from the request rather than queued in advance, which
    /// is what lets 100,000 items be a size like any other: the queue would need 101 responses
    /// built before the first one is sent.
    /// </summary>
    [Theory]
    [InlineData(100, 100, false)]
    [InlineData(100, 100, true)]
    [InlineData(101, 100, false)]
    [InlineData(999, 999, false)]
    [InlineData(999, 999, true)]
    [InlineData(1000, 999, false)]
    [InlineData(1001, 999, false)]
    [InlineData(100_000, 999, false)]   // 101 pages, the size no queue can hold
    public async Task Every_item_comes_back_from_a_generated_collection(int total, int pageSize, bool trailingEmptyPage)
    {
        var pages = new PagedCollection(total, pageSize, trailingEmptyPage: trailingEmptyPage);
        var handler = new MockHttpHandler();
        pages.ServeOn(handler);

        await AssertWholeCollection(handler, pages.InitialUrl, total, pages.PageCount);
    }

    /// <summary>
    /// One seam over the scripted handler as well. The generator is programmed on both of the
    /// suite's handlers, and the scripted one resolves a keyed entry ahead of its own queue -
    /// which it has none of here, so a page it did not key would be a request with nothing to
    /// answer it rather than a page quietly repeated.
    /// </summary>
    [Fact]
    public async Task Every_item_comes_back_from_a_generated_collection_over_the_scripted_handler()
    {
        const int total = 1001;
        var pages = new PagedCollection(total, pageSize: 999);
        var handler = new StubHttpMessageHandler();
        pages.ServeOn(handler);

        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, new ResilientGraphClientOptions { NoRateLimit = true });
        var iterator = new PageIterator(client);

        var ids = new List<string>(total);
        await foreach (var item in iterator.StreamAllWithCountAsync(pages.InitialUrl, maxItems: 0, onCount: null))
        {
            ids.Add(item.GetProperty("id").GetString()!);
        }

        Assert.Equal(total, ids.Count);
        Assert.Distinct(ids);
        Assert.Equal("user1", ids[0]);
        Assert.Equal($"user{total}", ids[^1]);
        Assert.Equal(pages.PageCount, handler.RequestCount);
        Assert.All(handler.Requests.Skip(1).Select((r, i) => (r, i)),
            x => Assert.Equal(pages.NextLinkTo(x.i + 2), x.r.Uri));
    }

    /// <summary>
    /// One pass of the largest size through the cmdlet, because the iterator's guarantee is not
    /// the caller's: Export-MgxCollection writes each item to a temp as it arrives and moves
    /// that over the output at the end, and a line written twice or a page lost between the
    /// iterator and the file is invisible to every assertion above. -PageSize is left at its
    /// default, so the first request also states what that default is worth.
    /// </summary>
    [Fact]
    public void An_export_of_a_hundred_thousand_items_writes_each_one_once()
    {
        const int total = 100_000;
        var pages = new PagedCollection(total, pageSize: 999);
        var handler = new MockHttpHandler();
        pages.ServeOn(handler);

        using var transport = MgxTransportScope.Inject(handler);
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-boundary-{Guid.NewGuid():N}")).FullName;
        var output = Path.Combine(dir, "out.jsonl");
        try
        {
            using var ps = PowerShell.Create();
            ps.AddCommand("Import-Module")
              .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddCommand("Export-MgxCollection")
              .AddParameter("Uri", "/users")
              .AddParameter("OutputFile", output)
              .AddParameter("All")
              .AddParameter("WarningAction", ActionPreference.SilentlyContinue);
            ps.Invoke();

            // Before the file is read at all: an export that wrote an error record produced a
            // file that says nothing about what the run was asked to do, and counting its lines
            // would report on the wrong thing.
            Assert.Empty(ps.Streams.Error.Select(e => e.FullyQualifiedErrorId));

            var seen = new HashSet<string>(total);
            var lines = 0;
            foreach (var line in File.ReadLines(output))
            {
                lines++;
                using var item = JsonDocument.Parse(line);
                var id = item.RootElement.GetProperty("id").GetString();
                Assert.True(seen.Add(id!), $"line {lines} repeats id {id}");
            }

            Assert.Equal(total, lines);
            Assert.Equal(pages.PageCount, handler.RequestCount);
            Assert.Equal("https://graph.microsoft.com/v1.0/users?$top=999",
                handler.CapturedRequests[0].Uri);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

/// <summary>
/// The generator's own guards. It stands in for Graph at a page seam, so a shape the service
/// never returns has to be refused rather than served, and a page past the end of the collection
/// is the defect the boundary tests exist to catch rather than something to answer.
/// </summary>
public class PagedCollectionTests
{
    [Fact]
    public void A_trailing_empty_page_is_refused_on_a_total_that_does_not_divide_evenly()
    {
        // 1001 items at 999 a page end on a short page of two. Graph's empty page comes behind
        // a full one; behind a short one it is a nextLink no collection ever carries.
        var refused = Assert.Throws<ArgumentException>(
            () => new PagedCollection(1001, pageSize: 999, trailingEmptyPage: true));
        Assert.Equal("trailingEmptyPage", refused.ParamName);

        // The evenly dividing sizes the boundary theory passes keep their empty page.
        Assert.Equal(2, new PagedCollection(999, pageSize: 999, trailingEmptyPage: true).PageCount);
        Assert.Equal(3, new PagedCollection(1998, pageSize: 999, trailingEmptyPage: true).PageCount);
    }

    [Fact]
    public void A_page_past_the_end_is_refused_rather_than_answered()
    {
        var pages = new PagedCollection(1001, pageSize: 999);
        Assert.Equal(2, pages.PageCount);

        // The last page is short and carries no nextLink, so nothing should ask for a third.
        // An empty body here would let an iterator that asked anyway look correct.
        var past = Assert.Throws<InvalidOperationException>(() => pages.BodyFor(pages.PageCount + 1));
        Assert.Contains("Page 3", past.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => pages.BodyFor(0));
    }
}
