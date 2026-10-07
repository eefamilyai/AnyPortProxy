# AnyPortProxy

Lets people on the internet reach things running on your computers — with a friendly app, a terminal command, and one-click fixes.

| Incoming | What happens |
|---|---|
| **Websites** (ports 80 / 443) | Sent to a computer based on the address typed: `nas.reggilion.com` → your NAS, `theo.reggilion.com` → another PC. TLS is passed through untouched, so certificates stay on the backend. |
| **Every other port, TCP and UDP** | Forwarded to this PC (or a computer you choose) **on the same port**. Start something on port 1234 and `reggilion.com:1234` reaches it — no config change. |
| **Port rules** | "Send port X to computer Y", TCP and/or UDP — e.g. UDP 51820 → your NAS for WireGuard, TCP 22 → a Raspberry Pi. |

Subdomains only matter for websites (80/443): other protocols never send the hostname, so on every other port all names that point at you arrive at the same place.

## What you set up outside AnyPortProxy (once)

1. **Router:** make this PC the **DMZ host**, or forward **TCP and UDP 1–65535** to it. Without this, only the ports your router forwards ever reach the PC. (DMZ means AnyPortProxy's blocked-ports list becomes your front door — the defaults block RDP, SMB, WinRM, SSH…)
2. **DNS:** add **`*.yourdomain.com` → your public IP** (an A record) so any subdomain works without touching DNS again. Add a separate record for `yourdomain.com` itself if you want the bare domain. The Health check tells you if the wildcard is missing.
3. **Test from outside:** your phone on **mobile data** (not Wi-Fi), e.g. start `python -m http.server 12345` and open `http://yourdomain.com:12345`.

## Install

1. Download **`AnyPortProxySetup-<version>.exe`** from the [Releases](../../releases) page.
2. Run it, say *Yes* to the Windows permission prompt, press **Install**.
3. In the app: add your **Websites**, open **Ports** for your games/apps, then run the **Health check** — it finds and fixes most problems for you.

The installer contains everything (no .NET or anything else needed): the app, the background service (auto-starts with Windows, restarts itself if it ever crashes), the WinDivert network driver, a firewall rule, Start menu + desktop shortcuts, and the `apx` terminal command. Running a newer installer updates in place and keeps your settings. Uninstall from **Settings → Apps → Installed apps**, or run the installer again and choose *Uninstall*.

Silent install (for scripts): `AnyPortProxySetup-<version>.exe /S` — exit code 0 = success, log in `%TEMP%\AnyPortProxySetup.log`.

## Updates

AnyPortProxy updates itself from this repository's **GitHub Releases**:

- The app checks at startup and every 12 hours, and shows a bar with *What's new* / *Update now*. `apx update` does the same in a terminal.
- The background service also checks every 6 hours and — if *Install updates automatically* is on (default) — installs the new version **when nobody is connected** (or after a day at the latest). Settings are always kept.
- Every download is verified before it runs: exact size, GitHub's SHA-256 checksum, and the file's product name/version. Links outside github.com are refused.

For this to work: the repository must be **public**, each release needs a tag like `v1.5.0`, and the `AnyPortProxySetup-<version>.exe` from `dist\` attached. `build.ps1` bakes the repository in automatically (read from this folder's git remote — nothing is sent anywhere); override with `.\build.ps1 -UpdateRepo owner/repo`. Turn automatic installs off in Settings or with `apx update auto off`.

## Several game servers on one port (by address)

Some protocols send the address the player typed, so several servers can share one port: `mc1.example.com` → one computer, `mc2.example.com` → another, both on 25565. Players type just `mc2.example.com` (Minecraft assumes 25565).

| Works | Doesn't (the game never sends the address) |
|---|---|
| Minecraft Java, anything over HTTPS (any port), plain HTTP (any port) | Minecraft Bedrock, Valheim, CS2, Terraria, Rust, ARK… and all UDP |

**How:** Ports → *Open a port* → pick the game → tick **🏷 Give this server its own address** → type `mc2` and the computer. Or `apx game add mc2 192.168.1.30`, or `apx port open "minecraft java" --address mc2 --to 192.168.1.30`.

- The first server on a port also catches players who type your IP or an unlisted name.
- A server on **this PC** must move off the shared port (e.g. Minecraft `server-port=25566`) — the app suggests the next port and warns if something is still sitting on it.
- For games that can't do it, give each server its own port (*Send a port to another computer*), optionally with an SRV DNS record if the game supports SRV.

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

**Router:** see "What you set up outside AnyPortProxy" above, or let the port helper ask your router automatically (UPnP) per port.

## The app

- **Getting-started tour** — opens automatically the first time: 8 short illustrated steps that explain how it works and help you set up your domain (with a live DNS check), your router (shows this PC's address and opens the router page), a first website, a first game, and a health check. Reopen it from Home or Settings → Help. Every page also has a **"How does this work?"** link. Terminal version: `apx tour`.
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
apx forward udp on|off                                also forward UDP (on by default)
apx portmap add 51820 192.168.58.20 --udp --name WireGuard   send a port to another computer
apx portmap remove 51820
apx limits 20000 300                                  flood protection: total / per internet address
apx games                                             game servers sharing a port by address
apx game add mc2 192.168.58.30                        mc2.yourdomain.com → that computer (port 25565)
apx update  |  apx update --check  |  apx update auto on|off
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

## UDP

UDP has no handshake, and replies to this PC's *own* outgoing UDP (time sync, games, torrents) look exactly like new traffic — relaying those would break them. So for each new UDP sender AnyPortProxy decides:

| Situation | What happens |
|---|---|
| An app on this PC listens on all interfaces (most game/voice/VPN servers) | Gets the datagrams **directly** — nothing is relayed, the app sees real addresses. It needs a firewall rule (the port helper adds one; most servers ask on first run). |
| The app only listens on `127.0.0.1` / `::1` | **Relayed** to it. |
| Nothing listens here and forwarding goes to this PC | Ignored. |
| Forwarding goes to another computer | **Relayed** there (unless an app on this PC is using that port). |
| Windows' own UDP (DHCP, NTP, NetBIOS, IPsec, SSDP, mDNS, LLMNR, DNS) | Never touched. |

Relay sessions end after 2 minutes of silence and count against the flood limits. Turn UDP off with `apx forward udp off` or the checkbox on the Ports page.

**Port rules** (`apx portmap add 51820 192.168.58.20 --udp`, or Ports → *Send a port to another computer*) are real listening sockets on this PC, so they work even without the WinDivert driver. Measured: 200 UDP clients × 500 datagrams with zero loss and zero reordering, ~60,000 datagrams/s each way, 42 MB RAM.

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
- **IPv4 only** for the all-ports forwarding; websites and port rules accept IPv4 and IPv6.
- **HTTPS on other ports** (e.g. a dev server on :8443) shows a certificate warning unless that app has a real certificate for your domain — AnyPortProxy passes TLS through untouched.
- Backends see AnyPortProxy's address, not the visitor's real IP.
- Some antivirus products flag `WinDivert64.sys` (it's signed and widely used). The Health check / status will say if the driver was blocked.
- The Health check looks up your internet address via api.ipify.org (fallback icanhazip.com).
