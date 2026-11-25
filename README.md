# Project R — Open Source Enterprise ITAM & MDM

Project R is an open-source **IT Asset Management (ITAM)** and **Mobile Device Management (MDM)** platform built with **Laravel 11 + Filament 3**, a native **Windows Agent** (C# .NET Framework 4.8 + Golang), an **Android Mobile Guardian** agent (Kotlin Device Owner), and a **Golang WebSocket Remote Desktop Relay**.

## Key Features
- **Automated Hardware & Software Inventory**: Tracks CPU, RAM, storage, BIOS/UUID, monitors, antivirus status, installed apps, network adapters, and BitLocker recovery keys.
- **Hardware Tamper Alerts**: Detects physical component changes (e.g., RAM downgrade, disk swap) with approval audit logs.
- **Windows Policy Engine (MDM)**: Enforces USB storage modes (Allow / Read-Only DLP / Block), Task Manager, CMD/PowerShell restrictions, local admin governance, and browser domain blocking (`hosts` + DoH policy lock).
- **Silent Software Deployment & Scripts**: Pushes `.msi`, `.exe`, or Winget packages globally or per device with installed-app skip checks.
- **Interactive Web Remote Desktop**: Built-in low-latency Golang screen streaming with user consent approval, 2-minute timeout countdown, live two-way chat, and clipboard sync.
- **Domain-Independent Multi-Server Support**: Pre-built signed agent binaries work with **any self-hosted Project R server/domain** via installer flags (`/SERVER=... /TOKEN=... /TAG=...`) or runtime config (`agent.json`).

## Architecture
- **Web Panel & API**: Laravel 11 + Filament 3 + MySQL (`app/`, `resources/`, `routes/`)
- **Windows Service & UI (`x64` / `x86`)**: `agent/windows/src/` (`ProjectRAgent.exe`, `ProjectRManager.exe`)
- **Windows Remote Desktop Daemon (`x64` / `x86`, Win 10+)**: `agent/windows/remote-agent/` (`ProjectRRemoteAgent.exe`)
- **Remote Desktop Relay Server**: `services/remote-relay/` (`project-r-relay`)

## Quick Client Installation
Run in an elevated PowerShell window on a Windows endpoint, replacing `<your-server-domain>` with your Project R instance URL:
```powershell
irm https://<your-server-domain>/install | iex
```
Enter your server URL, Deployment Token (from **Config Receiver**), and Asset Tag in the GUI.

## Code Signing & Verifiable Builds
Windows release artifacts can be compiled directly from source via `.github/workflows/release-agent.yml` on GitHub-hosted `windows-latest` runners, supporting free open-source code signing via **SignPath Foundation** (`SIGNPATH_API_TOKEN` and `SIGNPATH_ORG_ID`).

## License
Released under the [MIT License](LICENSE).
