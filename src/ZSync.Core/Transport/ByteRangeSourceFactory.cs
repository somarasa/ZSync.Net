using System;
using System.IO;
using System.Net.Http;

namespace ZSync.Core.Transport
{
    public static class ByteRangeSourceFactory
    {
        public static IByteRangeSource Create(string urlOrPath, HttpClient? httpClient = null)
        {
            if (string.IsNullOrWhiteSpace(urlOrPath))
            {
                throw new ArgumentNullException(nameof(urlOrPath));
            }

            if (Uri.TryCreate(urlOrPath, UriKind.Absolute, out var uri))
            {
                if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                    uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpByteRangeSource(urlOrPath, httpClient);
                }

                if (uri.IsFile || uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
                {
                    return new FileByteRangeSource(uri.LocalPath);
                }
            }

            // Local filesystem path or Windows SMB/UNC share (e.g. \\server\share\file or /path/to/file)
            return new FileByteRangeSource(urlOrPath);
        }
    }
}
