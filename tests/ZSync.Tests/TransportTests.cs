using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ZSync.Core.Transport;

namespace ZSync.Tests
{
    [TestFixture]
    public class TransportTests
    {
        private string _tempDir = null!;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "transport_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
            }
        }

        [Test]
        public async Task FileByteRangeSource_ReadsExactRangeCorrectly()
        {
            var testFile = Path.Combine(_tempDir, "sample.bin");
            var testBytes = Encoding.UTF8.GetBytes("0123456789ABCDEF-GHIJKLMNOPQRSTUVWXYZ");
            await File.WriteAllBytesAsync(testFile, testBytes);

            using var source = new FileByteRangeSource(testFile);

            var len = await source.GetContentLengthAsync();
            Assert.That(len, Is.EqualTo(testBytes.Length));

            // Read range [10, 15] -> "ABCDEF"
            var chunk = await source.ReadRangeAsync(10, 6);
            Assert.That(Encoding.UTF8.GetString(chunk), Is.EqualTo("ABCDEF"));
        }

        [Test]
        public async Task HttpByteRangeSource_SendsRangeHeaderAndParsesResponse()
        {
            var fullData = Encoding.UTF8.GetBytes("Hello world from simulated HTTP range server!");

            var handler = new MockHttpMessageHandler(req =>
            {
                if (req.Method == HttpMethod.Head)
                {
                    var headResp = new HttpResponseMessage(HttpStatusCode.OK);
                    headResp.Content.Headers.ContentLength = fullData.Length;
                    return headResp;
                }

                Assert.That(req.Headers.Range, Is.Not.Null);
                var range = req.Headers.Range!.Ranges.GetEnumerator();
                range.MoveNext();
                var from = (int)range.Current.From!.Value;
                var to = (int)range.Current.To!.Value;
                int count = to - from + 1;

                var slice = new byte[count];
                Array.Copy(fullData, from, slice, 0, count);

                var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(slice)
                };
                resp.Content.Headers.ContentLength = count;
                return resp;
            });

            using var httpClient = new HttpClient(handler);
            using var httpSource = new HttpByteRangeSource("https://example.com/file.bin", httpClient);

            var len = await httpSource.GetContentLengthAsync();
            Assert.That(len, Is.EqualTo(fullData.Length));

            // Read range [6, 10] -> "world"
            var sliceBytes = await httpSource.ReadRangeAsync(6, 5);
            Assert.That(Encoding.UTF8.GetString(sliceBytes), Is.EqualTo("world"));
        }

        private sealed class MockHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
            public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(_handler(request));
        }
    }
}
