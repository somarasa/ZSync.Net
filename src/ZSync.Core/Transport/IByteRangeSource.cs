using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ZSync.Core.Transport
{
    /// <summary>
    /// Represents an abstraction for reading random byte ranges from a local, SMB/UNC, or HTTP data source.
    /// </summary>
    public interface IByteRangeSource : IDisposable
    {
        /// <summary>
        /// Gets the total length of the remote or share file in bytes.
        /// </summary>
        Task<long> GetContentLengthAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads an exact byte range [offset, offset + length - 1].
        /// </summary>
        Task<byte[]> ReadRangeAsync(long offset, int length, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads an exact byte range directly into a destination stream.
        /// </summary>
        Task ReadRangeToStreamAsync(long offset, int length, Stream destination, CancellationToken cancellationToken = default);
    }
}
