namespace Simple.MesGateway.Sources;

/// <summary>
/// One row of the MES transport query, its seven columns as the MES gave
/// them. Values are not checked here: which values a demand needs (an AREA of
/// the right shape, a date in DATES) is the server's business.
/// </summary>
public sealed record MesRow(
    string? TaskType,
    string? Sublot,
    string? Area,
    string? Eqp,
    string? Step,
    string? Dates,
    string? Package);

/// <summary>The columns of the previous line's main MES query, which the gateway's rows carry.</summary>
public static class MesColumns
{
    public const string TaskType = "TASK_TYPE";
    public const string Sublot = "SUBLOT";
    public const string Area = "AREA";
    public const string Eqp = "EQP";
    public const string Step = "STEP";
    public const string Dates = "DATES";
    public const string Package = "PACKAGE";

    public static IReadOnlyList<string> All { get; } = [TaskType, Sublot, Area, Eqp, Step, Dates, Package];
}
