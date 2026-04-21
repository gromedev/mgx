using System.Runtime.CompilerServices;
using System.Text.Json;
using Mgx.Engine.Http;

namespace Mgx.Engine.Pagination;

/// <summary>
/// State for resuming a previously interrupted pagination stream.
/// Constructed by the consumer from a loaded <see cref="PaginationCheckpoint"/>.
/// </summary>
public sealed record ResumeState(string NextLink, int SkipOnFirstPage, long ItemsAlreadyCollected);

/// <summary>
/// Information about a completed page, passed to the consumer via callback.
/// </summary>
public sealed record PageCompletedInfo(string? NextPageUrl);

/// <summary>
/// Streaming page iterator that follows @odata.nextLink and yields items
/// via IAsyncEnumerable for immediate pipeline output. Does not perform
/// checkpoint I/O; the consumer owns checkpoint lifecycle.
/// </summary>
public sealed class PageIterator
{
    private readonly ResilientGraphClient _client;

    /// <summary>
    /// Maximum consecutive empty pages before breaking to prevent infinite loops.
    /// Graph API should never return empty pages with nextLink on regular endpoints.
    /// Delta endpoints CAN return many empty pages with nextLink between the data
    /// and the final deltaLink page (observed: 15+ empty pages on /users/delta).
    /// When onDeltaLink is provided, the limit is raised to 1000 to allow delta
    /// pagination to reach the final page while still guarding against Graph bugs.
    /// </summary>
    private const int MaxConsecutiveEmptyPages = 3;
    private const int MaxConsecutiveEmptyPagesDelta = 1000;

    public PageIterator(ResilientGraphClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Stream items and also capture @odata.count from the first page.
    /// Supports resume via optional <paramref name="resume"/> state.
    /// Fires <paramref name="onPageComplete"/> after each page is fully yielded.
    /// </summary>
    /// <remarks>
    /// SkipOnFirstPage uses positional skip, which assumes
    /// the Graph API returns the same page content on re-fetch. If items were
    /// added or deleted between crash and resume, positional skip may produce
    /// duplicates or miss items. This is inherent to skiptoken-based pagination;
    /// Graph does not provide idempotency tokens for collection endpoints.
    /// </remarks>
    public async IAsyncEnumerable<JsonElement> StreamAllWithCountAsync(
        string initialUrl,
        int maxItems,
        Action<long>? onCount,
        Dictionary<string, string>? headers = null,
        ResumeState? resume = null,
        Action<PageCompletedInfo>? onPageComplete = null,
        Action<string>? onDeltaLink = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var expectedHost = new Uri(initialUrl);
        string? nextLink = resume?.NextLink ?? initialUrl;
        long totalYielded = resume?.ItemsAlreadyCollected ?? 0;
        int skipOnFirstPage = resume?.SkipOnFirstPage ?? 0;
        bool isFirstPage = true;
        bool countCaptured = false;
        int consecutiveEmptyPages = 0;
        var emptyPageLimit = onDeltaLink != null ? MaxConsecutiveEmptyPagesDelta : MaxConsecutiveEmptyPages;

        while (nextLink != null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await _client.GetCollectionPageAsync(nextLink, cancellationToken, headers);

            if (!countCaptured && page.Count.HasValue)
            {
                onCount?.Invoke(page.Count.Value);
                countCaptured = true;
            }

            // Capture deltaLink from the final page of a delta query response.
            // Validated against expectedHost before surfacing.
            if (page.DeltaLink != null)
            {
                var validatedDelta = NextLinkValidator.Validate(page.DeltaLink, expectedHost);
                if (validatedDelta != null)
                    onDeltaLink?.Invoke(validatedDelta);
            }

            if (page.Value.Length == 0)
            {
                consecutiveEmptyPages++;
                if (consecutiveEmptyPages >= emptyPageLimit)
                    break;
            }
            else
            {
                consecutiveEmptyPages = 0;
            }

            int skippedOnPage = 0;
            foreach (var item in page.Value)
            {
                if (isFirstPage && skippedOnPage < skipOnFirstPage)
                {
                    skippedOnPage++;
                    continue;
                }

                yield return item;
                totalYielded++;

                if (maxItems > 0 && totalYielded >= maxItems)
                    yield break;
            }

            nextLink = NextLinkValidator.Validate(page.NextLink, expectedHost);
            isFirstPage = false;

            onPageComplete?.Invoke(new PageCompletedInfo(nextLink));
        }
    }
}
