# Persistent Desktops for WSLC

PDWSLC is an independent community project for persistent Linux desktops on
Windows. The current PowerShell launcher runs an Ubuntu KDE desktop using
`wslc.exe` and [LinuxServer Webtop](https://docs.linuxserver.io/images/docker-webtop/).
The desktop opens in your Windows browser and can be used full screen.

Author: **Sebastian Sejzer**.

## Project status

The current launcher creates or reuses a desktop, preserves its home volume,
starts and stops it, reports status and logs, and waits for local HTTPS before
opening the browser. It also accepts a custom image, name, session, port, and
timezone. Its default timezone is `Asia/Jerusalem`; override it with `-TimeZone`.

Verified on Windows with WSLC 3.0.1: KDE startup, HTTPS response, repeated starts,
and stop/resume with a home file preserved. Persistence across Windows reboot
and WSLC session recreation has not yet been validated.

Desktop catalogs, installation wizard, managed updates, GPU configuration,
xrdp, and a native Windows launcher are planned in the
[roadmap](docs/ROADMAP.md). See the [architecture](docs/ARCHITECTURE.md) for
the proposed design and [contributing guide](CONTRIBUTING.md) for validation.

## Requirements

- **WSL 3.0.1 or later**, with `wslc.exe` available on PATH.
- Windows PowerShell 5.1 or newer, and Windows `curl.exe`.
- Internet access for the first image download.

PDWSLC requires WSL 3.0.1 or later. The launcher checks the installed WSL
version before accessing desktop containers. Check it with `wsl --version`
and update with `wsl --update` if needed.
See the [WSL Containers documentation](https://learn.microsoft.com/en-us/windows/wsl/wsl-container)
for installation and version checks.

## Run

From this directory in Windows PowerShell:

```powershell
.\desktop.ps1
```

The launcher reuses a container named `desktop`, including its existing image,
home mounts and HTTPS port. If it is stopped, the launcher starts it. If it does
not exist, the launcher creates it with the `ubuntu-kde` Webtop image, a
`desktop-home` named volume mounted at `/config`, 1 GiB of shared memory, and
HTTPS port 3001 bound to Windows loopback. It waits for an HTTP response before
opening `https://localhost:3001`. Accept Webtop's self-signed certificate in the
browser for this local address.

An existing container must publish Webtop's HTTPS port, `3001/tcp`, to an IPv4
loopback or wildcard host address. An incompatible container is retained and
reported rather than replaced.

```powershell
.\desktop.ps1 -Action Status
.\desktop.ps1 -Action Logs
.\desktop.ps1 -Action Stop
.\desktop.ps1                     # Resume and reopen
.\desktop.ps1 -NoBrowser          # Start and print the URL
.\desktop.ps1 -Session <session>  # Use the session containing your desktop
```

For a new container, customize its name, image, port or timezone:

```powershell
.\desktop.ps1 -Name xfce -Image lscr.io/linuxserver/webtop:ubuntu-xfce -Port 3002
```

Creation options apply only when the named container is absent. Startup waits
up to 300 seconds after creation; use `-TimeoutSeconds` to adjust this. A failed
startup leaves the container and volume available for inspection. The initial
image download happens before this readiness timeout.

## Persistence and lifecycle

The desktop user's home, documents and settings live in `/config`. Stopping the
container or closing the browser keeps those files. Closing the browser leaves
the container running. System package changes belong to the container's writable
layer; bake them into a custom image to retain them across container replacement.
The launcher never removes containers or volumes and does not upgrade images.

The launcher uses Webtop's browser streaming and does not require xrdp or WSLg
desktop support. GPU acceleration is not configured. For the image's desktop
variants and streaming options, see the upstream Webtop documentation.

PDWSLC code is licensed under the [MIT License](LICENSE). Desktop images and
their bundled applications retain their respective upstream licenses.
