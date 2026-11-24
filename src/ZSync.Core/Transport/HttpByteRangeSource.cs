using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace ZSync.Core.Transport
{
    /// <summary>
    /// Implements byte range reading over HTTP and HTTPS using standard RFC 7233 HTTP Range requests.
    /// Compatible with Nginx, Apache, GitLab Generic Package Registry, S3, MinIO, and CDNs.
    /// </summary>
    public sealed class HttpByteRangeSource : IByteRangeSource
    {
        private readonly Uri _uri;
        private readonly HttpClient _httpClient;
        private readonly bool _disposeClient;
        private long? _cachedLength;

        public HttpByteRangeSource(string url, HttpClient? httpClient = null)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentNullException(nameof(url));
            _uri = new Uri(url);
            if (httpClient == null)
            {
                _httpClient = new HttpClient();
                _disposeClient = true;
            }
            else
            {
                _httpClient = httpClient;
                _disposeClient = false;
            }
        }

        public async Task<long> GetContentLengthAsync(CancellationToken cancellationToken = default)
        {
            if (_cachedLength.HasValue)
            {
                return _cachedLength.Value;
            }

            using var req = new HttpRequestMessage(HttpMethod.Head, _uri);
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            if (resp.Content.Headers.ContentLength.HasValue)
            {
                _cachedLength = resp.Content.Headers.ContentLength.Value;
                return _cachedLength.Value;
            }

            // Fallback to GET with zero bytes range
            using var rangeReq = new HttpRequestMessage(HttpMethod.Get, _uri);
            rangeReq.Headers.Range = new RangeHeaderValue(0, 0);
            using var rangeResp = await _httpClient.SendAsync(rangeReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (rangeResp.Content.Headers.ContentRange?.Length.HasValue == true)
            {
                _cachedLength = rangeResp.Content.Headers.ContentRange.Length.Value;
                return _cachedLength.Value;
            }

            throw new InvalidOperationException($"Unable to determine content length for URL '{_uri}'.");
        }

        public async Task<byte[]> ReadRangeAsync(long offset, int length, CancellationToken cancellationToken = default)
        {
            if (length <= 0) return Array.Empty<byte>();

            using var req = new HttpRequestMessage(HttpMethod.Get, _uri);
            req.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            return await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task ReadRangeToStreamAsync(long offset, int length, Stream destination, CancellationToken cancellationToken = default)
        {
            var bytes = await ReadRangeAsync(offset, length, cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposeClient)
            {
                _httpClient.Dispose();
            }
        }
    }
}
