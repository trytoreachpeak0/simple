using System.Text.Json.Nodes;

namespace Simple.Cli.Scenarios;

/// <summary>
/// One scenario file (scenarios/README.md): the initial state of a local
/// system, the events that drive it and what is expected of the server's API
/// and event record. Read and checked by <see cref="ScenarioFile"/>.
/// </summary>
/// <param name="Name">The file's path under scenarios/ without .yaml, with '/'; a file elsewhere: its file name.</param>
public sealed record Scenario(
    string Name,
    string Path,
    string Title,
    string? Description,
    string Site,
    Verdict ExpectedVerdict,
    InitialState Initial,
    IReadOnlyList<ScenarioEvent> Events,
    IReadOnlyList<Expectation> Expectations);

/// <param name="Terminals">Vehicles with an onboard terminal, each with its IO simulator.</param>
/// <param name="FakeRiotSpeedup">FakeRiot:Timing:Speedup, or null for the fake's own default.</param>
/// <param name="FakeMesRows">The rows the fake MES starts with, or null for the gateway's seed rows.</param>
/// <param name="Cargo">Slots holding cargo when each terminal's IO simulator starts, by terminal number.</param>
public sealed record InitialState(
    int Terminals,
    double? FakeRiotSpeedup,
    IReadOnlyList<RiotVehicleChange> FakeRiotVehicles,
    IReadOnlyList<JsonObject>? FakeMesRows,
    IReadOnlyDictionary<int, IReadOnlyList<int>> Cargo);

/// <summary>A change to a vehicle on the fake RIoT, as its control API takes it; only the values given change.</summary>
public sealed record RiotVehicleChange(string DeviceKey, int? Battery, bool? BrakeReleased, string? Mode, bool? ManualCharger, bool? EmergencyButtonPressed);

/// <summary>One event: when it happens (<see cref="At"/> or <see cref="When"/>) and the one action it does.</summary>
/// <param name="Label">How the timeline and the result name it: its id, or "event N".</param>
/// <param name="Within">How long <see cref="When"/> is waited for.</param>
/// <param name="Line">Where it is in the file.</param>
public sealed record ScenarioEvent(
    string? Id,
    string Label,
    TimeSpan? At,
    Condition? When,
    TimeSpan Within,
    ScenarioAction Action,
    int Line);

/// <summary>An action, by its key in the file (add-mes-row, scan…), with its arguments as written.</summary>
public sealed record ScenarioAction(string Kind, JsonObject Arguments);

public enum ExpectationKind
{
    /// <summary>The condition holds at some look within the window.</summary>
    Eventually,

    /// <summary>The condition holds at every look throughout the window.</summary>
    Always,

    /// <summary>The condition holds at no look throughout the window.</summary>
    Never,
}

/// <param name="Label">How the timeline and the result name it: its id, or "expectation N".</param>
/// <param name="Window">How long the window stays open: within for eventually, for for always and never.</param>
/// <param name="After">The event whose action, as it starts, opens the window; null: the start of the scenario.</param>
/// <param name="OnlyIf">Judged when the window opens; when it does not hold, the expectation is not applicable.</param>
public sealed record Expectation(
    string? Id,
    string Label,
    string? Title,
    ExpectationKind Kind,
    Condition Condition,
    TimeSpan Window,
    string? After,
    Condition? OnlyIf,
    int Line);
