# Watchdog validation and safe Discord test

## Automated tests

```powershell
./scripts/verify.ps1
```

No test touches either actual PC, real Minecraft files, real Java processes or a real webhook. Unit tests use supplied timestamps and independent observations. Integration tests use isolated SQLite stores, locally generated ephemeral certificates, real loopback HTTPS/TCP listeners and a recording HTTP test transport. The transport verifies actual Discord JSON serialization, mentions disabled, alert IDs, retry scheduling, persisted queue/state and outage/recovery ordering; it does **not** prove a message appeared in a real Discord channel.

The matrix covers:

| Requested scenario | Safe automated substitute | Windows/manual follow-up |
| --- | --- | --- |
| Normal operation | Healthy samples and real local mTLS heartbeat | Observe AM5 for five minutes |
| AM4 agent stopped | Heartbeat fails, other probes succeed | Stop only SSC.Agent; restore it afterward |
| Minecraft Java stopped | Agent reports verified process absent | Test on a staging server or during approved maintenance |
| TCP 25565 unavailable | Closed disposable loopback port | Point a disposable watchdog config at a closed staging port |
| AM4 network disconnected | All probes fail | Use an isolated test host/VM or staged address, not AM4's production NIC |
| AM4 powered off | Same unavailable-probe samples | Observe only a planned graceful AM4 shutdown; no forced power-off test |
| AM4 restored | Three healthy heartbeats; one recovery | Restore staging host / actual host after planned maintenance |
| Discord unavailable | Test transport throws / returns 429 / 404 | Test network outage on staging; verify queue on AM5 |
| Watchdog restarted | Reopen SQLite and reconcile with gap | Restart-Service SSC.Watchdog; inspect state continuity |
| AM5 rebooted | Same persisted-state gap reconciliation | Planned normal AM5 reboot; confirm Automatic service without login |

Also verify wrong/missing client certificates and wrong server pin are rejected; unknown process telemetry never fabricates a Minecraft crash. Minecraft status protocol tests distinguish TCP acceptance from a responding status handler. No automated test kills a process or shuts down a PC.

## Safe real Discord test on AM5 — Minecraft stays running

First complete secure certificate and DPAPI provisioning in [WATCHDOG.md](WATCHDOG.md). Ensure the configured webhook points to your intended test/operations channel.

1. Open elevated Windows PowerShell on AM5. Record the current service state and stop only the watchdog. This intentionally pauses monitoring briefly, not Minecraft:

   ```powershell
   Get-Service SSC.Watchdog
   Stop-Service SSC.Watchdog
   ```
2. Run the test and always resume monitoring:

   ```powershell
   try {
       ./scripts/Test-SscDiscord.ps1
   } finally {
       Start-Service SSC.Watchdog
   }
   ```

   The script enqueues an explicitly labeled **OPERATOR TEST**, calls the same persistent production outbox/delivery implementation once, and reports success only if that specific test ID was acknowledged with a Discord success response. It never reads or changes server files, stops Java, fabricates an outage, or prints a URL. Existing FIFO backlog can leave the test pending; if so, the script fails honestly and the restarted service drains pending alerts in order.
3. Verify **SHADOW SERVER COMMAND — OPERATOR TEST** in the designated Discord channel. Record its event ID and actual UTC event time. HTTP success alone does not verify channel selection; check visually.
4. Run `./scripts/Get-SscStatus.ps1`, confirm service Running, pending queue drains, and last delivery time updates. Expect a monitoring-gap record from stopping/restarting the watchdog.
5. If delivery fails, do not repeatedly enqueue new tests. Repair the existing pending/held delivery, then allow the service to drain it. Never paste the URL in a ticket or diagnostic transcript.

This is the actual-channel test chosen by the operator. It was not run on the operator's PCs from the development environment.

## Safe local agent-outage/recovery exercise

With Minecraft running and normal AM4/AM5 networking intact, stop **only** `SSC.Agent` on AM4 during an agreed monitoring test window. After three failed heartbeats, expect **AM4 reachable — telemetry agent unavailable** if ICMP or game-port evidence succeeds, not a claim of power loss. Restore `Start-Service SSC.Agent`; after three healthy responses, expect one recovery. Minecraft should remain untouched throughout.

For a complete simulated host outage, use a separate Windows VM with the Agent and a disposable watchdog configuration/Discord test channel. Do not disconnect production AM4 or forcibly power off either PC. Expect one unreachable alert, no repeats over multiple polling periods, then one recovery after three successes. Configure a unique data directory in an isolated VM/Windows test identity rather than sharing the production outbox.

## Windows acceptance not covered by Linux execution

Verify virtual-account service startup, PFX decryption/ACLs, performance counters/WMI access, firewall Private-profile scoping, actual Event Log writes, failure-restart actions, no-login startup after AM5 reboot, sleep-gap records, certificate renewal, WPF rendering/colors/keyboard/DPI behavior, and real Discord delivery/channel selection. Capture evidence and record failures without calling unexecuted tests passed.
