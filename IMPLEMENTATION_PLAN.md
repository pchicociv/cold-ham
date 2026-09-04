# Cold Ham implementation plan

## Goal

Build a simple Windows desktop application in .NET that lets a user maintain website and application block lists, activate them immediately or on a weekly schedule, and reverse every system change cleanly.

## Product scope

### MVP

1. A single WPF window with four practical areas: Overview, Websites, Applications, and Schedule.
2. Website rules entered as domains. Active rules are written to an app-owned section of the Windows hosts file and removed when blocking stops.
3. Application rules entered as executable names or selected `.exe` paths. While blocking is active, matching processes are detected and terminated after explicit user confirmation when enabling the block.
4. Manual Start/Stop controls plus an optional weekly time window.
5. Local JSON persistence, an activity log, validation, duplicate prevention, and clear administrator-rights errors.
6. A small test project covering domain normalization, hosts-file updates, schedule decisions, and configuration persistence.

### Deliberately deferred

- Browser extensions, URL-path or keyword filtering, incognito statistics, cloud sync, remote administration, password locks, donation breaks, and anti-tamper kernel/service components.
- Blocking Microsoft Store apps by package identity.
- Cross-platform support.

## Architecture

- `src/ColdHam.App`: WPF presentation and application composition.
- `src/ColdHam.Core`: models, validation, scheduling, configuration storage, and enforcement abstractions.
- `src/ColdHam.Windows`: Windows hosts-file and process enforcement implementations.
- `tests/ColdHam.Core.Tests`: dependency-free executable test harness so the repository can build and test offline.

The app runs enforcement in-process on a short timer. This keeps the MVP transparent and easy to uninstall. A Windows service can be introduced later if enforcement must continue after the UI exits.

## Safety and reversibility

- Hosts-file changes are bounded by `# Cold Ham BEGIN` and `# Cold Ham END` markers.
- Applying a website block replaces only that marked section; stopping removes only that section.
- Domain input is normalized and restricted to valid host names. Wildcards and raw hosts-file lines are rejected.
- Executable rules are normalized to a file name for matching. Protected/system process names and Cold Ham are rejected.
- The app does not attempt anti-uninstall or privilege escalation.

## Interface direction

- Visual thesis: compact native Windows utility, neutral surfaces, one blue action color, and status-led hierarchy.
- Content plan: current status and master action; editable website list; editable application list; weekly schedule; recent activity.
- Interaction thesis: immediate state feedback when blocking changes, inline validation on rule entry, and unobtrusive status/log updates. No ornamental motion.

## Implementation sequence

1. Create solution/project structure and ignore rules.
2. Implement domain models, validation, schedule evaluation, and JSON storage.
3. Implement bounded hosts-file transformation and Windows process enforcement.
4. Build the WPF interface and wire commands, timers, persistence, and elevation-aware errors.
5. Add tests and run `dotnet build` plus the test harness.
6. Perform a smoke test that uses a temporary hosts file and a harmless process fixture; never modify the real hosts file during automated tests.

## Definition of done

- The solution builds without errors.
- Tests pass without administrator rights and without touching the machine hosts file.
- Rules survive restart.
- Starting and stopping a block are visibly reflected in the UI and activity log.
- Website blocking reports when elevation is required.
- Stopping a block removes all app-owned hosts entries and leaves unrelated content byte-for-byte equivalent apart from normalized boundary whitespace.
