using Simple.MesGateway.Sources;

namespace Simple.MesGateway.Rounds;

/// <summary>
/// The result of the latest round, served whole: the gateway keeps no history
/// and does not compare one round with the next.
/// </summary>
/// <param name="Round">Counts every read since the gateway started, failed ones too; 0 before the first.</param>
/// <param name="ReadAt">When the read began, by the gateway's clock; null before the first round.</param>
/// <param name="Failure">Why the round failed; null when it succeeded.</param>
/// <param name="Rows">Every row the MES gave; null when the round failed, so "no data" never reads as "no rows".</param>
public sealed record MesSnapshot(
    long Round,
    DateTimeOffset? ReadAt,
    bool Succeeded,
    MesReadFailure? Failure,
    IReadOnlyList<MesRow>? Rows)
{
    public static MesSnapshot None { get; } = new(0, null, false, null, null);
}

/// <param name="Kind"><see cref="MesReadFailure.Read"/> or <see cref="MesReadFailure.Format"/>.</param>
public sealed record MesReadFailure(string Kind, string Message)
{
    /// <summary>The source could not be read.</summary>
    public const string Read = "read";

    /// <summary>The source answered, but the result does not have the agreed shape.</summary>
    public const string Format = "format";
}

/// <summary>Holds the latest snapshot for every reader.</summary>
public sealed class SnapshotStore
{
    private MesSnapshot latest = MesSnapshot.None;
    private long rounds;

    public MesSnapshot Latest => Volatile.Read(ref latest);

    public long NextRound() => Interlocked.Increment(ref rounds);

    public void Publish(MesSnapshot snapshot) => Volatile.Write(ref latest, snapshot);
}
