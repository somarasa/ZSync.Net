# Copilot Instructions — ZSync.Net

## Overview
ZSync.Net is a high-performance C# library and CLI tool for client-driven binary delta synchronization over HTTP and SMB file shares, using xxHash64 and block-level GZip.

## Development Rules
1. **Standards Interoperability**: Maintain 100% two-way interoperability with HDiffPatch (HSyni23/HSynz23) and Colin Phipps' zsync (0.6.2).
2. **Multi-Targeting**: Always maintain compatibility with both `net8.0` (LTS) and `net10.0`.
3. **Atomic File Safety**: Updates must reconstruct into a temporary sibling file (`.zsnew.<id>`), verify checksums, and atomically replace the target file with `File.Move(..., overwrite: true)`.
4. **Transport Support**: Preserve support for both `HttpByteRangeSource` (RFC 7233 range requests) and `FileByteRangeSource` (SMB/UNC and local FileStream seeks).
