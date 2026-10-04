# Contributing to PDWSLC

The runtime manager is `desktop.ps1`; `src/PDWSLC.App` contains a WPF preview
using the process adapter in `src/PDWSLC.Core`. Run the script from Windows PowerShell;
WSL 3.0.1 or later is required. The WSL Containers runtime is a Windows
dependency. The PowerShell manager needs neither a WSL source build nor Docker
Desktop. Building the native app requires the .NET 10 SDK.

## Validate launcher changes

Check PowerShell parsing and `git diff --check`. For behavior changes, use a
dedicated test desktop with its own name, port, and home volume. Check creation,
repeated start, status, logs, stop, and resume. Write a temporary file in
`/config`, verify it after resume, and remove only that test file. Check the
container ID, port mapping, and mounts remain the same when reusing a desktop.

Run the management regression tests in Windows PowerShell. They parse the
launcher and exercise it with an in-memory WSLC runtime, including listing,
legacy discovery, shared-storage refusal, deletion, creation, and resume:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\management.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\requirements.ps1
```

The requirements tests mock every WSL install/update and feature-change command
and bypass the helper's elevation guard only inside the test harness. Core tests
simulate missing/old prerequisites, post-install verification, denied consent,
and reboot-required outcomes. Neither suite installs anything on the host.
These tests do not validate actual image startup, browser streaming, WSLC storage
durability, or a clean-Windows setup/reboot cycle. Integration checks must use dedicated test resources.

```powershell
.\desktop.ps1 -Name pdwslc-test -Port 3101 -NoBrowser
.\desktop.ps1 -Name pdwslc-test -Action Status
.\desktop.ps1 -Name pdwslc-test -Action Logs
.\desktop.ps1 -Name pdwslc-test -Action Stop
.\desktop.ps1 -Name pdwslc-test -NoBrowser
```

Reserve tests involving WSLC termination or Windows reboot for a dedicated test
environment. Record the Windows, WSL, WSLC, image digest, transport, and GPU
versions with integration results. Do not claim a capability based only on a
successful CLI call: display, encoding, audio, and reconnect need observable
end-to-end checks.

## Scope

Use [the roadmap](docs/ROADMAP.md) to separate milestones. New features should
extend the shared desktop lifecycle rather than embed runtime logic directly
in a UI. Avoid modifying unrelated WSLC containers, global certificates, or
host drivers. Keep secrets and runtime storage outside the repository.

## Validate native app changes

Use the .NET 10 SDK. Build WPF and run the portable core checks:

```powershell
dotnet build .\src\PDWSLC.App\PDWSLC.App.csproj -c Release
dotnet run --project .\tests\PDWSLC.Core.Tests\PDWSLC.Core.Tests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build-app.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\native-app-smoke.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\portable-app-smoke.ps1
```

The native smoke check opens and closes the published app, waits for read-only
discovery, and checks the Host tree, context menus, creation form, and desktop
Properties using Windows UI Automation. Folder creation uses a disposable
metadata file via the process-local `PDWSLC_FOLDER_DATA_PATH` environment
variable, leaving the user's organization intact. It does not click runtime
creation or deletion actions. Test create/start/stop/open/delete behavior
manually using dedicated names, ports, and home volumes. Check empty lists,
invalid sessions, malformed names/ports, legacy storage authorization, shared
volume refusal, partial deletion errors, moves, drag/drop, retained orphan folder
placement, folder removal, and session isolation. Never use existing personal home
storage for destructive checks.

Cross-compiling WPF and publishing `win-x64` from Linux is supported with the
.NET 10 SDK; actual UI and WSLC checks require Windows. A self-contained publish
produces a single executable containing .NET, native libraries, and embedded
PowerShell management and prerequisite scripts. `portable-app-smoke.ps1` copies only that executable to a disposable Windows
temporary folder and runs the native smoke check without sidecars.
The scripts extract into a user cache, separate from containers and home data;
run the smoke check on a copy with no files next to it before distributing.
