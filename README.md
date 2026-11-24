# ZSync.Net

High-Performance C# Delta Synchronization Library and CLI (Modernized Zsync + HDiffPatch with xxHash64 and Multi-Transport SMB / HTTP Range Requests).

**Zero external NuGet dependencies** — 100% pure .NET.

---

## Key Features

1. **Client-Driven Delta Sync (Zero Server Compute)**:
   - Does not require a server-side daemon or active compute process.
   - Works over any standard HTTP/HTTPS web server (Nginx, Apache, GitLab Generic Packages, S3, MinIO, CDN) using standard RFC 7233 `Range: bytes=start-end` requests.

2. **Native SMB / UNC & Local File Share Support**:
   - Random-access streaming over Windows SMB file shares (`\\server\share\...`) and local files (`file://`).
   - Translates to native SMB `READ` requests at specific offsets: **the multi-gigabyte payload stays on the share; only the missing chunks travel across the network wire**.

3. **Modern xxHash64 (RAM Speed Hashing)**:
   - Replaces slow legacy MD4/MD5 hashes with **xxHash64** (>10 GB/s on modern CPUs) for collision-resistant block verification.
   - Combined with an $O(1)$ sliding-window Adler-32 rolling hash for fast offset-shifted block discovery.

4. **HDiffPatch Single-Pass GZip Generation (Minimized I/O)**:
   - Generates the uncompressed control file (`.zsync`), the block-compressed GZip payload (`.gz`), and the compressed control file (`.gz.zsync`) in a **single streaming pass over the input file**, cutting disk I/O to the absolute minimum.

5. **Doubly Optimized Bandwidth**:
   - Only missing blocks are downloaded.
   - When using the `.gz` payload, those missing blocks are downloaded in GZip-compressed format and decompressed on the fly into the target destination.

---

## Solution Structure

```
ZSync.Net/
├── src/
│   ├── ZSync.Core/
│   │   ├── Hashing/
│   │   │   ├── FastXxHash64.cs          # Pure C# 64-bit xxHash (>10 GB/s, 0 external deps)
│   │   │   └── RollingHash.cs           # O(1) sliding-window Adler-32 rolling hash
│   │   ├── Model/
│   │   │   ├── BlockChecksum.cs         # Block metadata (weak hash, xxHash64, compressed offsets)
│   │   │   ├── ByteRange.cs             # Range coalescing helper to minimize roundtrips
│   │   │   └── ZSyncControlFile.cs      # Binary & JSON control file reader/writer (.zsync)
│   │   ├── Transport/
│   │   │   ├── IByteRangeSource.cs      # Transport abstraction for random-access reading
│   │   │   ├── FileByteRangeSource.cs   # SMB / UNC (\\server\share\...) & local FileStream seeks
│   │   │   ├── HttpByteRangeSource.cs   # RFC 7233 HTTP Range requests (Range: bytes=X-Y)
│   │   │   └── ByteRangeSourceFactory.cs# Automatic transport selector (http/https vs smb/file)
│   │   └── Engine/
│   │       ├── ZSyncControlBuilder.cs   # Single-pass I/O generator (.zsync + .gz + .gz.zsync)
│   │       ├── ZSyncBlockMatcher.cs     # Sliding-window matcher scanning local base build
│   │       └── ZSyncSyncEngine.cs       # Multi-transport delta reconstructor & verifier
│   └── ZSync.Cli/                       # Standalone CLI tool (make & sync commands)
├── tests/
│   └── ZSync.Tests/                     # Comprehensive test suite (10/10 tests passing)
│       ├── FastXxHash64Tests.cs         # Hashing correctness & multi-block slicing
│       ├── RollingHashTests.cs          # O(1) rolling update equivalence proof
│       ├── TransportTests.cs            # SMB/File & HTTP Range mock transport tests
│       ├── ZSyncControlBuilderTests.cs  # Single-pass all-in-one generator tests
│       └── ZSyncSyncEngineTests.cs      # End-to-end delta sync & verification tests
├── ZSync.Net.slnx                       # Solution file
└── README.md
```

### Detailed Component Responsibilities

| Namespace / Directory | Class | Purpose |
|---|---|---|
| `ZSync.Core.Hashing` | `FastXxHash64` | Pure C# implementation of Yann Collet's 64-bit xxHash algorithm. Used for 100% collision-resistant strong block identification and whole-file integrity verification. |
| `ZSync.Core.Hashing` | `RollingHash` | $O(1)$ Adler-32 sliding-window checksum. Computes rolling hashes byte-by-byte to detect matching blocks even when byte offsets shift due to code insertions or deletions. |
| `ZSync.Core.Transport` | `IByteRangeSource` | Unified interface for reading random byte ranges `[offset, offset + length - 1]`. |
| `ZSync.Core.Transport` | `FileByteRangeSource` | Reads byte ranges from local paths, mounted shares, and Windows SMB / UNC shares (`\\server\share\...`) using `FileStream.Seek()`. Leaves unneeded gigabytes on the remote server. |
| `ZSync.Core.Transport` | `HttpByteRangeSource` | Reads byte ranges over HTTP / HTTPS using standard HTTP `Range:` headers. |
| `ZSync.Core.Transport` | `ByteRangeSourceFactory` | Automatically inspects URI / path scheme (`http://`, `https://`, `\\`, `file://`) and instantiates the correct source. |
| `ZSync.Core.Model` | `BlockChecksum` | Holds block index, uncompressed offset/length, weak hash, xxHash64, and compressed offset/length in `.gz`. |
| `ZSync.Core.Model` | `ZSyncControlFile` | Manages reading and writing `.zsync` control files with magic header `ZSYNC_NET_V1`. |
| `ZSync.Core.Engine` | `ZSyncControlBuilder` | Generates uncompressed `.zsync`, block-compressed `.gz`, and `.gz.zsync` files **in a single streaming pass**, eliminating repeated disk reads. |
| `ZSync.Core.Engine` | `ZSyncBlockMatcher` | Scans existing local files using sliding-window hash matching to build a reuse map. |
| `ZSync.Core.Engine` | `ZSyncSyncEngine` | Assembles the target file: copies matched blocks locally, streams missing ranges over HTTP or SMB, decompresses on the fly if GZip mode is active, and verifies whole-file SHA256 and xxHash64. |

