using System.Net;
using System.Text;
using Mgx.IntegrationTests.Fakes;

namespace Mgx.IntegrationTests;

/// <summary>
/// A Graph collection that exists only as a function of the request: every page is computed
/// from the skip token the previous page's nextLink carried, so a hundred thousand items are
/// one programmed entry rather than a hundred thousand queued responses. Nothing is enumerated
/// in advance, and nothing here is mutable, so the handler can answer from it under its lock or
/// outside it.
/// <para>
/// It is registered through the request-keyed programming and takes exactly the precedence
/// every other entry does: a fault aimed at one page is an entry registered BEFORE this one -
/// the first matching entry answers - and the pages either side of it still come from here.
/// </para>
/// </summary>
public sealed class PagedCollection
{
    /// <summary>What this generator's nextLinks carry, and what <see cref="PageOf"/> reads.</summary>
    private const string SkipToken = "$skiptoken=page";

    private readonly string _idPrefix;

    /// <param name="total">Items in the whole collection.</param>
    /// <param name="pageSize">Items on every page but the last.</param>
    /// <param name="resourceUrl">The collection's URL with no query string. Every request whose
    /// URI is this, or this followed by a query, is one of these pages.</param>
    /// <param name="trailingEmptyPage">The shape Graph returns when the collection divides
    /// evenly: the last full page still carries a nextLink, and following it yields an empty
    /// page. False ends the collection with the last full page's absent nextLink instead, and
    /// true is refused on a total that does not divide evenly.</param>
    /// <param name="idPrefix">Item ids are this followed by the item's 1-based position.</param>
    public PagedCollection(
        int total,
        int pageSize,
        string resourceUrl = "https://graph.microsoft.com/v1.0/users",
        bool trailingEmptyPage = false,
        string idPrefix = "user")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        // Graph appends its empty page behind a full one. A collection that does not divide
        // evenly ends at a short page, and a nextLink behind a short page is a shape the service
        // never returns - served here it would make a boundary test pass against a wire no
        // caller will ever meet.
        if (trailingEmptyPage && total % pageSize != 0)
            throw new ArgumentException(
                $"A trailing empty page is the shape Graph returns when the collection divides "
                + $"evenly; {total} items served {pageSize} at a time ends at a short page.",
                nameof(trailingEmptyPage));

        Total = total;
        PageSize = pageSize;
        ResourceUrl = resourceUrl;
        _idPrefix = idPrefix;

        // An empty collection is still one page: a request has to be answered with something.
        var full = Math.Max(1, (total + pageSize - 1) / pageSize);
        PageCount = trailingEmptyPage ? full + 1 : full;
    }

    public int Total { get; }

    public int PageSize { get; }

    public string ResourceUrl { get; }

    /// <summary>Pages a complete enumeration asks for, and so the requests it costs.</summary>
    public int PageCount { get; }

    /// <summary>Where an enumeration starts, page size and all.</summary>
    public string InitialUrl => $"{ResourceUrl}?$top={PageSize}";

    /// <summary>The link page <paramref name="page"/>'s predecessor carries; <paramref name="page"/> is 2 or more.</summary>
    public string NextLinkTo(int page) => $"{ResourceUrl}?{SkipToken}{page}";

    /// <summary>
    /// Which page a request asks for, 1-based: the number in the skip token, or 1 for the
    /// initial URL, which carries none.
    /// </summary>
    public static int PageOf(string uri)
    {
        var token = uri.IndexOf(SkipToken, StringComparison.Ordinal);
        if (token < 0) return 1;

        var start = token + SkipToken.Length;
        var end = start;
        while (end < uri.Length && char.IsAsciiDigit(uri[end])) end++;
        return end > start ? int.Parse(uri[start..end]) : 1;
    }

    /// <summary>Whether <paramref name="uri"/> addresses this collection rather than some other resource.</summary>
    private bool Addresses(string uri) =>
        uri.StartsWith(ResourceUrl, StringComparison.Ordinal)
        && (uri.Length == ResourceUrl.Length || uri[ResourceUrl.Length] == '?');

    /// <summary>The predicate a handler entry is keyed on.</summary>
    public bool Matches(MockRequest request) => Addresses(request.Uri);

    /// <summary>
    /// Page <paramref name="page"/> as a Graph collection response. A page past the end is a
    /// thrown exception rather than an empty body: the iterator asking for one is the defect
    /// these tests exist to catch, and an obliging answer would hide it.
    /// </summary>
    public string BodyFor(int page)
    {
        if (page < 1 || page > PageCount)
            throw new InvalidOperationException(
                $"Page {page} was requested from a collection of {Total} items served "
                + $"{PageSize} at a time, which is {PageCount} page(s).");

        var already = (page - 1) * PageSize;
        var count = Math.Clamp(Total - already, 0, PageSize);

        var json = new StringBuilder(count * 24 + 128).Append("{\"value\":[");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) json.Append(',');
            json.Append("{\"id\":\"").Append(_idPrefix).Append(already + i + 1).Append("\"}");
        }
        json.Append(']');
        if (page < PageCount)
            json.Append(",\"@odata.nextLink\":\"").Append(NextLinkTo(page + 1)).Append('"');
        return json.Append('}').ToString();
    }

    /// <summary>The answer to one request, for a handler entry armed with a factory.</summary>
    public HttpResponseMessage Answer(MockRequest request) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                BodyFor(PageOf(request.Uri)), Encoding.UTF8, "application/json")
        };

    /// <summary>
    /// Program <paramref name="handler"/> to serve this collection, and hand the handler back so
    /// further entries chain. Entries registered first win, so a fault on one page goes on
    /// before this call.
    /// </summary>
    public MockHttpHandler ServeOn(MockHttpHandler handler) => handler.When(Matches).Respond(Answer);

    /// <inheritdoc cref="ServeOn(MockHttpHandler)"/>
    public StubHttpMessageHandler ServeOn(StubHttpMessageHandler handler) =>
        handler.When(Matches).Respond(Answer);
}
