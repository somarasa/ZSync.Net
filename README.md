# ZSync.Net

[![Tests Passing](https://img.shields.io/badge/tests-41%20passing%20(100%25)-brightgreen.svg?logo=github-actions&logoColor=white)](https://github.com/somarasa/ZSync.Net/actions)
[![.NET 8.0 LTS](https://img.shields.io/badge/.NET-8.0%20LTS-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Linux%20%7C%20Windows-lightgrey.svg)]()
[![Transports](https://img.shields.io/badge/transports-HTTP%20Range%20%7C%20SMB%20%7C%20UNC%20%7C%20File-blue.svg)]()
[![Format Interop](https://img.shields.io/badge/format%20interop-HDiffPatch%20%7C%20ZSync%200.6.2-brightgreen.svg)]()
[![Compression](https://img.shields.io/badge/compression-Zstandard%20%7C%20Zlib-blue.svg)]()
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

High-Performance C# Delta Synchronization Library and CLI for .NET.
**100% Interoperable** with Hou Sisong's [HDiffPatch](https://github.com/sisong/hdiffpatch) (`HSyni23` / `.hsyni` and `HSynz23` / `.hsynz`) and Colin Phipps' [zsync](http://zsync.moria.org.uk/) (`0.6.2`), featuring Zstandard streaming compression, xxHash64, and multi-transport SMB / HTTP Range requests.

---

## Installation

### Add as Submodule / Project Reference
```bash
git submodule add https://github.com/somarasa/ZSync.Net.git external/ZSync.Net
```
Reference in your `.csproj`:
```xml
<ProjectReference Include="../external/ZSync.Net/src/ZSync.Core/ZSync.Core.csproj" />
```

### Global CLI Tool (Build from Source)
```bash
dotnet pack src/ZSync.Cli/ZSync.Cli.csproj -c Release -o ./artifacts
dotnet tool install --global --add-source ./artifacts ZSync.Net.Cli
```
Once installed, execute commands globally via `zsync`:
```bash
zsync make Quantum.Worker.dll --compress zstd
zsync sync --control https://updates.example.com/Quantum.Worker.dll.hsyni --target Quantum.Worker.dll
```

---

## Key Features

1. **100% Two-Way Standards Interoperability**:
   - Seamlessly generates and consumes HDiffPatch control files (`.hsyni`) and payloads (`.hsynz`).
   - Seamlessly synchronizes against legacy Colin Phipps' `zsync` (`0.6.2`) control files (`.zsync`).
   - Verified two-way against compiled native `hsync_make` and `hsync_demo` (v1.3.0).

2. **First-Class Zstandard & Zlib Support**:
   - Ultra-fast Zstandard block compression (`zstdD`) with sliding prefix dictionary lookback and magicless streaming frames.
   - Raw Deflate / Zlib (`zlibD`) streaming compression.

3. **Client-Driven Delta Sync (Zero Server Compute)**:
   - Does not require a server-side daemon or active compute process.
   - Works over any standard HTTP/HTTPS web server (Nginx, Apache, GitLab Generic Packages, S3, MinIO, CDN) using RFC 7233 `Range: bytes=start-end` requests.

4. **Native SMB / UNC & Local File Share Support**:
   - Random-access streaming over Windows SMB file shares (`\\server\share\...`) and local files (`file://`).
   - Translates to native SMB `READ` requests at specific offsets: **the multi-gigabyte payload stays on the share; only the missing chunks travel across the network wire**.

5. **True Single-Pass Streaming I/O & Constant RAM Footprint**:
   - **Generation**: Creates `.hsyni` and `.hsynz` in a single pass using streaming `FastXxHash64.State` with zero file buffering.
   - **Reconstruction**: Rebuilds target files in a single write pass, concurrently verifying `HDiffPatchCheckChecksum`, SHA-256, and xxHash64 on the fly with zero second disk pass.
   - **Matcher**: Scans multi-gigabyte seed files using a sliding chunk buffer with constant ~1 MB memory usage.

6. **Universal Checksums & Fast Rolling Hashes**:
   - First-class support for `xxh128` (XXH3 128-bit, native `hsync_make` default), `md5`, `sha256`, `sha512`, `crc32`, and `xxh64`.
   - 64-bit Adler rolling hash (`FastAdler64`) with 256-word table lookups for $O(1)$ block rolling.
   - Built-in MD5, MD4, and SHA-1 support for full standards compliance.

7. **Atomic In-Place File Patching**:
   - Safely updates target files in-place: reconstructs into `.zsnew.<guid>`, verifies checksums on the fly, and atomically switches places (`File.Move(..., overwrite: true)`).
   - Zero leftover files on success; destination remains completely intact if interrupted.

---

## Solution Structure

```
ZSync.Net/
├── src/
│   ├── ZSync.Core/
│   │   ├── Compression/
│   │   │   ├── IBlockCompressor.cs        # Block compressor interface (with dictionary lookback)
│   │   │   ├── ZstdBlockCompressor.cs     # Zstandard streaming magicless compressor / decompressor
│   │   │   └── DeflateBlockCompressor.cs  # Raw Deflate / Zlib compressor / decompressor
│   │   ├── Format/
│   │   │   ├── HDiffPatch/
│   │   │   │   ├── HSyniControlFile.cs    # HDiffPatch .hsyni reader/writer & .hsynz payload parser
│   │   │   │   └── PackUInt.cs            # 7-bit variable-length integer pack/unpack with tagging
│   │   │   └── ZSyncLegacy/
│   │   │       └── LegacyZSyncControlFile.cs # Colin Phipps' zsync 0.6.2 ASCII header & binary table parser
│   │   ├── Hashing/
│   │   │   ├── FastXxHash64.cs            # Pure C# 64-bit xxHash with incremental streaming State
│   │   │   ├── FastAdler64.cs             # 64-bit Adler rolling hash matching HDiffPatch fast_adler64
│   │   │   ├── HDiffPatchCheckChecksum.cs # XOR-accumulated check checksum matcher
│   │   │   ├── RollingHash.cs             # 32-bit Adler rolling hash for legacy zsync
│   │   │   └── Md4.cs                     # Managed MD4 implementation for legacy zsync
│   │   ├── Model/
│   │   │   ├── BlockChecksum.cs           # Block metadata (weak hash, strong hash, offsets)
│   │   │   ├── ByteRange.cs               # Range coalescing helper to minimize roundtrips
│   │   │   └── ZSyncControlFile.cs        # Format-agnostic loader & sniffer (.hsyni & .zsync)
│   │   ├── Transport/
│   │   │   ├── IByteRangeSource.cs        # Transport abstraction for random-access reading
│   │   │   ├── FileByteRangeSource.cs     # SMB / UNC (\\server\share\...) & local FileStream seeks
│   │   │   ├── HttpByteRangeSource.cs     # RFC 7233 HTTP Range requests (Range: bytes=X-Y)
│   │   │   └── ByteRangeSourceFactory.cs  # Automatic transport selector (http/https vs smb/file)
│   │   └── Engine/
│   │       ├── ZSyncControlBuilder.cs     # Single-pass streaming generator (.hsyni & .hsynz)
│   │       ├── ZSyncBlockMatcher.cs       # Streaming sliding-window matcher with constant memory
│   │       └── ZSyncSyncEngine.cs         # Single-pass atomic reconstructor & verifier
│   └── ZSync.Cli/                         # Standalone CLI tool (make & sync commands)
├── tests/
│   └── ZSync.Tests/                       # Comprehensive test suite (41/41 tests passing across .NET 8.0 & 10.0)
│       ├── FastXxHash64Tests.cs           # xxHash64 correctness & streaming state verification
│       ├── RollingHashTests.cs            # Adler rolling update equivalence tests
│       ├── TransportTests.cs              # SMB/File & HTTP Range transport tests
│       ├── ZSyncControlBuilderTests.cs    # Single-pass generator tests
│       ├── ZSyncSyncEngineTests.cs        # End-to-end delta sync & verification tests
│       └── HDiffPatchInteroperabilityTests.cs # Live 2-way interop with native hsync_make / hsync_demo
├── ZSync.Net.slnx                         # Solution file
└── README.md
```

---

## Architectural Flow

```
[Target File: Quantum.Worker.dll (or service.exe / database.db)]
        │
        ▼ (ZSyncControlBuilder.GenerateAllAsync - Single-Pass Streaming)
 ┌──────────────────────────────────────┬──────────────────────────────────────┐
 │                                      │                                      │
 ▼                                      ▼                                      ▼
Quantum.Worker.dll.hsyni               Quantum.Worker.dll.hsynz
(Bit-packed control file: Adler64/xxH) (Block-compressed streaming payload: zstd/zlib)

──────────────────────────────────────────────────────────────────────────────────

[Client Synchronization Flow]
 1. Read Quantum.Worker.dll.hsyni (or legacy .zsync)
 2. Scan local existing seed with streaming sliding window (Adler64 / xxHash64)
    ├── Block 0: MATCHED locally ──► Copy directly from local file
    ├── Block 1: MATCHED locally ──► Copy directly from local file
    ├── Block 2: MISSING ──────────► Fetch compressed range from HTTP / SMB share ──► Decompress on the fly
    └── Block 3: MATCHED locally ──► Copy directly from local file
 3. Concurrently update SHA-256, xxHash64, and HDiffPatchCheckChecksum during write
 4. Validate checksums and atomically move .zsnew.<guid> into place
```

---

## CLI Usage

### 1. Generate Control File & Zstandard Payload (Single Pass)
```bash
# Generate .hsyni and .hsynz with Zstandard compression (default)
zsync make Quantum.Worker.dll --block-size 4096 --compress zstd --out-dir /dist
```
Generates at once:
- `/dist/Quantum.Worker.dll.hsyni` (HDiffPatch control file)
- `/dist/Quantum.Worker.dll.hsynz` (Zstandard block-compressed payload)

### 2. Synchronize from HTTP/HTTPS
```bash
zsync sync \
  --control https://updates.example.com/Quantum.Worker.dll.hsyni \
  --payload https://updates.example.com/Quantum.Worker.dll.hsynz \
  --source /opt/quantum/current/Quantum.Worker.dll \
  --target /opt/quantum/staging/Quantum.Worker.dll
```

### 3. Synchronize from SMB / UNC Network Share
```bash
zsync sync \
  --control \\fileserver\updates\Quantum.Worker.dll.hsyni \
  --payload \\fileserver\updates\Quantum.Worker.dll.hsynz \
  --source /opt/quantum/current/Quantum.Worker.dll \
  --target /opt/quantum/staging/Quantum.Worker.dll
```

### 4. Atomic In-Place Patching (Single `--target` Argument)
```bash
# When --source is omitted, ZSync automatically uses the existing target file as the base,
# streams delta chunks into an atomic sibling (.zsnew.xxxx), verifies checksum, switches places,
# and removes all temporary leftovers:
zsync sync \
  --control https://updates.example.com/Quantum.Worker.dll.hsyni \
  --target /opt/quantum/Quantum.Worker.dll
```

---

## C# API Usage

### Generating Control Info
```csharp
using ZSync.Core.Engine;

// Generate .hsyni and .hsynz at once in a single streaming pass
var summary = await ZSyncControlBuilder.GenerateAllAsync(
    inputFilePath: "Quantum.Worker.dll",
    outputDirectory: "/dist",
    options: new ZSyncControlBuilderOptions 
    { 
        BlockSize = 4096,
        CompressType = "zstd", // "zstd" or "zlib"
        ChecksumType = "xxh64" // "xxh64" or "md5"
    });

Console.WriteLine($"Compressed: {summary.CompressedSize} bytes ({summary.CompressionRatio:F1}%)");
Console.WriteLine($"Blocks:     {summary.BlockCount}");
Console.WriteLine($"xxHash64:   0x{summary.XxHash64:X16}");
```

### Syncing / Reconstructing Files
```csharp
using ZSync.Core.Engine;
using ZSync.Core.Model;
using ZSync.Core.Transport;

// 1. Load control file (supports .hsyni or legacy .zsync from HTTP, SMB, or local path)
var control = await ZSyncControlFile.LoadAsync("https://updates.example.com/Quantum.Worker.dll.hsyni");

// 2. Open range source (automatically detects HTTP Range vs SMB FileStream seek)
using var rangeSource = ByteRangeSourceFactory.Create("https://updates.example.com/Quantum.Worker.dll.hsynz");

// 3. Reconstruct target file using existing local build
var result = await ZSyncSyncEngine.SyncAsync(
    control: control,
    rangeSource: rangeSource,
    destinationPath: "/opt/quantum/Quantum.Worker.dll",
    localSourceFile: "/opt/quantum/Quantum.Worker.dll");

Console.WriteLine($"Bandwidth saved: {result.BandwidthSavedRatio:F1}%");
Console.WriteLine($"SHA256:          {result.Sha256}");
Console.WriteLine($"xxHash64:        0x{result.XxHash64:X16}");
```

---

## Building and Running Tests

```bash
# Build solution across .NET 8.0 & .NET 10.0
XDG_RUNTIME_DIR=/tmp dotnet build ZSync.Net.slnx

# Run complete test suite (including live 2-way native HDiffPatch interop tests)
XDG_RUNTIME_DIR=/tmp dotnet test ZSync.Net.slnx

# Pack NuGet packages and .NET Global Tool
XDG_RUNTIME_DIR=/tmp dotnet pack ZSync.Net.slnx -c Release -o ./artifacts
```

---

## Acknowledgements & Prior Art

- **[Hou Sisong (sisong/hdiffpatch)](https://github.com/sisong/hdiffpatch)**: Pioneered high-performance binary diffing algorithms and the modern `HSyni23` / `HSynz23` delta sync specifications.
- **[Colin Phipps (zsync)](http://zsync.moria.org.uk/)**: Invented the original client-driven HTTP range partial download algorithm.
- **[Yann Collet (xxHash & Zstandard)](https://github.com/Cyan4973/xxHash)**: Creator of the ultra-fast xxHash and Zstandard algorithms.

---

## License
MIT
