---
name: zsync-net
description: >-
  Workflow guide and runbook for ZSync.Net — high-performance C# delta synchronization
  over HTTP and SMB/UNC interoperable with HDiffPatch (HSyni23/HSynz23) and Colin Phipps' zsync.
---

# ZSync.Net Skill Guide

This skill provides operational workflows, CLI recipes, and C# integration patterns for **ZSync.Net**, a client-driven delta synchronization library and CLI tool providing 100% two-way interoperability with Hou Sisong's `HDiffPatch` (`HSyni23` / `.hsyni` and `HSynz23` / `.hsynz`) and Colin Phipps' `zsync` (`0.6.2`).

---

## When to Use ZSync.Net

- **Client-Driven Updates (Zero Server-Side Compute)**: Distributing updates from standard static HTTP servers (GitLab Generic Packages, Nginx, S3, MinIO) without running an active rsync daemon.
- **Large Binary / DLL / Data Synchronization**: Updating service binaries (`.dll`, `.exe`), database dumps (`.db`), or system images over low-bandwidth or remote WAN links.
- **Standards Compatibility**: Interoperating directly with Hou Sisong's `hsync_make` and `hsync_demo` or legacy `zsync` client ecosystems.
- **Zstandard Streaming Compression**: Modern `zstd` / `zstdD` compression with sliding dictionary lookback and magicless streaming frames.
- **Windows SMB / UNC File Shares**: Streaming delta blocks across network shares (`\\server\share\...`) using random-access seeks so only modified chunks travel across the network.
- **Atomic In-Place File Patching**: Updating an active binary in-place without risk of corruption if the network disconnects or process halts mid-update.

---

## CLI Recipes

### 1. Generating Control Files & Zstandard Payload (Single Pass)
To generate the `.hsyni` control file and `.hsynz` block-compressed payload in a single streaming pass:

```bash
# Using installed Global Tool
zsync make /path/to/Quantum.Worker.dll --block-size 4096 --compress zstd --out-dir /dist

# Or via dotnet CLI
XDG_RUNTIME_DIR=/tmp dotnet run --project src/ZSync.Cli -- make /path/to/Quantum.Worker.dll --block-size 4096 --compress zstd --out-dir /dist
```

**Options for `zsync make`:**
- `--block-size <bytes>`: Block size in bytes (default: 4096).
- `--compress <zstd|zlib>`: Compression type (default: `zstd`).
- `--checksum <xxh64|md5>`: Block strong checksum type (default: `xxh64`).
- `--out-dir <dir>`: Output destination directory.

**Generated Artifacts:**
- `/dist/Quantum.Worker.dll.hsyni` — HDiffPatch control file containing block index and hash lookup tables.
- `/dist/Quantum.Worker.dll.hsynz` — Block-compressed payload stream (Zstandard or Deflate).

### 2. In-Place Atomic Synchronization (Single Target)
When `--source` is omitted and the target file exists, ZSync automatically uses it as the base, constructs into `.zsnew.<guid>`, validates checksums, and atomically replaces the file:

```bash
# In-place atomic update over HTTP
zsync sync \
  --control https://updates.example.com/Quantum.Worker.dll.hsyni \
  --target /opt/quantum/Quantum.Worker.dll

# In-place atomic update over Windows SMB / UNC Share
zsync sync \
  --control \\fileserver\updates\Quantum.Worker.dll.hsyni \
  --target C:\Services\Quantum\Quantum.Worker.dll
```

### 3. Explicit Source and Staging Synchronization
To keep the old version intact and reconstruct the candidate build into a staging directory:

```bash
zsync sync \
  --control https://updates.example.com/Quantum.Worker.dll.hsyni \
  --payload https://updates.example.com/Quantum.Worker.dll.hsynz \
  --source /opt/quantum/current/Quantum.Worker.dll \
  --target /opt/quantum/staging/Quantum.Worker.dll
```

### 4. Legacy ZSync Mode
ZSync.Net automatically detects legacy Colin Phipps' `zsync` (`0.6.2`) control files (`.zsync`):

```bash
zsync sync \
  --control https://updates.example.com/app.iso.zsync \
  --target /opt/images/app.iso
```

---

## C# Integration Workflows

### 1. Generating Control Information in Build / CI Pipelines
```csharp
using ZSync.Core.Engine;

var summary = await ZSyncControlBuilder.GenerateAllAsync(
    inputFilePath: "Quantum.Worker.dll",
    outputDirectory: "/dist",
    options: new ZSyncControlBuilderOptions
    {
        BlockSize = 4096,
        CompressType = "zstd", // "zstd" or "zlib"
        ChecksumType = "xxh64" // "xxh64" or "md5"
    });

Console.WriteLine($"Uncompressed: {summary.UncompressedSize} bytes");
Console.WriteLine($"Payload:      {summary.CompressedSize} bytes ({summary.CompressionRatio:F1}%)");
Console.WriteLine($"Blocks:       {summary.BlockCount}");
Console.WriteLine($"xxHash64:     0x{summary.XxHash64:X16}");
```

### 2. Synchronizing and Reconstructing Files in Application Code
```csharp
using ZSync.Core.Engine;
using ZSync.Core.Model;
using ZSync.Core.Transport;

// 1. Load control file (supports HTTP URLs, SMB shares, or local paths for both .hsyni and .zsync)
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

## Verification & Testing

Always verify builds and run the test suite across both supported runtimes (.NET 8 and .NET 10):

```bash
# Build
XDG_RUNTIME_DIR=/tmp dotnet build ZSync.Net.slnx

# Run all unit and live native HDiffPatch interop tests
XDG_RUNTIME_DIR=/tmp dotnet test ZSync.Net.slnx

# Pack NuGet packages and global CLI tool
XDG_RUNTIME_DIR=/tmp dotnet pack ZSync.Net.slnx -c Release -o ./artifacts
```
