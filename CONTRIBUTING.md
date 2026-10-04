# Contributing to PDWSLC

The current implementation is `desktop.ps1`. Run it from Windows PowerShell;
WSL 3.0.1 or later is required. The WSL Containers runtime is a Windows
dependency. No WSL source build, Docker
Desktop, or .NET build is needed for the prototype.

## Validate launcher changes

Check PowerShell parsing and `git diff --check`. For behavior changes, use a
dedicated test desktop with its own name, port, and home volume. Check creation,
repeated start, status, logs, stop, and resume. Write a temporary file in
`/config`, verify it after resume, and remove only that test file. Check the
container ID, port mapping, and mounts remain the same when reusing a desktop.

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

The native launcher and its build projects are planned, not yet present.
