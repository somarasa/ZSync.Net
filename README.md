# ZSync.Net

[![Tests Passing](https://img.shields.io/badge/tests-passing%20(100%25)-brightgreen.svg?logo=github-actions&logoColor=white)](https://github.com/somarasa/ZSync.Net/actions)
[![.NET 8.0 LTS](https://img.shields.io/badge/.NET-8.0%20LTS-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Linux%20%7C%20Windows-lightgrey.svg)]()
[![Transports](https://img.shields.io/badge/transports-HTTP%20Range%20%7C%20SMB%20%7C%20UNC%20%7C%20File-blue.svg)]()
[![Zero Dependencies](https://img.shields.io/badge/dependencies-0%20(Pure%20BCL)-brightgreen.svg)]()
[![AI-Assisted](https://img.shields.io/badge/Ported%20with-AI%20Assistance-blueviolet.svg)]()
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

High-Performance C# Delta Synchronization Library and CLI (Modernized Zsync + HDiffPatch with xxHash64 and Multi-Transport SMB / HTTP Range Requests).

**Zero external NuGet dependencies** — 100% pure .NET.

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
Once installed, execute commands globally via `zsync` (or `dotnet zsync`):
```bash
zsync make Quantum.Worker.dll
zsync sync --control https://updates.example.com/Quantum.Worker.dll.gz.zsync --target Quantum.Worker.dll
```

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

6. **Atomic In-Place File Patching**:
   - When updating an existing file in-place, `ZSync.Net` reads existing blocks from the target file, constructs the new build in a temporary sibling (`.zsnew.<id>`), verifies the whole-file checksum, and atomically switches places (`File.Move(..., overwrite: true)`).
   - Leftovers are automatically removed on success. If verification fails, the original active file is completely untouched.

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
[Target File: Quantum.Worker.dll (or service.exe / database.db)]
        │
        ▼ (ZSyncControlBuilder.GenerateAllAsync - Single I/O Pass)
 ┌──────────────────────────────┬──────────────────────────────┬──────────────────────────────┐
 │                              │                              │                              │
 ▼                              ▼                              ▼                              ▼
Quantum.Worker.dll.zsync       Quantum.Worker.dll.gz          Quantum.Worker.dll.gz.zsync
(Uncompressed control)         (Block-compressed payload)     (Hashes & compressed offsets)

──────────────────────────────────────────────────────────────────────────────────

[Client Synchronization Flow]
 1. Read Quantum.Worker.dll.gz.zsync control file
 2. Scan local existing Quantum.Worker.dll with sliding-window RollingHash + FastXxHash64
    ├── Block 0: MATCHED locally ──► Copy directly from local file
    ├── Block 1: MATCHED locally ──► Copy directly from local file
    ├── Block 2: MISSING ──────────► Fetch compressed range from HTTP / SMB share ──► Decompress on the fly
    └── Block 3: MATCHED locally ──► Copy directly from local file
 3. Output Quantum.Worker.dll reconstructed in staging
 4. Verify SHA256 and xxHash64 against control file
```

> **Note on File Types**: `ZSync.Net` operates on any file (regular binaries, service executables `.exe`, libraries `.dll`, database files `.db`, data files, or archives). A regular service binary (`Quantum.Worker.dll`) is used throughout the examples below.

---

## CLI Usage

### 1. Generate Control Files & GZip Payload (Single Pass)
```bash
# Example with a service binary / DLL
dotnet ZSync.Cli.dll make Quantum.Worker.dll --block-size 4096 --out-dir /dist
```
Generates at once:
- `/dist/Quantum.Worker.dll.zsync` (uncompressed control file)
- `/dist/Quantum.Worker.dll.gz` (block-compressed GZip payload)
- `/dist/Quantum.Worker.dll.gz.zsync` (compressed control file)

### 2. Synchronize from HTTP/HTTPS
```bash
dotnet ZSync.Cli.dll sync \
  --control https://updates.example.com/Quantum.Worker.dll.gz.zsync \
  --payload https://updates.example.com/Quantum.Worker.dll.gz \
  --source /opt/quantum/current/Quantum.Worker.dll \
  --target /opt/quantum/staging/Quantum.Worker.dll
```

### 3. Synchronize from SMB / UNC Network Share
```bash
dotnet ZSync.Cli.dll sync \
  --control \\fileserver\updates\Quantum.Worker.dll.gz.zsync \
  --payload \\fileserver\updates\Quantum.Worker.dll.gz \
  --source /opt/quantum/current/Quantum.Worker.dll \
  --target /opt/quantum/staging/Quantum.Worker.dll
```

### 4. Atomic In-Place Patching (Single `--target` Argument)
```bash
# When --source is omitted, ZSync automatically uses the existing target file as the base,
# streams delta chunks into an atomic sibling (.zsnew.xxxx), verifies checksum, switches places,
# and removes all temporary leftovers:
dotnet ZSync.Cli.dll sync \
  --control https://updates.example.com/Quantum.Worker.dll.gz.zsync \
  --target /opt/quantum/Quantum.Worker.dll
```

---

## C# API Usage

### Generating Control Info
```csharp
using ZSync.Core.Engine;

// Generate .zsync, .gz, and .gz.zsync at once with minimum disk I/O
var summary = await ZSyncControlBuilder.GenerateAllAsync(
    inputFilePath: "Quantum.Worker.dll",
    outputDirectory: "/dist",
    options: new ZSyncControlBuilderOptions { BlockSize = 4096 });
```

### Syncing / Reconstructing Files
```csharp
using ZSync.Core.Engine;
using ZSync.Core.Model;
using ZSync.Core.Transport;

// 1. Load control file (from HTTP URL, SMB path, or local file)
var control = await ZSyncControlFile.LoadAsync("https://updates.example.com/Quantum.Worker.dll.gz.zsync");

// 2. Open range source (automatically detects HTTP vs SMB vs File)
using var rangeSource = ByteRangeSourceFactory.Create("https://updates.example.com/Quantum.Worker.dll.gz");

// 3. Reconstruct target file using existing local build
var result = await ZSyncSyncEngine.SyncAsync(
    control: control,
    rangeSource: rangeSource,
    localSourceFile: "Quantum.Worker.dll",
    destinationPath: "Quantum.Worker.dll");

Console.WriteLine($"Bandwidth saved: {result.BandwidthSavingsPercentage:F1}%");
Console.WriteLine($"Checksum verified: {result.ChecksumVerified}");
```

---

## Building and Running Tests

```bash
# Build solution
dotnet build ZSync.Net.slnx

# Run test suite across all target frameworks (.NET 8 and .NET 10)
dotnet test ZSync.Net.slnx

# Pack NuGet packages and .NET Global Tool
dotnet pack ZSync.Net.slnx -c Release -o ./artifacts
```

---

## Acknowledgements & Prior Art

- **[Hou Sisong (sisong/hdiffpatch)](https://github.com/sisong/hdiffpatch)**: Pioneered high-performance binary diffing algorithms and the block-compressed GZip sync format (`hsynz`).
- **[Colin Phipps (zsync)](http://zsync.moria.org.uk/)**: Invented the original client-driven HTTP range partial download algorithm.
- **[Yann Collet (xxHash)](https://github.com/Cyan4973/xxHash)**: Creator of the ultra-fast xxHash algorithm.

### AI-Assisted Porting & Development
This project was conceptualized, ported, and verified with the assistance of advanced AI pair-programming, translating and modernizing the core concepts from C/C++ [HDiffPatch/libhsync](https://github.com/sisong/hdiffpatch) and [zsync](http://zsync.moria.org.uk/) into high-performance, idiomatic, zero-dependency C# for modern .NET (8.0 LTS & 10.0).

---

## License
MIT
