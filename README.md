# Shadow Server Command — Minecraft Edition

## TLS heartbeat repair — existing AM4/AM5 installations

**[Download the repaired Agent + Watchdog Windows x64 package](https://github.com/Ghost9353/ShadowServerCommand/raw/refs/heads/main/downloads/SSC-MC-tls-heartbeat-repair-win-x64.zip)**

- [AM4/AM5 update commands, verification and rollback](docs/TLS-HEARTBEAT-REPAIR.md)
- [Root cause and executed Linux/Windows tests](docs/TLS-REPAIR-REPORT.md)
- [Current source ZIP](https://github.com/Ghost9353/ShadowServerCommand/raw/refs/heads/main/downloads/SSC-MC-tls-heartbeat-repair-source.zip)

This binary-only repair preserves existing configuration, certificate identities/pins, encrypted secrets and history. It restarts only SSC services, never Minecraft. Windows Schannel tests pass; the runbook includes live checks for your actual service identities and Discord channel.

The original full Windows ZIP below predates this TLS repair. Existing installations should apply the repair package above; use the original package only as a baseline/rollback reference.


## Download to your Windows PC

- [Download the ready-built Windows x64 package](https://github.com/Ghost9353/ShadowServerCommand/raw/refs/heads/main/downloads/SSC-MC-watchdog-win-x64.zip)
- [Download the original source ZIP](https://github.com/Ghost9353/ShadowServerCommand/raw/refs/heads/main/downloads/SSC-MC-watchdog-source.zip)
- [Installation and configuration guide](docs/WATCHDOG.md)
- [Safe Discord test instructions](docs/WATCHDOG-TESTING.md)

Save the Windows ZIP to **Downloads**, right-click it in File Explorer, and select **Extract All**. Keep each published application directory together. The package includes the desktop, Agent, Watchdog, configuration examples, deployment scripts, and guides.

Required runtimes: .NET 8 Windows Desktop Runtime x64 for the desktop, ASP.NET Core Runtime 8 x64 on AM4 for the Agent, and .NET Runtime 8 x64 on AM5 for the Watchdog. Follow the installation guide before starting either service.

TLS repair Release build: zero warnings/errors; 61 automated tests passed on Linux and Windows CI. Actual service-account deployment and real Discord delivery after the repair remain to be verified on the PCs. See [the report](docs/WATCHDOG-REPORT.md).


**SSC-MC · v0.1.0 Alpha · desktop simulation + independent watchdog extension**

A Windows 11 x64 WPF operations console with a phosphor-green mainframe theme. The desktop dashboard uses labeled simulation fixtures. The opt-in Agent/Watchdog extension provides real read-only mTLS heartbeat monitoring and persistent Discord alerts, independently of the desktop. No component controls Minecraft or modifies its installation. See [watchdog installation](docs/WATCHDOG.md), [safe tests](docs/WATCHDOG-TESTING.md), [troubleshooting](docs/WATCHDOG-TROUBLESHOOTING.md) and the [watchdog development report](docs/WATCHDOG-REPORT.md).

## Build and launch

Install Visual Studio 2022 **17.8 or newer** with the **.NET desktop development** workload and a supported .NET 8 SDK. Open [ShadowServerCommand.sln](ShadowServerCommand.sln), set `SSC.Desktop` as startup project, select x64 or Any CPU on Windows x64, and press F5.

From PowerShell at the repository root:

```powershell
dotnet restore ShadowServerCommand.sln
dotnet build ShadowServerCommand.sln -c Release
dotnet test tests/SSC.UnitTests -c Release
dotnet test tests/SSC.IntegrationTests -c Release
dotnet test tests/SSC.SecurityTests -c Release
dotnet run --project src/SSC.Desktop -c Release
```

Build a Windows x64 distribution (requires .NET Desktop Runtime 8 on the destination):

```powershell
dotnet publish src/SSC.Desktop -c Release -r win-x64 --self-contained false -o artifacts/desktop
```

Run `artifacts/desktop/SSC.Desktop.exe`. Keep the published directory together. Cross-compiling WPF from Linux is supported through `EnableWindowsTargeting`; launching WPF still requires Windows. No Windows launch is implied by a successful Linux build.

## Console and navigation

- Alt+1 through Alt+9 and Alt+0 select the original ten modules; Ctrl+W opens WATCHDOG. Tab and arrow keys navigate controls.
- Ctrl+L focuses the command input; Enter executes; F1 prints help; F5 captures another simulated sample.
- `open overview`, `open performance`, `open players`, `open diagnostics`, `open worlds`, `open console`, `open security`, `open lifecycle`, `open alerts`, `open history` select modules. `open watchdog` selects the live AM5 watchdog section.
- `refresh`, `clear`, `help`, `crt on`, `crt off` are the only other local commands. CRT is off by default; no effects are needed to operate the app.
- The filter box searches the selected module's rows, including demonstration log messages. It is not a live server log search.
- Closing the desktop only exits the desktop. Server control is disabled, including requests marked confirmed.

Every sampled dashboard metric is from a fixed fixture and marked simulated. TPS, MSPT, world, process and availability signals without a configured source are explicitly unavailable. Configured versions are user-supplied values, not detection results.

## Structure

| Project | Responsibility |
| --- | --- |
| SSC.Desktop | WPF, MVVM, keyboard console, ten original modules plus WATCHDOG |
| SSC.Core | Domain models, telemetry, control, probe, alert, log and history interfaces |
| SSC.Contracts | Versioned agent/control DTOs; no network transport |
| SSC.Infrastructure | SQLite migration runner, simulation provider, DI, structured log composition |
| SSC.Agent | Private mTLS heartbeat endpoint and read-only Windows/process telemetry |
| SSC.Watchdog | Independent Windows service, probes, persistent incidents/outbox and Discord delivery |
| SSC.UnitTests | Simulation and local navigation/parser behavior |
| SSC.IntegrationTests | SQLite initialization, migration compatibility and persistence |
| SSC.SecurityTests | Command allowlist and disabled control/configuration boundaries |

The Agent and Watchdog now require explicit secure provisioning; their bundled defaults are disabled. See [the deployment guide](docs/WATCHDOG.md) for actual service installation on AM4/AM5. They do not require the desktop to run.

## Configuration, storage and recovery

The Phase 1 desktop loads its bundled `appsettings.json`; standard .NET environment overrides use double underscores (for example `Agent__Simulation`). Phase 1 startup rejects `Simulation=false` or `RemoteAdministrationEnabled=true`. Paths and port in `AgentOptions` are configuration metadata only and are not used to access the installation.

Data lives under `%LOCALAPPDATA%\ShadowServerCommand` for the desktop identity. `desktop.db` contains desktop simulation history. The operational services now use protected ProgramData paths described in the watchdog guide. The desktop appends one snapshot on startup and on F5/refresh; no background sampling runs in this milestone. Operational service state, logs and secrets use ProgramData with explicit ACLs.

Migrations run in a transaction, record their version, and reject a newer schema rather than downgrading it. SQLite uses WAL, foreign keys and a 10-second busy timeout. Snapshot queries are parameterized and bounded to 1–1000 records. The desktop loads the most recent 100. No automatic history deletion occurs. For backup, stop the corresponding application and copy the database plus any remaining WAL/SHM files together; never copy a live main database alone.

JSON logs are in `logs/<role>-*.jsonl`, rolling daily and at 10 MiB. Fourteen files per role are retained, including size rollovers; this is a file count, not a guarantee of 14 days. Database records are not tied to log retention. Avoid logging secrets or raw remote input when adding adapters.

Archive errors appear in the status line and terminal; F5 retries initialization and sampling. Startup configuration failures show an error dialog and exit. Check disk permissions, free space and logs first. Preserve a failing archive before repair; do not delete history as a recovery shortcut.

See [architecture and security](docs/ARCHITECTURE.md), [future capability setup](docs/CAPABILITIES.md), [Windows acceptance checklist](docs/WINDOWS-ACCEPTANCE.md) and [development report](docs/DEVELOPMENT-REPORT.md).
