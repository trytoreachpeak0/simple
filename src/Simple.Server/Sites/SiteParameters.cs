namespace Simple.Server.Sites;

/// <summary>
/// The durations, counts and battery levels of the parameter configuration
/// (docs/rules-overview.md, summary table two), each with its ADR default.
/// A site file sets only the ones it changes. The onboard terminal's own
/// reconnect cap (25 s) is not here: a terminal that is not connected cannot
/// read the configuration (ADR 0025). Dispatch zones carry their own demand
/// order policy and wait cap (<see cref="DispatchZone"/>), fixed stations
/// their own "all loaded" countdown (<see cref="FixedStation"/>).
/// </summary>
public sealed record SiteParameters
{
    /// <summary>Departure countdown, nothing loaded yet (ADR 0005).</summary>
    public TimeSpan DepartureNothingLoaded { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Departure countdown, a sublot loaded halfway (ADR 0005).</summary>
    public TimeSpan DepartureLoadingHalfway { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Departure countdown, between two sublots (ADR 0005).</summary>
    public TimeSpan DepartureBetweenSublots { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Departure countdown, all loaded: the whole site's value (ADR 0005, 0035).</summary>
    public TimeSpan DepartureAllLoaded { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>No progress at a station before the alarm (ADR 0005, 0012).</summary>
    public TimeSpan NoProgressAlarm { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>A lock reading older than this is not fresh (ADR 0009).</summary>
    public TimeSpan LockReadingStaleAfter { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Disconnected this long, an onboard terminal fault (ADR 0009).</summary>
    public TimeSpan TerminalFaultAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>A class-3 stop triggered again within this after SIMPLE released it counts as someone else's (ADR 0009).</summary>
    public TimeSpan ClassThreeRetriggerWindow { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Wait before an automatic recovery: rebuild after failed or cancelled, back online (ADR 0008, 0010, 0011).</summary>
    public TimeSpan AutoRecoveryWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Hung this long without resuming, a fault (ADR 0010).</summary>
    public TimeSpan HangFaultAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Still hung this long after the resume command, a fault (ADR 0010).</summary>
    public TimeSpan ResumeFaultAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A cancelled foreign order not ended after this, a fault (ADR 0011).</summary>
    public TimeSpan ForeignOrderFaultAfter { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Orders cancelled on entering a fault not read back as ended within this, an alarm (ADR 0012).</summary>
    public TimeSpan FaultCancelReadBackAlarm { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The brake's window: the third automatic recovery of one object within it stops (ADR 0008).</summary>
    public TimeSpan BrakeWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The brake's count (ADR 0008).</summary>
    public int BrakeCount { get; init; } = 3;

    /// <summary>The longest wait between retries: reading the MES, reaching the fleet manager, reading the IO module (ADR 0008).</summary>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Reading the MES or reaching the fleet manager failing this long, an alarm (ADR 0008).</summary>
    public TimeSpan UnreachableAlarmAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>No new round from the MES gateway this long, by the server's clock, an MES fault (ADR 0027).</summary>
    public TimeSpan MesStaleAfter { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>The IO module unreadable this long, an IO module fault (ADR 0008).</summary>
    public TimeSpan IoModuleFaultAfter { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A vehicle offline this long, a vehicle body fault (ADR 0008).</summary>
    public TimeSpan VehicleOfflineFaultAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Held by traffic control this long, a fleet manager fault (ADR 0008).</summary>
    public TimeSpan TrafficBlockedFaultAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The longest wait between resends of an unconfirmed emergency stop (ADR 0009).</summary>
    public TimeSpan EmergencyStopResendMaxDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>An alarm that is not a fault goes up to the team leader after this (ADR 0013).</summary>
    public TimeSpan AlarmEscalateAfter { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Urgent charge level, in percent (ADR 0020).</summary>
    public int UrgentChargeLevel { get; init; } = 20;

    /// <summary>Charge-needed level, in percent (ADR 0020).</summary>
    public int ChargeNeededLevel { get; init; } = 35;

    /// <summary>Ready level, in percent (ADR 0020).</summary>
    public int ReadyLevel { get; init; } = 60;

    /// <summary>Full level, in percent (ADR 0020).</summary>
    public int FullLevel { get; init; } = 99;

    /// <summary>Charging that rises less than <see cref="ChargingStallRise"/> within this has stalled (ADR 0020).</summary>
    public TimeSpan ChargingStallWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>In percentage points (ADR 0020).</summary>
    public int ChargingStallRise { get; init; } = 3;

    /// <summary>How many days the event record keeps (ADR 0019); MCP may not change it.</summary>
    public int EventRetentionDays { get; init; } = 180;

    /// <summary>An unlock request older than this is not executed (ADR 0024).</summary>
    public TimeSpan UnlockRequestMaxAge { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>An onboard input older than this is not taken (ADR 0024).</summary>
    public TimeSpan OnboardInputMaxAge { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>No fresh desired state for this long, buttons grey out; the console reads it too (ADR 0024, 0030).</summary>
    public TimeSpan ButtonsGreyAfter { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>No fresh desired state for this long, "disconnected from the server"; the console reads it too (ADR 0024, 0030).</summary>
    public TimeSpan DisconnectedAfter { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Every parameter by the name a site file gives it, in summary table
    /// two's order: what reading a site file and validating go through.
    /// </summary>
    public static IReadOnlyList<SiteParameter> All { get; } =
    [
        new("departure-nothing-loaded", ParameterKind.Duration, p => p.DepartureNothingLoaded, (p, v) => p with { DepartureNothingLoaded = (TimeSpan)v }),
        new("departure-loading-halfway", ParameterKind.Duration, p => p.DepartureLoadingHalfway, (p, v) => p with { DepartureLoadingHalfway = (TimeSpan)v }),
        new("departure-between-sublots", ParameterKind.Duration, p => p.DepartureBetweenSublots, (p, v) => p with { DepartureBetweenSublots = (TimeSpan)v }),
        new("departure-all-loaded", ParameterKind.Duration, p => p.DepartureAllLoaded, (p, v) => p with { DepartureAllLoaded = (TimeSpan)v }),
        new("no-progress-alarm", ParameterKind.Duration, p => p.NoProgressAlarm, (p, v) => p with { NoProgressAlarm = (TimeSpan)v }),
        new("lock-reading-stale-after", ParameterKind.Duration, p => p.LockReadingStaleAfter, (p, v) => p with { LockReadingStaleAfter = (TimeSpan)v }),
        new("terminal-fault-after", ParameterKind.Duration, p => p.TerminalFaultAfter, (p, v) => p with { TerminalFaultAfter = (TimeSpan)v }),
        new("class-three-retrigger-window", ParameterKind.Duration, p => p.ClassThreeRetriggerWindow, (p, v) => p with { ClassThreeRetriggerWindow = (TimeSpan)v }),
        new("auto-recovery-wait", ParameterKind.Duration, p => p.AutoRecoveryWait, (p, v) => p with { AutoRecoveryWait = (TimeSpan)v }),
        new("hang-fault-after", ParameterKind.Duration, p => p.HangFaultAfter, (p, v) => p with { HangFaultAfter = (TimeSpan)v }),
        new("resume-fault-after", ParameterKind.Duration, p => p.ResumeFaultAfter, (p, v) => p with { ResumeFaultAfter = (TimeSpan)v }),
        new("foreign-order-fault-after", ParameterKind.Duration, p => p.ForeignOrderFaultAfter, (p, v) => p with { ForeignOrderFaultAfter = (TimeSpan)v }),
        new("fault-cancel-read-back-alarm", ParameterKind.Duration, p => p.FaultCancelReadBackAlarm, (p, v) => p with { FaultCancelReadBackAlarm = (TimeSpan)v }),
        new("brake-window", ParameterKind.Duration, p => p.BrakeWindow, (p, v) => p with { BrakeWindow = (TimeSpan)v }),
        new("brake-count", ParameterKind.Count, p => p.BrakeCount, (p, v) => p with { BrakeCount = (int)v }),
        new("retry-max-delay", ParameterKind.Duration, p => p.RetryMaxDelay, (p, v) => p with { RetryMaxDelay = (TimeSpan)v }),
        new("unreachable-alarm-after", ParameterKind.Duration, p => p.UnreachableAlarmAfter, (p, v) => p with { UnreachableAlarmAfter = (TimeSpan)v }),
        new("mes-stale-after", ParameterKind.Duration, p => p.MesStaleAfter, (p, v) => p with { MesStaleAfter = (TimeSpan)v }),
        new("io-module-fault-after", ParameterKind.Duration, p => p.IoModuleFaultAfter, (p, v) => p with { IoModuleFaultAfter = (TimeSpan)v }),
        new("vehicle-offline-fault-after", ParameterKind.Duration, p => p.VehicleOfflineFaultAfter, (p, v) => p with { VehicleOfflineFaultAfter = (TimeSpan)v }),
        new("traffic-blocked-fault-after", ParameterKind.Duration, p => p.TrafficBlockedFaultAfter, (p, v) => p with { TrafficBlockedFaultAfter = (TimeSpan)v }),
        new("emergency-stop-resend-max-delay", ParameterKind.Duration, p => p.EmergencyStopResendMaxDelay, (p, v) => p with { EmergencyStopResendMaxDelay = (TimeSpan)v }),
        new("alarm-escalate-after", ParameterKind.Duration, p => p.AlarmEscalateAfter, (p, v) => p with { AlarmEscalateAfter = (TimeSpan)v }),
        new("urgent-charge-level", ParameterKind.Level, p => p.UrgentChargeLevel, (p, v) => p with { UrgentChargeLevel = (int)v }),
        new("charge-needed-level", ParameterKind.Level, p => p.ChargeNeededLevel, (p, v) => p with { ChargeNeededLevel = (int)v }),
        new("ready-level", ParameterKind.Level, p => p.ReadyLevel, (p, v) => p with { ReadyLevel = (int)v }),
        new("full-level", ParameterKind.Level, p => p.FullLevel, (p, v) => p with { FullLevel = (int)v }),
        new("charging-stall-window", ParameterKind.Duration, p => p.ChargingStallWindow, (p, v) => p with { ChargingStallWindow = (TimeSpan)v }),
        new("charging-stall-rise", ParameterKind.Count, p => p.ChargingStallRise, (p, v) => p with { ChargingStallRise = (int)v }),
        new("event-retention-days", ParameterKind.Count, p => p.EventRetentionDays, (p, v) => p with { EventRetentionDays = (int)v }),
        new("unlock-request-max-age", ParameterKind.Duration, p => p.UnlockRequestMaxAge, (p, v) => p with { UnlockRequestMaxAge = (TimeSpan)v }),
        new("onboard-input-max-age", ParameterKind.Duration, p => p.OnboardInputMaxAge, (p, v) => p with { OnboardInputMaxAge = (TimeSpan)v }),
        new("buttons-grey-after", ParameterKind.Duration, p => p.ButtonsGreyAfter, (p, v) => p with { ButtonsGreyAfter = (TimeSpan)v }),
        new("disconnected-after", ParameterKind.Duration, p => p.DisconnectedAfter, (p, v) => p with { DisconnectedAfter = (TimeSpan)v }),
    ];
}

/// <summary>A duration, a count (at least 1) or a battery level (a percentage, ADR 0020).</summary>
public enum ParameterKind
{
    Duration,
    Count,
    Level,
}

/// <param name="Name">As a site file writes it, for example <c>departure-all-loaded</c>.</param>
public sealed record SiteParameter(string Name, ParameterKind Kind, Func<SiteParameters, object> Get, Func<SiteParameters, object, SiteParameters> With);
