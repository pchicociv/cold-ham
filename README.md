# Cold Ham

Cold Ham is a Windows desktop utility for blocking distracting websites and applications. Built with .NET 10 and WPF, it keeps your configuration on your computer and provides manual focus sessions, a weekly schedule, and controls to reverse its blocking changes.

## Features

- Focus sessions lasting 25 minutes, 50 minutes, a custom duration, or indefinitely.
- A weekly schedule with selected days and an optional overnight time window.
- Website blocking through an app-owned section of the Windows hosts file.
- An allowed website list for exceptions, plus an optional **Allowed websites only** mode for Chrome and Edge.
- Application blocking by executable name or full `.exe` path, with confirmation before closing matching processes.
- A system tray icon, activity messages, and recovery controls.
- Spanish and English interfaces. Spanish is the default; select **English** in the **Idioma / Language** selector.
- Installation and automatic updates through Velopack for packaged builds.

## Build and run

You need Windows and the .NET 10 SDK. Run these commands from the repository root:

```powershell
dotnet restore ColdHam.slnx
dotnet build ColdHam.slnx -c Release
dotnet run --project src/ColdHam.App/ColdHam.App.csproj -c Release
```

The initial restore needs access to NuGet for Velopack. The domain, Windows enforcement, and test projects have no external package dependencies.

Launching the app and editing configuration do not require administrator rights. Applying or removing hosts-file website rules does require elevation; use **Restart as administrator** when prompted.

## Using Cold Ham

1. In **Websites**, add domains such as `example.com` to block.
2. In **Allowed**, add website exceptions or enable **Allowed websites only** for supported browsers.
3. In **Applications**, add executable names such as `example.exe` or select full executable paths. Save your work in those applications before authorizing their closure.
4. Choose a session duration and select **Start**, or enable a weekly window in **Schedule**. Custom sessions accept 1–1440 minutes.
5. Check the status and activity messages to confirm whether rules were applied. Resolve any reported issue and use **Retry** when needed.

**End session** ends only the manual session. Blocking continues if the weekly schedule is currently active. **Stop all** ends the session and disables the schedule, then attempts to remove web rules.

Closing the window hides Cold Ham in the system tray while enforcement continues. Use the tray menu to reopen it or exit. A normal exit removes web rules and preserves the configured schedule for the next launch. If rule removal fails, the app stays open and shows recovery controls.

## How blocking works

### Websites

Standard website blocking manages only the section between `# Cold Ham BEGIN` and `# Cold Ham END` in the Windows hosts file. Unrelated content is preserved. Allowed domains take priority over matching blocked domains within Cold Ham's rules.

Rules cover exact domains and their `www` variants. Add other subdomains separately. Hosts-file blocking does not filter individual URL paths, searches, or every subdomain automatically.

**Allowed websites only** replaces the blocked website list with Chrome and Edge policies during an active session or schedule. An empty allowed list blocks all website navigation covered by those policies. Restart the browser after applying or removing policies, and inspect `chrome://policy` or `edge://policy` if necessary.

This mode does not cover other browsers, other applications, or Edge profiles using personal Microsoft accounts. Internal browser pages remain available. Cold Ham refuses conflicting existing browser URL policies and removes only policy values it owns.

### Applications

Cold Ham periodically detects and closes processes matching the configured executable names or full paths. Authorization covers the current rule set, including future launches during the active blocking period. Cold Ham itself, protected Windows processes, and unsafe or wildcard rules are excluded.

### Sessions and schedules

Weekly schedules use local time. An end time earlier than the start time continues into the following day; equal start and end times cover the whole selected day. Manual sessions store an absolute end time and can resume after reopening the app if they have not expired.

Enforcement runs inside the Cold Ham process, without a Windows service. Schedules and process monitoring require the app to remain running. If the process is forcibly stopped or the computer sleeps, existing web rules can remain until Cold Ham resumes or reopens and reconciles them. The session timer tracks elapsed time even if a blocking operation fails.

## Local configuration

Settings, rules, language, and sessions are stored at:

```text
%LocalAppData%\ColdHam\settings.json
```

Saves retain the previous settings as `settings.json.bak`. Invalid settings are preserved in a `.corrupt-*` file; recovery disables manual and scheduled activation so you can review the recovered configuration. Browser policy recovery information also lives under `%LocalAppData%\ColdHam`.

Blocking works locally. Packaged builds use a network connection to check for and download updates. Update failures do not stop enforcement, and update restarts are deferred while blocking or recovery work is active.

## Tests

The tests use an executable harness rather than a test framework. Run them with:

```powershell
dotnet run --project tests/ColdHam.Core.Tests/ColdHam.Core.Tests.csproj -c Release
```

Coverage includes normalization, scheduling, sessions, persistence and recovery, hosts-file transformations, application consent, browser policies, localization, and update coordination. Tests use temporary files and simulated adapters; they do not require elevation or change the real hosts file.

## Packaging

Create a self-contained Windows x64 portable build:

```powershell
dotnet publish src/ColdHam.App/ColdHam.App.csproj -p:PublishProfile=Portable-win-x64
```

The output is written to `bin/portable/win-x64/`. This standalone portable build does not receive managed installation updates.

To build installer and update packages:

```powershell
./scripts/Build-Release.ps1 -Version 1.0.0
```

Use the intended release version when packaging. See [RELEASING.md](RELEASING.md) for the release workflow, signing, and update validation details. That document is currently in Spanish.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/ColdHam.App` | WPF interface, tray integration, and update adapter |
| `src/ColdHam.Core` | Models, normalization, scheduling, persistence, and update coordination |
| `src/ColdHam.Windows` | Hosts-file, browser policy, and process enforcement |
| `tests/ColdHam.Core.Tests` | Executable test harness |
| `scripts/Build-Release.ps1` | Build, test, publish, and package automation |

See [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) for architecture, implementation decisions, and outstanding manual validation.
