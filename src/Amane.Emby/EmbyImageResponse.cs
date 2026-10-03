using Amane.Core;
using MediaBrowser.Common.Net;

namespace Amane.Emby;

/// <summary>图片流交给 Emby，释放流时一同释放 HTTP 响应。</summary>
internal static class EmbyImageResponse
{
    public static async Task<HttpResponseInfo> DownloadAsync(AmaneClient client, string url, CancellationToken cancellationToken)
    {
        var response = await client.GetImageAsync(url, cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseInfo
            {
                Content = new ResponseStream(stream, response),
                ContentType = response.Content.Headers.ContentType?.ToString(),
                ContentLength = response.Content.Headers.ContentLength,
                StatusCode = response.StatusCode,
                ResponseUrl = response.RequestMessage?.RequestUri?.ToString() ?? url,
                Headers = response.Headers.Concat(response.Content.Headers)
                    .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)
            };
        }
        catch { response.Dispose(); throw; }
    }

    private sealed class ResponseStream(Stream stream, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => stream.CanSeek;
        public override bool CanWrite => stream.CanWrite;
        public override long Length => stream.Length;
        public override long Position { get => stream.Position; set => stream.Position = value; }
        public override void Flush() => stream.Flush();
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => stream.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);
        public override void SetLength(long value) => stream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
            if (disposing) { stream.Dispose(); response.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
