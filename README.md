# AnyPortProxy

Lets people on the internet reach things running on your computers — with a friendly app, a terminal command, and one-click fixes.

| Incoming | What happens |
|---|---|
| **Websites** (ports 80 / 443) | Sent to a computer based on the address typed: `nas.reggilion.com` → your NAS, `theo.reggilion.com` → another PC. TLS is passed through untouched, so certificates stay on the backend. |
| **Every other TCP port** | Forwarded to this PC (or a computer you choose) **on the same port**. Start something on port 1234 and `reggilion.com:1234` reaches it — no config change. |
| UDP (games like Minecraft Bedrock) | Not relayed, but the port helper sets up Windows Firewall + your router so UDP goes straight to this PC. |

## Install

1. Download **`AnyPortProxySetup-<version>.exe`** from the [Releases](../../releases) page.
2. Run it, say *Yes* to the Windows permission prompt, press **Install**.
3. In the app: add your **Websites**, open **Ports** for your games/apps, then run the **Health check** — it finds and fixes most problems for you.

The installer contains everything (no .NET or anything else needed): the app, the background service (auto-starts with Windows, restarts itself if it ever crashes), the WinDivert network driver, a firewall rule, Start menu + desktop shortcuts, and the `apx` terminal command. Running a newer installer updates in place and keeps your settings. Uninstall from **Settings → Apps → Installed apps**, or run the installer again and choose *Uninstall*.

Silent install (for scripts): `AnyPortProxySetup-<version>.exe /S` — exit code 0 = success, log in `%TEMP%\AnyPortProxySetup.log`.

