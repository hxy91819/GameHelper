# Encoding

All repository source, documentation, and configuration text files must be valid UTF-8. This protects Chinese user-facing text from being rewritten through Windows default ANSI code pages.

## Enforcement Layers

- `.editorconfig` declares `charset = utf-8` for editor integrations.
- `.gitattributes` declares `working-tree-encoding=UTF-8` for tracked text formats.
- `RepositoryEncodingTests` verifies active repository text files decode as UTF-8.

## Command-Line Rules

- Prefer `apply_patch` for automated edits.
- In PowerShell, never rely on the process default encoding for text rewrites; use an explicit UTF-8 writer such as `Set-Content -Encoding utf8`.
- If console output looks like mojibake, verify file bytes with a strict UTF-8 decoder before rewriting the file.
