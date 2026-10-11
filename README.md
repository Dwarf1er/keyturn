<div align="center">

# Keyturn
##### Rotating secrets in your OS credential store

<img alt="Keyturn logo" height="280" src="assets/keyturn-mark.svg" />

![License](https://img.shields.io/github/license/Dwarf1er/keyturn?style=for-the-badge)
![Issues](https://img.shields.io/github/issues/Dwarf1er/keyturn?style=for-the-badge)
![PRs](https://img.shields.io/github/issues-pr/Dwarf1er/keyturn?style=for-the-badge)
![Stars](https://img.shields.io/github/stars/Dwarf1er/keyturn?style=for-the-badge)
![Release](https://img.shields.io/github/v/release/Dwarf1er/keyturn?style=for-the-badge)

</div>

Keyturn keeps rotating secrets, like a VPN password that changes every morning, in your OS credential store
instead of a `.json` file, and hands them to your other dev tools through one small CLI. When a secret comes due,
a desktop prompt asks you for the new value. The rest of the time it stays out of the way.

Every secret has its own schedule. Your tools never need to know how Keyturn stores anything: they run through
`keyturn run`, or call `keyturn get` the way they would call any credential helper. Values live only in the OS
credential store (macOS Keychain, Windows Credential Manager, Linux Secret Service). `secrets.json` in your config
dir holds names, schedules and last-set times, never values.

## Table of Contents

<!-- mtoc-start -->

* [What's included](#whats-included)
* [Installation](#installation)
  * [From source](#from-source)
* [Quickstart](#quickstart)
* [Schedules](#schedules)
* [Using secrets from your tools](#using-secrets-from-your-tools)
  * [Let keyturn inject the secret](#let-keyturn-inject-the-secret)
  * [The credential-helper pattern](#the-credential-helper-pattern)
  * [The CLI contract](#the-cli-contract)
  * [What keyturn does not do](#what-keyturn-does-not-do)
* [Scheduled prompt](#scheduled-prompt)
* [Under the hood](#under-the-hood)
  * [Where secrets live](#where-secrets-live)
  * [Platform notes](#platform-notes)
* [Building from source](#building-from-source)
  * [Releasing](#releasing)
* [Acknowledgements](#acknowledgements)
* [License](#license)

<!-- mtoc-end -->

## What's included

| | |
| --- | --- |
| **`keyturn`** | The CLI and the supported way for other tools to get a secret. A ~3 MB NativeAOT binary that starts in milliseconds. |
| **`Keyturn.App`** | The prompt window for secrets that are due, built with Avalonia. `--manage` opens a window to add, edit and remove secrets. |
| **`Keyturn.Core`** | The library behind both. |

## Installation

Download the archive for your platform from the [Releases](../../releases) page and unpack it somewhere on your
`PATH`. Keep `keyturn` and `Keyturn.App` in the same folder: `keyturn` finds the app beside itself.

| Platform | Archive |
| --- | --- |
| Linux x64 / arm64 | `keyturn-VERSION-linux-x64.tar.gz`, `keyturn-VERSION-linux-arm64.tar.gz` |
| macOS Apple silicon / Intel | `keyturn-VERSION-osx-arm64.tar.gz`, `keyturn-VERSION-osx-x64.tar.gz` |
| Windows x64 | `keyturn-VERSION-win-x64.zip` |

Verify with `sha256sum -c SHA256SUMS`. Each archive holds two single files and nothing else to install:

* **`keyturn`** (CLI): a ~3 MB NativeAOT binary that starts in milliseconds, since other tools call it.
* **`Keyturn.App`**: a ~20 MB self-contained file with its Skia and HarfBuzz native libraries embedded. No .NET
  install needed. On macOS it is `Keyturn.app`, a bundle with the Keyturn icon, signed ad hoc. On first launch .NET unpacks those libraries into a per-user folder (`~/.net/Keyturn.App/` on
  Linux and macOS, `%TEMP%\.net\Keyturn.App\` on Windows), which is safe to delete. Launching takes roughly
  150 ms longer than the CLI; a scheduled launch with nothing due still exits without opening a window.

A few things to know:

* **The binaries are not code-signed**, and on macOS the app is only signed ad hoc, not notarized (that needs a paid
  Apple Developer ID). Gatekeeper will quarantine what you download: after unpacking, run
  `xattr -dr com.apple.quarantine .` in the folder. Windows SmartScreen will warn too.
* **macOS:** needs macOS 12 or later (the binaries declare 12 as their minimum; Microsoft's support policy for
  .NET 10 covers macOS 15 and later). `Keyturn.app` is a normal Dock app: it shows in the Dock and Cmd-Tab while a
  prompt or the manage window is open, and the hourly check that finds nothing due exits before any window exists,
  so it never shows up. The first time the app reads a secret the CLI stored (or the other way round), macOS asks
  whether to allow keychain access; choose Always Allow. The ad-hoc signature changes with every build, so expect to
  be asked again after an upgrade.
* **Linux:** the app needs a graphical session and the usual desktop libraries (fontconfig, freetype). The
  binaries link against the glibc of the machine that built them; each release's build summary reports the
  minimum version for both files (the app's runtime baseline is much older than the CLI's NativeAOT build). The
  CLI works headless apart from `--refresh`.

### From source

Prefer to build it yourself? Clone the repo and run the installer (needs the [.NET 10 SDK](https://dotnet.microsoft.com/download)).

On Linux and macOS:

```bash
git clone https://github.com/Dwarf1er/keyturn.git
cd keyturn
./install.sh
```

On Windows, from PowerShell:

```powershell
git clone https://github.com/Dwarf1er/keyturn.git
cd keyturn
.\install.ps1
```

Both build the CLI and the app and install them for the current user without administrator rights: the binaries go to
`~/.local/bin` (`%LOCALAPPDATA%\Programs\Keyturn` on Windows). Each also adds a launcher with the Keyturn icon that
opens the manage window: an application-menu entry on Linux, `Keyturn.app` in `~/Applications` on macOS (find it with
Spotlight or Launchpad), a Start Menu shortcut on Windows. On Windows the icon is also embedded in both executables.

| `install.sh` | `install.ps1` | |
| --- | --- | --- |
| `--prefix DIR` | `-Prefix DIR` | Where the binaries go. |
| `--schedule` | `-Schedule` | Also run `keyturn schedule install`. |
| `--no-launcher` | `-NoShortcut` | Skip the menu entry or shortcut (macOS: keep `Keyturn.app` beside the CLI). |
| `--app-dir DIR` | | macOS only: where `Keyturn.app` goes (default `~/Applications`). |
| `--jit` | `-Jit` | Build the CLI without NativeAOT (see below). |
| `--uninstall` | `-Uninstall` | Remove what the installer put there. Your secrets and config are never touched. |

The CLI is built with NativeAOT, which needs a native toolchain: `clang`, the zlib headers and `binutils` on Linux,
the Xcode command line tools (`xcode-select --install`) on macOS, or the "Desktop development with C++" workload of
the Visual Studio Build Tools on Windows. If it's missing, the
installer says what to install and falls back to a self-contained CLI instead of failing: about 15 MB and 70 ms to
start, against 3 MB and 3 ms. It's otherwise identical.

The release archives are bare binaries, so on Linux the application-menu entry comes from `install.sh`.

## Quickstart

**1. Define a secret** and when it goes stale:

```bash
keyturn add vpn daily@06:00 "Corporate VPN password"
```

**2. Give it a value.** Either run `keyturn set vpn` (hidden prompt, or pipe the value in on stdin), or let the
prompt window ask you.

**3. Have the prompt appear when a secret comes due:**

```bash
keyturn schedule install
```

**4. Use it from a tool**, without the tool knowing anything about Keyturn:

```bash
keyturn run -s vpn=VPN_PASSWORD -- ./deploy.sh
```

From here on, whenever `vpn` goes stale the prompt asks for a new value, and `keyturn run` refuses to start
`./deploy.sh` until you've given one. See [Schedules](#schedules) for the formats and
[Using secrets from your tools](#using-secrets-from-your-tools) for the details.

## Schedules

`12h`, `7d`, `30m` (stale that long after it was last set) or `daily@06:00` (stale once the most recent local
06:00 has passed). Stale values stay readable, but `get`/`run` refuse them unless `--allow-stale`.

## Using secrets from your tools

There are two supported ways. Neither makes your tool depend on Keyturn's code or storage format.

### Let keyturn inject the secret

```bash
keyturn run -s vpn=VPN_PASSWORD -- ./deploy.sh
```

The command runs with `VPN_PASSWORD` in its environment. The tool only knows it reads an environment
variable. `keyturn` refuses to start the command if the secret is missing or expired.

### The credential-helper pattern

Give your tool one setting, "run this command to get the password", and point it at `keyturn get`:

```bash
export DEPLOY_PASSWORD_COMMAND="keyturn get vpn"
```

```bash
PASSWORD=$(keyturn get vpn) ./deploy.sh        # in a script
```

The tool never imports or knows about Keyturn, and you can swap the command for another secret source later.
Some tools expect a *file* or an *askpass* program rather than a command setting; a two-line wrapper does it:

```bash
#!/bin/sh
# ~/bin/vpn-password: usable wherever a tool wants "a script that prints the password"
exec keyturn get vpn --refresh
```

Whether a given tool accepts such a script depends on the tool; check its docs.

### The CLI contract

| | |
| --- | --- |
| **stdout** | Only data: the secret value, or JSON with `--json`. No trailing newline when piped; one on a terminal. |
| **stderr** | All diagnostics and prompts. |
| **exit codes** | `0` ok, `1` usage error or unknown secret, `2` no value, `3` stale, `4` storage error (credential store or config file). |
| **no surprises** | `get` never prompts on the terminal and never reads stdin. Only `--refresh` may open the GUI. |

```bash
keyturn get NAME [--refresh] [--timeout SECONDS] [--allow-stale] [--json]
keyturn run -s NAME[=ENV_VAR] ... [--refresh] [--allow-stale] -- command args...
keyturn list|status NAME|due [--json]
keyturn add NAME SCHEDULE [DESCRIPTION]     keyturn set NAME     keyturn remove NAME
```

**`--refresh`** is for unattended tools: if the secret is stale or missing, Keyturn opens the prompt for just
that secret and waits (default 5 minutes, `--timeout`), then continues with the new value. If many tools hit
the same expired secret at once, they share **one** prompt. If there's no graphical session, the prompt can't
be shown, or the wait times out, `get` exits `2`/`3` as if `--refresh` hadn't been given.

**`--json`** output is stable and never contains a secret value except in `get --json`:

```json
{ "name": "vpn", "schedule": "daily@06:00", "description": "Corporate VPN", "state": "fresh",
  "lastSetAt": "2026-10-11T06:02:11-04:00", "expiresAt": "2026-10-12T06:00:00-04:00" }
```

`state` is `fresh`, `stale` or `missing`; `list` and `due` return arrays.

### What keyturn does not do

* It doesn't authenticate callers: any process running as you can run `keyturn get`. `run` puts the secret in
  the child's environment, where other processes of the same user can read it.
* The CLI is a process per call (startup time not measured), and the `keyturn` binary must be on `PATH`.
* Reading the OS store directly bypasses the expiry check and is **not** a supported interface; the entry
  layout is an implementation detail and may change.

## Scheduled prompt

`keyturn schedule install` launches the app at login and hourly (systemd user timer / launchd agent / Task
Scheduler; `--print` previews). The app exits instantly, with no window, unless something is due.

## Under the hood

### Where secrets live

| OS | Store |
| --- | --- |
| macOS | Login keychain (generic password) |
| Windows | Credential Manager (`keyturn:NAME`) |
| Linux | Secret Service via libsecret |

### Platform notes

* **Linux** needs a Secret Service provider (GNOME Keyring, KWallet, KeePassXC, oo7), libsecret, and an
  unlocked keyring. There is no fallback for headless machines yet. `--refresh` needs a graphical session.
* **Windows** limits a secret to 1,280 characters.
* **macOS and Windows are newer than Linux.** CI builds, installs and smoke-tests both on real runners, but the
  credential-store code itself has only been exercised by hand on Linux so far. Expect rough edges.
* Secrets pass through managed strings and live in process memory briefly; this protects secrets at rest and
  from other users, not from malware running as you.

## Building from source

Requires the .NET 10 SDK.

```bash
git clone https://github.com/Dwarf1er/keyturn.git
cd keyturn
dotnet build
dotnet test
```

To build the release binaries for your own OS, then smoke-test them:

```bash
scripts/publish.sh linux-x64 1.0.0 dist/keyturn
scripts/smoke.sh dist/keyturn 1.0.0
```

Neither NativeAOT nor the self-contained build cross-compiles between operating systems, which is why CI uses one
runner per OS. The build flags live in each project's publish profiles (`src/*/Properties/PublishProfiles`), shared by
`scripts/publish.sh`, the installers and CI. `scripts/make-icons.sh` regenerates the icons in `assets/` from
`assets/keyturn-mark.svg`.

### Releasing

Pushing a tag builds and publishes everything (see `.github/workflows/release.yml`):

```bash
git tag v1.0.0 && git push origin v1.0.0      # v1.0.0-rc.1 publishes a prerelease
```

The workflow tests on Linux, Windows and macOS, builds each platform, smoke-tests it (including loading the app's
embedded native libraries), and attaches the archives and `SHA256SUMS` to a GitHub release. **Actions > release >
Run workflow** does a dry run (everything except publishing) from any branch.

## Acknowledgements

* [Git Credential Manager](https://github.com/git-ecosystem/git-credential-manager), whose macOS and Windows native
  interop declarations are adapted in `src/Keyturn.Core/ThirdParty/GitCredentialManager/` (MIT).
* [Avalonia](https://avaloniaui.net/) for the prompt window.
* [Inter](https://rsms.me/inter/) by Rasmus Andersson, converted to outlines for the wordmark (SIL Open Font License).

## License

This software is licensed under the [MIT license](LICENSE).
