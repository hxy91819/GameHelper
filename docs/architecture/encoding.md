# Encoding

All repository source, documentation, and configuration text files must be valid UTF-8. This protects Chinese user-facing text from being rewritten through Windows default ANSI code pages.

## Enforcement Layers

- `.editorconfig` declares `charset = utf-8` for editor integrations.
- `.gitattributes` declares `working-tree-encoding=UTF-8` for tracked text formats.
- `RepositoryEncodingTests` verifies active repository text files decode as UTF-8 and do not contain common UTF-8/GBK mojibake markers.

## Command-Line Rules

- Prefer `apply_patch` for automated edits.
- In Windows PowerShell 5.1, never rely on default file encoding for reads or writes. Use `Get-Content -Encoding utf8`, `Set-Content -Encoding utf8`, and `Select-String -Encoding utf8`; otherwise UTF-8 files without a BOM can be decoded through the system ANSI code page and rewritten as mojibake.
- If console output looks like mojibake, verify file bytes with a strict UTF-8 decoder before rewriting the file.