---

## Architectural Flow

```
[Target File: app-v1.0.1.zip]
        │
        ▼ (ZSyncControlBuilder.GenerateAllAsync - Single I/O Pass)
 ┌──────────────────────┬──────────────────────┬──────────────────────┐
 │                      │                      │                      │
 ▼                      ▼                      ▼                      ▼
app-v1.0.1.zip.zsync   app-v1.0.1.zip.gz      app-v1.0.1.zip.gz.zsync (Hashes & offsets)
(Uncompressed control) (Block-compressed payload)

──────────────────────────────────────────────────────────────────────────────────

[Client Synchronization Flow]
 1. Read app-v1.0.1.zip.gz.zsync control file
 2. Scan local existing app-v1.0.0.zip with sliding-window RollingHash + FastXxHash64
    ├── Block 0: MATCHED locally ──► Copy from local file
    ├── Block 1: MATCHED locally ──► Copy from local file
    ├── Block 2: MISSING ──────────► Fetch compressed range from HTTP / SMB share ──► Decompress on the fly
    └── Block 3: MATCHED locally ──► Copy from local file
 3. Output app-v1.0.1.zip reconstructed in staging
 4. Verify SHA256 and xxHash64 against control file
```

---

## CLI Usage

### 1. Generate Control Files & GZip Payload (Single Pass)
```bash
dotnet ZSync.Cli.dll make app-v1.0.1.zip --block-size 4096 --out-dir /dist
```
Generates at once:
- `/dist/app-v1.0.1.zip.zsync` (uncompressed control file)
- `/dist/app-v1.0.1.zip.gz` (block-compressed GZip payload)
- `/dist/app-v1.0.1.zip.gz.zsync` (compressed control file)

### 2. Synchronize from HTTP/HTTPS
```bash
dotnet ZSync.Cli.dll sync \
  --control https://updates.example.com/app-v1.0.1.zip.gz.zsync \
  --payload https://updates.example.com/app-v1.0.1.zip.gz \
  --source /opt/myapp/current/app-v1.0.0.zip \
  --target /opt/myapp/staging/app-v1.0.1.zip
```

### 3. Synchronize from SMB / UNC Network Share
```bash
dotnet ZSync.Cli.dll sync \
  --control \\fileserver\updates\app-v1.0.1.zip.gz.zsync \
  --payload \\fileserver\updates\app-v1.0.1.zip.gz \
  --source /opt/myapp/current/app-v1.0.0.zip \
  --target /opt/myapp/staging/app-v1.0.1.zip
```

---

## C# API Usage

### Generating Control Info
```csharp
using ZSync.Core.Engine;

// Generate .zsync, .gz, and .gz.zsync at once with minimum disk I/O
var summary = await ZSyncControlBuilder.GenerateAllAsync(
    inputFilePath: "app-1.0.1.zip",
    outputDirectory: "/dist",
    options: new ZSyncControlBuilderOptions { BlockSize = 4096 });
```

### Syncing / Reconstructing Files
```csharp
using ZSync.Core.Engine;
using ZSync.Core.Model;
using ZSync.Core.Transport;

// 1. Load control file (from HTTP URL, SMB path, or local file)
var control = await ZSyncControlFile.LoadAsync("https://updates.example.com/app-1.0.1.zip.gz.zsync");

// 2. Open range source (automatically detects HTTP vs SMB vs File)
using var rangeSource = ByteRangeSourceFactory.Create("https://updates.example.com/app-1.0.1.zip.gz");

// 3. Reconstruct target file using existing local build
var result = await ZSyncSyncEngine.SyncAsync(
    control: control,
    rangeSource: rangeSource,
    localSourceFile: "app-1.0.0.zip",
    destinationPath: "app-1.0.1.zip");

Console.WriteLine($"Bandwidth saved: {result.BandwidthSavingsPercentage:F1}%");
Console.WriteLine($"Checksum verified: {result.ChecksumVerified}");
```

---

## Building and Running Tests

```bash
# Build solution
dotnet build ZSync.Net.slnx

# Run test suite
dotnet test ZSync.Net.slnx
```

---

## License
MIT
