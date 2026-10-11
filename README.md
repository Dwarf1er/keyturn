# Keyturn

Rotating secrets (a daily VPN password, a weekly token) kept in your OS credential store and handed to your
other dev tools through one small CLI. A desktop prompt asks you for a new value only when one is due, so
nothing sits in a `.json` file.

- **`keyturn`** (CLI): the supported way for other tools to get a secret. See [Using secrets from your tools](#using-secrets-from-your-tools).
- **`Keyturn.App`** (Avalonia): the prompt window for secrets that are due; `--manage` to add, edit and remove.
- **`Keyturn.Core`**: the library behind both.

Values live only in the OS credential store. `secrets.json` in your config dir holds names, schedules and
last-set times, never values.

## Schedules

`12h`, `7d`, `30m` (stale that long after it was last set) or `daily@06:00` (stale once the most recent local
06:00 has passed). Stale values stay readable, but `get`/`run` refuse them unless `--allow-stale`.

## Using secrets from your tools

There are two supported ways. Neither makes your tool depend on Keyturn's code or storage format.

### 1. Let keyturn inject the secret

```sh
keyturn run -s vpn=VPN_PASSWORD -- ./deploy.sh
```

The command runs with `VPN_PASSWORD` in its environment. The tool only knows it reads an environment
variable. `keyturn` refuses to start the command if the secret is missing or expired.

### 2. The credential-helper pattern

Give your tool one setting, "run this command to get the password", and point it at `keyturn get`:

```sh
export DEPLOY_PASSWORD_COMMAND="keyturn get vpn"
```

```sh
PASSWORD=$(keyturn get vpn) ./deploy.sh        # in a script
```

The tool never imports or knows about Keyturn, and you can swap the command for another secret source later.
Some tools expect a *file* or an *askpass* program rather than a command setting; a two-line wrapper does it:

```sh
#!/bin/sh
# ~/bin/vpn-password: usable wherever a tool wants "a script that prints the password"
exec keyturn get vpn --refresh
```

(Whether a given tool accepts such a script depends on the tool; check its docs.)

### The CLI contract

| | |
|---|---|
| **stdout** | Only data: the secret value, or JSON with `--json`. No trailing newline when piped; one on a terminal. |
| **stderr** | All diagnostics and prompts. |
| **exit codes** | `0` ok, `1` usage error or unknown secret, `2` no value, `3` stale, `4` credential store error. |
| **no surprises** | `get` never prompts on the terminal and never reads stdin. Only `--refresh` may open the GUI. |

```sh
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

- It doesn't authenticate callers: any process running as you can run `keyturn get`. `run` puts the secret in
  the child's environment, where other processes of the same user can read it.
- The CLI is a process per call (startup time not measured), and the `keyturn` binary must be on `PATH`.
- Reading the OS store directly bypasses the expiry check and is **not** a supported interface; the entry
  layout is an implementation detail and may change.

## Scheduled prompt

`keyturn schedule install` launches the app at login and hourly (systemd user timer / launchd agent / Task
Scheduler; `--print` previews). The app exits instantly, with no window, unless something is due.

## Implementation notes

| OS | Store |
|---|---|
| macOS | Login keychain (generic password) |
| Windows | Credential Manager (`keyturn:NAME`) |
| Linux | Secret Service via libsecret |

- **Linux** needs a Secret Service provider (GNOME Keyring, KWallet, KeePassXC, oo7), libsecret, and an
  unlocked keyring. There is no fallback for headless machines yet. `--refresh` needs a graphical session.
- **Windows** limits a secret to 1,280 characters.
- **macOS and Windows have not been run yet.** They build for every target, but the store code has only been
  exercised on Linux. Expect the first real run to need fixes.
- Secrets pass through managed strings and live in process memory briefly; this protects secrets at rest and
  from other users, not from malware running as you.

## Third-party code

The macOS and Windows native-interop declarations are adapted from
[Git Credential Manager](https://github.com/git-ecosystem/git-credential-manager) (MIT). See
`src/Keyturn.Core/ThirdParty/GitCredentialManager/`.
