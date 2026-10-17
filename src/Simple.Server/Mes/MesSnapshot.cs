namespace Simple.Server.Mes;

// The MES gateway's GET /api/v1/snapshot, as the server reads it. The
// gateway's own types are in Simple.MesGateway (Rounds/MesSnapshot.cs,
// Sources/MesRow.cs); the smoke test runs both processes against each other.

/// <param name="Round">Counts every read since the gateway started; 0 means it has not read yet.</param>
/// <param name="Rows">Null when the round failed.</param>
public sealed record MesSnapshot(
    long Round,
    DateTimeOffset? ReadAt,
    bool Succeeded,
    MesReadFailure? Failure,
    IReadOnlyList<MesRow>? Rows);

/// <param name="Kind">"read" (the MES could not be read) or "format" (it answered in the wrong shape).</param>
public sealed record MesReadFailure(string Kind, string Message);

/// <summary>One row of the MES transport query: TASK_TYPE, SUBLOT, AREA, EQP, STEP, DATES, PACKAGE, as the MES gave them.</summary>
public sealed record MesRow(
    string? TaskType,
    string? Sublot,
    string? Area,
    string? Eqp,
    string? Step,
    string? Dates,
    string? Package);
