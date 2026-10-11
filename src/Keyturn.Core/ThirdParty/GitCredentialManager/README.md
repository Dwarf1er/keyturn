# Git Credential Manager (vendored interop)

Native-interop code adapted from [Git Credential Manager](https://github.com/git-ecosystem/git-credential-manager),
MIT License, Copyright GitHub, Inc. and contributors (see `LICENSE` here).

Source revision: `91d6da2f699d60802cdd5d339ba2b91b38dce549` (2026-10-09).

| Our file | Upstream file | Changes |
|---|---|---|
| `SecurityFramework.cs` | `src/Core/Interop/MacOS/Native/SecurityFramework.cs` | Only the `SecKeychain*` calls and result-code handling Keyturn uses; `CFRelease` from `CoreFoundation.cs`. |
| `Advapi32.cs` | `src/Core/Interop/Windows/Native/Advapi32.cs` (+ `Win32Error.cs`) | Only `CredRead/Write/Delete/Free`, the `Win32Credential` struct and its enums. |
| `InteropException.cs` | `src/Core/Interop/InteropException.cs` | Verbatim apart from namespace. |

The store logic in `../Platform/` is ours: GCM's Windows store matches git URLs (it throws for non-URL
service names), and its macOS store goes through CoreFoundation dictionaries where the `SecKeychain*`
calls below are enough. The declarations are unchanged so they can be diffed against upstream.
