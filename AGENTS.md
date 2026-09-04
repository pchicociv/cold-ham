# AGENTS.md

## Project

Cold Ham is a Windows-only .NET desktop utility for blocking distracting websites and applications. Keep the product local-first, understandable, and intentionally difficult to disable only while the user has explicitly enabled a block.

## Technical direction

- Target .NET 10 and WPF.
- Keep the solution dependency-free unless a package provides clear value that cannot be achieved with the platform libraries.
- Put domain models and enforcement behavior outside UI code-behind where practical.
- Persist configuration under the current user's local application-data directory.
- Website blocking must only modify entries inside a clearly marked, app-owned section of the Windows hosts file.
- Never remove or rewrite unrelated hosts-file content.
- Application blocking must match configured executable names or full paths and must never target Cold Ham itself, Windows system processes, or an empty/wildcard value.
- Operations requiring administrator rights must fail with a clear, actionable message.
- All enforcement changes must be reversible from the app.

## UI direction

- Favor a plain, compact, native Windows interface over a marketing-style or heavily themed UI.
- Use clear labels and status text. Avoid decorative animation, dashboards, and card grids.
- Keep website, application, schedule, and activity controls easy to find from one window.
- Provide confirmation before destructive list operations and before ending unrelated processes.

## Quality bar

- Build the entire solution after meaningful changes.
- Add unit tests for scheduling, normalization, persistence, and hosts-file transformations.
- Do not require elevation merely to launch or edit configuration; elevation is only needed when applying system-level website blocking.
- Update `IMPLEMENTATION_PLAN.md` when scope or architecture changes materially.

## Repository hygiene

- Do not commit generated `bin/`, `obj/`, user settings, or local logs.
- Preserve user changes and avoid destructive Git commands.
- Keep source files UTF-8 and warnings at zero where feasible.