## Build it yourself

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Produces `dist\AnyPortProxySetup-<version>.exe` (and the loose app in `publish\`). The first run downloads the WinDivert driver from its official GitHub release. Requires the .NET 10 SDK.

Before installing, turn off the old setup that holds ports 80/443 (the Health check will tell you if anything is still in the way):
```powershell
wsl --shutdown
```
and remove only the `netsh portproxy` rules for 80/443 (the Health check offers a one-click fix that removes just those).

**Router:** forward TCP 1–49151 (or use DMZ) to this PC. Or let the port helper ask your router automatically (UPnP) per port.

## The app

- **Home** — status at a glance and the next sensible thing to do.
- **Websites** — *Add website*: type the address (just `nas` is enough once your domain is set), pick the computer, done. Live hints check that the address exists on the internet and points to you, and *Test connection* checks the computer answers.
- **Ports (games & apps)** — *Open a port*: pick Minecraft, Valheim, Plex, a web dev server… (or type a port). One click sets up AnyPortProxy, **Windows Firewall**, and optionally **your router (UPnP)**. It warns about dangerous ports (RDP, SMB…), notices whether your server is actually running, and tells you the address to give friends. *Check a port* explains why people can't connect, with fix buttons.
- **Health check** — service, firewall, WinDivert driver, programs hogging 80/443 (with a hint about which one and how to stop it), old portproxy rules, whether each website's computer answers, DNS records vs your internet address, CGNAT / double-NAT detection. **Fix everything I can** applies the one-click fixes.
- **Activity** — live, colour-coded log of every connection.
- **Settings** — domain, log detail, open the terminal menu, reinstall/uninstall.

## The terminal (`apx`)

Installing adds the `apx` command (open a new terminal afterwards). Just type **`apx`** for a guided menu, or use commands:

```text
apx status                          is it running, what's set up
apx setup                           guided first-time setup
apx check [--fix]                   health check (and fix)
apx start | stop | restart
apx logs -f                         watch live activity

apx domain reggilion.com
apx sites
apx site add nas 192.168.58.20                       http→80, https→443 on that computer
apx site add theo 192.168.58.30 --http 5000 --https 5001
apx site add white this-pc --http 8080 --https off   website on this PC
apx site remove nas

apx ports
apx port presets mine                                 known games/apps
apx port open "minecraft java" --router               firewall + router + forwarding
apx port open 2456-2458 --udp --name Valheim
apx port check 25565 [--fix]
apx port close 25565 [--block]

apx forward                                           all-ports forwarding settings
apx forward block 3000 | unblock 3000
apx forward allow "3000-3999, 25565"
apx forward lan on|off
apx forward to 192.168.58.50
apx forward smart on|off                              smart routing (see below)
apx limits 20000 300                                  flood protection: total / per internet address
```

Commands that change things ask Windows for Administrator permission automatically (the result shows in a new window).

## Where things live

| What | Where |
|---|---|
| Program | `C:\Program Files\AnyPortProxy` (`AnyPortProxy.exe` = service + `apx`, `AnyPortProxyGui.exe` = app) |
| Settings | `C:\ProgramData\AnyPortProxy\config.json` — changes apply live, no restart needed |
| Logs | `C:\ProgramData\AnyPortProxy\logs` (14 days kept) |
| Live status | `C:\ProgramData\AnyPortProxy\status.json` (written by the service every 2 s) |

`install.ps1` / `uninstall.ps1 [-Purge]` do the same as the app's buttons, for scripting.

## Smart routing (on by default)

When forwarding to this PC, every new connection is checked against the live list of listening apps before anything is spent on it:

| Situation | What happens |
|---|---|
| Nothing runs on that port (port scanners, typos) | Ignored — Windows refuses it. No proxy connection, no log spam. |
| The app was opened with the port helper and listens on all interfaces | Handed **straight to the app**: zero proxy overhead, and the app sees the visitor's real IP. |
| The app only listens on `127.0.0.1` | Proxied to it (this is what makes localhost-only apps reachable). |
| The app only listens on `::1` (Node / Vite on "localhost") | Proxied to `::1` automatically. |

Turn it off with `apx forward smart off` or the checkbox on the Ports page.

## Built to keep running

- **Broken settings can't take it down.** A typo, a half-saved or a deleted `config.json` is detected and ignored; the proxy keeps running on the last good settings and the app / `apx status` / Health check say what's wrong. Invalid entries are skipped individually with a note.
- **Flood protection.** Caps on total connections (20,000) and per internet address (300; home-network addresses are exempt). Connection logging is rate-limited with a summary line, and the log file goes quiet past 256 MB/day, so floods can't fill the disk.
- **Self-repair.** The packet driver is restarted automatically if it ever stops; anything that fails to start is retried every 30 seconds; missing firewall rules are restored hourly and router (UPnP) forwards re-checked every 20 minutes. Windows restarts the service if the process ever dies.
- **Fast.** Packets are processed in batches of up to 128 per driver call across several threads with incremental checksums; routes are pre-compiled hash lookups; idle connections hold no buffers (2,000 idle connections ≈ 34 MB). Measured on loopback: ~1,800 new connections/s and ~180 MB/s through the proxy with zero errors.
- **"Site offline" page** for http visitors when the computer behind an address is down, instead of a silent hang-up (never reveals internal addresses).

## Tests

```powershell
dotnet build tests\AnyPortProxy.Tests -c Release
$env:ANYPORTPROXY_DATA = "$env:TEMP\apx-test"    # use a scratch settings folder
tests\AnyPortProxy.Tests\bin\Release\net10.0-windows\win-x64\ApxTests.exe          # 40,000+ unit checks
# load test against a running proxy:  ApxTests.exe load <proxyPort> <backendPort> <requests> <bulkMB>
```

## How the all-ports forwarding works

WinDivert rewrites inbound SYNs (e.g. to `:1234`) to an internal listener (`ListenPort`, default 34010), remembers the original port, and rewrites replies back. The listener connects to `target:1234`. Because the target defaults to `127.0.0.1`, apps that only listen on localhost become reachable from the internet too. If the service stops or crashes, WinDivert stops diverting and traffic falls back to normal Windows behaviour. Changes to forwarded/blocked ports apply instantly (a new filter handle replaces the old one).

## Caveats

- **Security:** for every forwarded port, AnyPortProxy effectively *replaces* Windows Firewall for internet visitors — including localhost-only services. Keep the blocked list (RDP, SMB, WinRM, SSH are blocked by default) or narrow the forwarded ports.
- **Home-network devices** aren't redirected by default, so Windows networking on your LAN is unaffected; they connect directly (that's what the per-port firewall rule is for).
- **IPv4 only** for the all-ports forwarding; websites accept IPv4 and IPv6.
- Backends see AnyPortProxy's address, not the visitor's real IP.
- Some antivirus products flag `WinDivert64.sys` (it's signed and widely used). The Health check / status will say if the driver was blocked.
- The Health check looks up your internet address via api.ipify.org (fallback icanhazip.com).
