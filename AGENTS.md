# AGENTS.md — ZSync.Net

Guidance and instructions for AI agents (Antigravity, Cursor, Copilot, Claude, etc.) working on or integrating with **ZSync.Net**.

---

## What is ZSync.Net?

**ZSync.Net** is a high-performance, client-driven delta synchronization library and CLI tool for .NET. It modernizes the algorithms from Colin Phipps' `zsync` and Hou Sisong's `hdiffpatch` (`libhsync`) with:
1. **Zero External Dependencies**: Pure .NET standard library implementation (`ZSync.Core` and `ZSync.Cli` require 0 third-party packages).
2. **xxHash64**: Replaced legacy MD4/MD5 with ultra-fast 64-bit xxHash (`FastXxHash64.cs`) for strong block identification.
3. **Multi-Transport Streaming**:
   - HTTP/HTTPS via standard RFC 7233 `Range: bytes=X-Y` requests.
   - Windows SMB / UNC shares (`\\server\share\...`) and local files (`file://`) via random-access `FileStream.Seek()`.
4. **Single-Pass GZip Generation**: Generates uncompressed `.zsync`, block-compressed `.gz`, and `.gz.zsync` files simultaneously in a single streaming pass over the input file to minimize disk I/O.
5. **Atomic In-Place Patching**: Reconstructs into a sibling `.zsnew.<id>` temporary file, validates checksums, atomically switches place with `File.Move(..., overwrite: true)`, and leaves zero leftover files.

---

## Agent Instructions & Rules

### Rule 1: Zero External Dependencies
- `ZSync.Core` and `ZSync.Cli` must remain **100% dependency-free**.
- Do not add external NuGet packages for hashing, compression, or networking. Use `System.IO`, `System.IO.Compression` (`DeflateStream`, `GZipStream`), `System.Net.Http`, and `System.Security.Cryptography`.

### Rule 2: Multi-Targeting Compatibility
- `ZSync.Core`, `ZSync.Cli`, and `ZSync.Tests` multi-target `net8.0` (LTS) and `net10.0`.
- Always ensure code builds and tests pass cleanly across both runtime targets:
  ```bash
  dotnet build ZSync.Net.slnx
  dotnet test ZSync.Net.slnx
  ```

### Rule 3: Preserving Atomic In-Place Safety
- Never truncate or overwrite destination files in-place during reconstruction.
- Always assemble into `.zsnew.<guid>`, verify whole-file SHA256 and xxHash64, and then perform an atomic move/replace.

---

## Common Agent Workflows

### How to Build the Solution
```bash
dotnet build ZSync.Net.slnx
```

### How to Run All Tests
```bash
dotnet test ZSync.Net.slnx
```

### How to Pack NuGet Packages & Global CLI Tool
```bash
dotnet pack ZSync.Net.slnx -c Release -o ./artifacts
```

### How to Generate Control & GZip Payload for Any Binary
```bash
dotnet run --project src/ZSync.Cli -- make <file> --block-size 4096 --out-dir <dist>
```

### How to Perform an Atomic In-Place Sync
```bash
dotnet run --project src/ZSync.Cli -- sync \
  --control https://updates.example.com/Quantum.Worker.dll.gz.zsync \
  --target /path/to/Quantum.Worker.dll
```
