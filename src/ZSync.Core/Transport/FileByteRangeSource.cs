using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ZSync.Core.Transport
{
    /// <summary>
    /// Implements byte range reading over standard local filesystem and Windows SMB / UNC shares (\\server\share\...).
    /// Uses native FileStream seek, meaning only the requested bytes travel across the SMB network share.
    /// </summary>
    public sealed class FileByteRangeSource : IByteRangeSource
    {
        private readonly string _filePath;
        private FileStream? _fileStream;
        private readonly object _lock = new();

        public FileByteRangeSource(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentNullException(nameof(filePath));

            if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri) &&
                (uri.IsFile || uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)))
            {
                _filePath = uri.LocalPath;
            }
            else
            {
                _filePath = filePath;
            }
        }

        private FileStream GetStream()
        {
            if (_fileStream == null)
            {
                lock (_lock)
                {
                    _fileStream ??= new FileStream(
                        _filePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 65536,
                        useAsync: true);
                }
            }
            return _fileStream;
        }

        public Task<long> GetContentLengthAsync(CancellationToken cancellationToken = default)
        {
            if (!File.Exists(_filePath))
            {
                throw new FileNotFoundException($"Source file not found at '{_filePath}'.");
            }
            return Task.FromResult(new FileInfo(_filePath).Length);
        }

        public async Task<byte[]> ReadRangeAsync(long offset, int length, CancellationToken cancellationToken = default)
        {
            if (length <= 0) return Array.Empty<byte>();

            var buffer = new byte[length];
            var stream = GetStream();

            lock (_lock)
            {
                stream.Seek(offset, SeekOrigin.Begin);
            }

            int totalRead = 0;
            while (totalRead < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await stream.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }

            if (totalRead < length)
            {
                Array.Resize(ref buffer, totalRead);
            }

            return buffer;
        }

        public async Task ReadRangeToStreamAsync(long offset, int length, Stream destination, CancellationToken cancellationToken = default)
        {
            var data = await ReadRangeAsync(offset, length, cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _fileStream?.Dispose();
                _fileStream = null;
            }
        }
    }
}
