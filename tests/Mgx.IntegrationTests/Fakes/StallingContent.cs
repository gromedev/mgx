using System.Net;
using System.Text;

namespace Mgx.IntegrationTests;

/// <summary>
/// A response body that delivers a prefix and then stops, without closing the stream, and ends
/// only when the read's own token is canceled. Requests go out with
/// HttpCompletionOption.ResponseHeadersRead, so neither HttpClient.Timeout nor the pipeline's
/// attempt timeout covers the content read at all - the engine's BodyReadTimeout is the only
/// thing that bounds it, and this is what makes that bound observable in-process.
/// </summary>
public sealed class StallingContent : HttpContent
{
    private readonly byte[] _prefix;
    private readonly long _declaredLength;

    /// <param name="prefix">Delivered before the stall, so the body looks like one that started.</param>
    /// <param name="declaredLength">What Content-Length claims, so a reader keeps waiting for the rest.</param>
    public StallingContent(string prefix = "{", long declaredLength = 4096)
        : this(Encoding.UTF8.GetBytes(prefix), declaredLength)
    {
    }

    public StallingContent(byte[] prefix, long declaredLength = 4096)
    {
        _prefix = prefix;
        _declaredLength = declaredLength;
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => await SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(_prefix, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    /// <summary>
    /// The stall as a reader on the content stream sees it. HttpContent's own implementation of
    /// this buffers the whole body first and drops the cancellation token doing it, so a caller
    /// that reads the body as a stream - a collection page, a $batch envelope, a content
    /// download - waits on this content forever instead of hitting its read timeout. A real
    /// response hands the stream over as soon as the headers are in and stalls on the read;
    /// this does the same, on the read's own token.
    /// </summary>
    protected override Task<Stream> CreateContentReadStreamAsync()
        => Task.FromResult<Stream>(new StallingStream(_prefix));

    protected override bool TryComputeLength(out long length)
    {
        length = _declaredLength;
        return true;
    }

    private sealed class StallingStream(byte[] prefix) : Stream
    {
        private int _delivered;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_delivered < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _delivered);
                prefix.AsSpan(_delivered, count).CopyTo(buffer.Span);
                _delivered += count;
                return count;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _delivered; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
