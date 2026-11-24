---
name: zsync-net
description: >-
  Workflow guide and runbook for ZSync.Net — high-performance C# delta synchronization
  over HTTP and SMB/UNC using xxHash64, atomic in-place file patching, and single-pass GZip generation.
---

# ZSync.Net Skill Guide

This skill provides operational workflows, CLI recipes, and C# integration patterns for **ZSync.Net**, a client-driven delta synchronization library and CLI tool inspired by `zsync` and `hdiffpatch`.

---

## When to Use ZSync.Net

- **Client-Driven Updates (Zero Server-Side Compute)**: Distributing updates from standard static HTTP servers (GitLab Generic Packages, Nginx, S3, MinIO) without running an active rsync daemon.
- **Large Binary / DLL / Data Synchronization**: Updating service binaries (`.dll`, `.exe`), database dumps (`.db`), or system images over low-bandwidth or remote WAN links.
- **Windows SMB / UNC File Shares**: Streaming delta blocks across network shares (`\\server\share\...`) using random-access seeks so only modified chunks travel across the network.
- **Atomic In-Place File Patching**: Updating an active binary in-place without risk of corruption if the network disconnects or process halts mid-update.

---

## CLI Recipes

### 1. Generating Control Files & GZip Payload (Single Pass)
To generate the uncompressed control file, block-compressed `.gz` payload, and compressed control file at once with minimal disk I/O:

```bash
# Using installed Global Tool
zsync make /path/to/Quantum.Worker.dll --block-size 4096 --out-dir /dist

# Or via dotnet CLI
dotnet ZSync.Cli.dll make /path/to/Quantum.Worker.dll --block-size 4096 --out-dir /dist
```

**Generated Artifacts:**
- `/dist/Quantum.Worker.dll.zsync` — Control file for raw uncompressed downloads.
- `/dist/Quantum.Worker.dll.gz` — Block-compressed GZip payload for compressed downloads.
- `/dist/Quantum.Worker.dll.gz.zsync` — Control file mapping uncompressed block hashes to compressed byte ranges.

### 2. In-Place Atomic Synchronization (Single Target)
When `--source` is omitted and the target file exists, ZSync automatically uses it as the base, constructs into `.zsnew.xxxx`, validates checksums, and atomically replaces the file:

```bash
# In-place atomic update over HTTP
zsync sync \
  --control https://updates.example.com/Quantum.Worker.dll.gz.zsync \
  --target /opt/quantum/Quantum.Worker.dll

# In-place atomic update over Windows SMB / UNC Share
zsync sync \
  --control \\fileserver\updates\Quantum.Worker.dll.gz.zsync \
  --target C:\Services\Quantum\Quantum.Worker.dll
```

### 3. Explicit Source and Staging Synchronization
To keep the old version intact and reconstruct the candidate build into a staging directory:

```bash
zsync sync \
  --control https://updates.example.com/Quantum.Worker.dll.gz.zsync \
  --payload https://updates.example.com/Quantum.Worker.dll.gz \
  --source /opt/quantum/current/Quantum.Worker.dll \
  --target /opt/quantum/staging/Quantum.Worker.dll
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
        BlockSize = 4096, // 2048 - 8192 bytes recommended
        CompressionLevel = System.IO.Compression.CompressionLevel.Optimal
    });

Console.WriteLine($"Uncompressed: {summary.UncompressedSize} bytes");
Console.WriteLine($"GZip Payload: {summary.GzipSize} bytes ({summary.CompressionRatio:F1}%)");
Console.WriteLine($"SHA256:       {summary.Sha256}");
Console.WriteLine($"xxHash64:     0x{summary.XxHash64:X16}");
```

### 2. Synchronizing and Reconstructing Files in Application Code
```csharp
using ZSync.Core.Engine;
using ZSync.Core.Model;
using ZSync.Core.Transport;

// 1. Load control file (supports HTTP URLs, SMB shares, or local paths)
var control = await ZSyncControlFile.LoadAsync("https://updates.example.com/Quantum.Worker.dll.gz.zsync");

// 2. Open range source (automatically detects HTTP Range vs SMB FileStream seek)
using var rangeSource = ByteRangeSourceFactory.Create("https://updates.example.com/Quantum.Worker.dll.gz");

// 3. Reconstruct target file atomically
var progress = new Progress<double>(pct => Console.WriteLine($"Sync progress: {pct:F0}%"));

var result = await ZSyncSyncEngine.SyncAsync(
    control: control,
    rangeSource: rangeSource,
    localSourceFile: null, // Null indicates atomic in-place update of destinationPath
    destinationPath: "/opt/quantum/Quantum.Worker.dll",
    progress: progress);

if (result.ChecksumVerified)
{
    Console.WriteLine($"Updated successfully! Saved {result.BandwidthSavingsPercentage:F1}% bandwidth.");
}
else
{
    throw new InvalidOperationException("ZSync verification failed; original file remains untouched.");
}
```

---

## Block Size Selection Guidelines

| File Size | Recommended Block Size | Control File Size | Notes |
|---|---|---|---|
| **< 10 MB** | `2048` bytes (2 KB) | ~100–150 KB | Granular matching for small binaries and micro-patches. |
| **10 MB – 100 MB** | `4096` bytes (4 KB) | ~70–500 KB | **Default & sweet spot** for server DLLs, executables, and DB files. |
| **100 MB – 1 GB** | `8192` – `16384` bytes | ~300 KB – 1.5 MB | Optimal for large binary distributions and data files. |
| **> 1 GB** | `32768` – `65536` bytes | ~600 KB – 1.2 MB | Keeps control file small for multi-gigabyte ISOs or system images. |
