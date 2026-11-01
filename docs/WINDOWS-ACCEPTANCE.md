# Windows 11 x64 acceptance checklist

Pending operator execution on Windows; Linux cannot launch WPF.

1. Run the README build and all three test-project commands. Record SDK, OS, totals and exit codes.
2. Launch the desktop as a regular user. Verify the title, boot transcript, simulation banner and green fixed-width theme with CRT off.
3. Select all ten modules using the mouse, Tab/arrows and Alt+1..0. Verify content changes and remains readable at minimum window size and Windows 125%/150% display scaling.
4. Press Ctrl+L, enter `help`, then `open security`, `open console`, `crt on`, `crt off`, `refresh`, and `clear`. Verify F1/F5 and rejection of `stop` and shell commands.
5. Filter console rows for `spawn`, verify only matching demonstration messages remain, then clear the filter.
6. Confirm TPS/MSPT show UNAVAILABLE and every sampled dashboard value is identified as simulation. No live operations are offered.
7. Restart desktop; confirm prior snapshots remain in `%LOCALAPPDATA%\ShadowServerCommand\desktop.db` and a new sample is appended. Inspect `schema_migrations` (v1) with a read-only SQLite viewer.
8. With the desktop stopped, exercise a protected/unwritable test data location using a disposable Windows test identity. Verify clear startup/archive errors and F5 recovery after restoring access. Do not alter a real archive to test corruption.
9. Close the desktop and verify Minecraft's process is unaffected. Verify no administrative listeners or server-directory writes occurred.
10. Inspect structured JSON logs for origin and failures. Validate rotation in a disposable environment using reduced thresholds; production limits are daily/10 MiB/14 files.

Do not mark Phase 1 fully accepted until the WPF launch and this interaction checklist pass. Agent/Watchdog real telemetry and outage behavior belong to later milestones.
