# ZSync.Net

High-Performance C# Delta Synchronization Library and CLI (Modernized Zsync + HDiffPatch with xxHash64 and Multi-Transport SMB / HTTP Range Requests).

**Zero external NuGet dependencies** — 100% pure .NET.

---

## Key Features

1. **Client-Driven Delta Sync (Zero Server Compute)**:
   - Does not require a server-side daemon or active compute process.
   - Works over any standard HTTP/HTTPS web server (Nginx, Apache, GitLab Generic Packages, S3, MinIO, CDN) using standard `Range: bytes=start-end` requests.

2. **Native SMB / UNC & Local File Share Support**:
   - Random-access streaming over Windows SMB file shares (`\\server\share\...`) and local files (`file://`).
   - Translates to native SMB `READ` requests at specific offsets: **the multi-gigabyte payload stays on the share; only the missing chunks travel across the network wire**.

3. **Modern xxHash64 (RAM Speed Hashing)**:
   - Replaces slow legacy MD4/MD5 hashes with **xxHash64** (>10 GB/s on modern CPUs) for collision-resistant block verification.
   - Combined with an O(1) sliding-window Adler-32 rolling hash.

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
│   │   │   ├── FastXxHash64.cs          # Ultra-fast 64-bit xxHash (0 external dependencies)
│   │   │   └── RollingHash.cs           # O(1) sliding-window rolling hash
│   │   ├── Model/
│   │   │   ├── BlockChecksum.cs         # Block metadata (weak hash, xxHash64, compressed offsets)
│   │   │   ├── ByteRange.cs             # Range coalescing helper
│   │   │   └── ZSyncControlFile.cs      # Binary & JSON control file reader/writer
│   │   ├── Transport/
│   │   │   ├── IByteRangeSource.cs      # Transport abstraction
│   │   │   ├── FileByteRangeSource.cs   # SMB / UNC / local FileStream random-access
│   │   │   ├── HttpByteRangeSource.cs   # RFC 7233 HTTP Range requests
│   │   │   └── ByteRangeSourceFactory.cs# Automatic transport selector
│   │   └── Engine/
│   │       ├── ZSyncControlBuilder.cs   # Single-pass I/O generator (.zsync + .gz + .gz.zsync)
│   │       ├── ZSyncBlockMatcher.cs     # Sliding-window matcher for local cache
│   │       └── ZSyncSyncEngine.cs       # Multi-transport delta reconstructor
│   └── ZSync.Cli/                       # Standalone CLI tool
└── tests/
    └── ZSync.Tests/                     # Comprehensive test suite
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
