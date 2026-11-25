# AGENTS.md — ZSync.Net

Guidance and instructions for AI agents (Antigravity, Cursor, Copilot, Claude, etc.) working on or integrating with **ZSync.Net**.

---

## What is ZSync.Net?

**ZSync.Net** is a high-performance, client-driven delta synchronization library and CLI tool for .NET. It provides **100% two-way interoperability** with Hou Sisong's `HDiffPatch` (`HSyni23` / `.hsyni` and `HSynz23` / `.hsynz`) and Colin Phipps' `zsync` (`0.6.2` / `.zsync`) with:
1. **Standards Compatibility**: Full compatibility with Hou Sisong's `hsync_make` / `hsync_demo` and Colin Phipps' `zsync` (no proprietary formats).
2. **High-Performance Compression**: First-class Zstandard (`zstd` / `zstdD`) support via `ZstdSharp.Port` with streaming magicless frames and sliding prefix dictionary lookback, alongside Deflate/Zlib (`zlib` / `zlibD`).
3. **xxHash64 & Adler64**: 64-bit xxHash (`FastXxHash64.cs`) for strong block identification and 64-bit Adler rolling hash (`FastAdler64.cs`), plus MD5, MD4, and SHA-1 where required by standards.
4. **Multi-Transport Streaming**:
   - HTTP/HTTPS via standard RFC 7233 `Range: bytes=X-Y` requests.
   - Windows SMB / UNC shares (`\\server\share\...`) and local files (`file://`) via random-access `FileStream.Seek()`.
5. **True Single-Pass Streaming I/O**:
   - Single-pass `.hsyni` and `.hsynz` generation with constant $O(1)$ RAM overhead using `FastXxHash64.State`.
   - Single-pass reconstruction: computes order-independent check checksums (`HDiffPatchCheckChecksum`), SHA-256, and xxHash64 on the fly during assembly with zero second disk pass.
   - Sliding-chunk local block matcher scanning multi-gigabyte seed files with constant ~1 MB memory footprint.
6. **Atomic In-Place Patching**: Reconstructs into a sibling `.zsnew.<guid>` temporary file, validates checksums, atomically switches place with `File.Move(..., overwrite: true)`, and leaves zero leftover files.

---

## Agent Instructions & Rules

### Rule 1: Standards Interoperability First
- All control files and payload formats must remain 100% compatible with HDiffPatch (`HSyni23` / `HSynz23`) and legacy `zsync` (0.6.2).
- Do not introduce custom proprietary headers or formats.
- Verified two-way against native `hsync_make` and `hsync_demo` binaries.

### Rule 2: Multi-Targeting Compatibility
- `ZSync.Core`, `ZSync.Cli`, and `ZSync.Tests` multi-target `net8.0` (LTS) and `net10.0`.
- Always ensure code builds and tests pass cleanly across both runtime targets:
  ```bash
  XDG_RUNTIME_DIR=/tmp dotnet build ZSync.Net.slnx
  XDG_RUNTIME_DIR=/tmp dotnet test ZSync.Net.slnx
  ```

### Rule 3: Single-Pass Streaming & Constant Memory
- Never buffer entire files into memory (`File.ReadAllBytes`, unbounded `MemoryStream`).
- Use `FastXxHash64.State` and `IncrementalHash` for streaming hashing.
- Keep local block matching bounded with streaming chunk buffers.

### Rule 4: Preserving Atomic In-Place Safety
- Never truncate or overwrite destination files in-place during reconstruction.
- Always assemble into `.zsnew.<guid>`, verify checksums on the fly, and then perform an atomic move/replace.

---

## Common Agent Workflows

### How to Build the Solution
```bash
XDG_RUNTIME_DIR=/tmp dotnet build ZSync.Net.slnx
```

### How to Run All Tests
```bash
XDG_RUNTIME_DIR=/tmp dotnet test ZSync.Net.slnx
```

### How to Pack NuGet Packages & Global CLI Tool
```bash
XDG_RUNTIME_DIR=/tmp dotnet pack ZSync.Net.slnx -c Release -o ./artifacts
```

### How to Generate Control & Payload for Any Binary
```bash
XDG_RUNTIME_DIR=/tmp dotnet run --project src/ZSync.Cli -- make <file> --block-size 4096 --compress zstd --out-dir <dist>
```

### How to Perform an Atomic In-Place Sync
```bash
XDG_RUNTIME_DIR=/tmp dotnet run --project src/ZSync.Cli -- sync \
  --control https://updates.example.com/Quantum.Worker.dll.hsyni \
  --target /path/to/Quantum.Worker.dll
```
