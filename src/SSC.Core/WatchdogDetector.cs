namespace SSC.Core;

// Pure state machine. The caller commits state, evidence and alerts atomically before delivery.
public sealed class WatchdogDetector(DetectionOptions options)
{
    public IReadOnlyList<PendingAlert> Apply(WatchdogState state, ProbeSample sample, bool restarted = false)
    {
        options.Validate();
        var alerts = new List<PendingAlert>();
        var now = sample.ObservedAt;
        if (state.LastCheck is { } previous && (restarted || now < previous || now - previous > TimeSpan.FromSeconds(options.IntervalSeconds * 1.5)))
        {
            var missed = Math.Max(0, (int)Math.Round((now - previous).TotalSeconds / options.IntervalSeconds) - 1);
            state.Gaps.Add(new(previous, now, missed, restarted ? "Watchdog restarted; continuity unverified" : "Monitoring delay / sleep / clock adjustment; continuity unverified"));
            for (var i = 0; i < state.Incidents.Count; i++)
                if (state.Incidents[i].RecoveredAt is null) state.Incidents[i] = state.Incidents[i] with { ObservationInterrupted = true };
            state.Failures = state.Successes = state.MinecraftFailures = state.MinecraftSuccesses = 0;
            state.ComputerFailureSince = state.MinecraftFailureSince = null; state.LastProcessRunning = null;
        }
        state.LastCheck = now; state.LastEvidence = sample.Evidence; state.HeartbeatLatencyMs = sample.HeartbeatLatencyMs;
        if (sample.HeartbeatOk)
        {
            state.Failures = 0; state.ComputerFailureSince = null; state.Successes = Math.Min(state.Successes + 1, options.RecoveryThreshold);
            state.LastHeartbeat = sample.Heartbeat?.ObservedAt ?? now; state.LastAgentHeartbeat = sample.Heartbeat;
            if (state.Successes >= options.RecoveryThreshold)
            {
                if (state.ComputerIncident is { } incidentId)
                {
                    Recover(incidentId, "AM4 COMPUTER RECOVERED", "HEARTBEAT: RESTORED");
                    state.ComputerIncident = null; state.EscalationSent = false;
                }
                state.Computer = ComputerCondition.Online;
            }
        }
        else
        {
            state.ComputerFailureSince ??= now;
            state.Successes = 0; state.Failures = Math.Min(state.Failures + 1, options.FailureThreshold);
            if (state.Failures >= options.FailureThreshold)
            {
                var condition = sample.NetworkReachable || sample.MinecraftPortOpen ? ComputerCondition.AgentUnavailable : ComputerCondition.Unreachable;
                // Reclassify an open incident without generating repeated initial notifications.
                if (state.ComputerIncident is null)
                    state.ComputerIncident = Open(condition == ComputerCondition.Unreachable ? "AM4 SERVER UNREACHABLE" : "AM4 REACHABLE — TELEMETRY AGENT UNAVAILABLE", condition == ComputerCondition.Unreachable
                        ? "Possible power failure, Windows crash, freeze, or network outage. Cause is not established."
                        : "Network evidence exists; agent endpoint/authentication/health requires investigation.");
                else if (!state.EscalationSent && condition == ComputerCondition.Unreachable)
                    { Alert("AM4 SERVER UNREACHABLE — ESCALATION", "Existing agent incident escalated; all reachability probes now fail. Cause is unknown."); state.EscalationSent = true; }
                if (condition == ComputerCondition.Unreachable) state.EscalationSent = true;
                state.Computer = condition;
            }
        }
        // Never infer Minecraft stopped from a missing agent. Keep confirmed state but label current evidence unknown in the dashboard.
        var process = sample.HeartbeatOk ? sample.Heartbeat?.MinecraftRunning : null;
        var candidate = process switch
        {
            false => MinecraftCondition.ProcessStopped,
            true when !sample.MinecraftPortOpen || sample.MinecraftResponsive is false => MinecraftCondition.PotentiallyUnresponsive,
            true => MinecraftCondition.Running,
            _ when sample.HeartbeatOk && !sample.MinecraftPortOpen => MinecraftCondition.PortUnavailable,
            _ => MinecraftCondition.Unknown
        };
        if (candidate == MinecraftCondition.Unknown)
        {
            state.MinecraftFailureSince = null; state.MinecraftFailures = state.MinecraftSuccesses = 0; state.Candidate = candidate;
        }
        else if (candidate == MinecraftCondition.Running)
        {
            state.MinecraftFailureSince = null; state.MinecraftFailures = 0; state.Candidate = candidate;
            state.MinecraftSuccesses = Math.Min(state.MinecraftSuccesses + 1, options.RecoveryThreshold);
            if (state.MinecraftSuccesses >= options.RecoveryThreshold)
            {
                if (state.MinecraftIncident is { } id) { Recover(id, "MINECRAFT SERVER RECOVERED", "Process running, TCP port accepting connections and configured status check not failing; application tick health not verified."); state.MinecraftIncident = null; }
                state.Minecraft = candidate;
            }
        }
        else
        {
            state.MinecraftSuccesses = 0;
            if (state.Candidate != candidate) state.MinecraftFailureSince = now;
            state.MinecraftFailureSince ??= now;
            state.MinecraftFailures = state.Candidate == candidate ? Math.Min(state.MinecraftFailures + 1, options.FailureThreshold) : 1;
            state.Candidate = candidate;
            if (state.MinecraftFailures >= options.FailureThreshold)
            {
                if (state.MinecraftIncident is null)
                    state.MinecraftIncident = Open(candidate switch
                    {
                        MinecraftCondition.ProcessStopped => "MINECRAFT PROCESS STOPPED",
                        MinecraftCondition.PortUnavailable => "MINECRAFT TCP PORT UNAVAILABLE",
                        _ => "MINECRAFT POTENTIALLY UNRESPONSIVE"
                    }, candidate == MinecraftCondition.ProcessStopped ? "Process stopped; intentional shutdown versus crash is unknown." : "TCP or Minecraft status probe failed; startup, disabled status replies, firewall or application stall are possible. Tick responsiveness is not verified.");
                state.Minecraft = candidate;
            }
        }
        // Count observed running->stopped transitions, not every stopped sample. No invented crashes while AM5 was offline.
        if (process is false && state.LastProcessRunning is true)
        {
            state.CrashTimes.Add(now);
            state.CrashTimes.RemoveAll(t => now - t > TimeSpan.FromMinutes(options.CrashWindowMinutes));
            if (state.CrashTimes.Count >= options.CrashBurstCount &&
                (state.LastCrashBurstAlert is null || now - state.LastCrashBurstAlert >= TimeSpan.FromMinutes(options.CrashWindowMinutes)))
            { Alert("REPEATED MINECRAFT STOPS", $"{state.CrashTimes.Count} observed process-stop transitions in {options.CrashWindowMinutes} minutes. Crash vs intentional stop unknown."); state.LastCrashBurstAlert = now; }
        }
        if (process.HasValue) state.LastProcessRunning = process;
        return alerts;

        Guid Open(string kind, string explanation)
        {
            var id = Guid.NewGuid();
            var started = kind.StartsWith("AM4", StringComparison.Ordinal) ? state.ComputerFailureSince ?? now : state.MinecraftFailureSince ?? now;
            state.Incidents.Add(new(id, kind, started, now, null, sample.Evidence));
            Alert(kind, explanation); return id;
        }
        void Recover(Guid id, string kind, string detail)
        {
            var index = state.Incidents.FindIndex(i => i.Id == id);
            if (index < 0) throw new InvalidDataException("Active incident missing from state.");
            var incident = state.Incidents[index] with { RecoveredAt = now };
            state.Incidents[index] = incident;
            Alert(kind, $"{detail}\nINCIDENT ELAPSED: {now - incident.StartedAt:c}\nCONTINUITY: {(incident.ObservationInterrupted ? "INTERRUPTED; includes unobserved time" : "observed checks at configured intervals")}");
        }
        void Alert(string kind, string detail) => alerts.Add(new(Guid.NewGuid(), now, kind,
            $"SHADOW SERVER COMMAND — {(kind.Contains("RECOVERED") ? "SYSTEM RESTORED" : "CRITICAL ALERT")}\nALERT: {kind}\nSYSTEM: AM4 Minecraft Server\nLAST CONTACT: {state.LastHeartbeat?.ToString("O") ?? "UNKNOWN"}\nDETECTED: {now:O}\nHEARTBEAT: {(sample.HeartbeatOk ? "RESTORED" : "FAILED")}\nMINECRAFT: {(processDescription())}\nEVIDENCE: {sample.Evidence}\nSTATUS: {(kind.Contains("RECOVERED") ? "RECOVERY CONFIRMED" : "INVESTIGATION REQUIRED")}\n{detail}"));
        string processDescription() => !sample.HeartbeatOk ? "UNKNOWN" : sample.Heartbeat?.MinecraftRunning switch { true => "PROCESS RUNNING", false => "PROCESS STOPPED", _ => "UNKNOWN" };
    }
}
