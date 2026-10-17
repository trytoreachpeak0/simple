namespace Simple.Cli.Scenarios;

/// <summary>
/// The four outcomes of a check (CLAUDE.md, "Verdicts and repair"). Cannot
/// tell and not applicable are kept apart on purpose, and cannot tell never
/// counts as a pass: each has its own exit code.
/// </summary>
public enum Verdict
{
    Holds,
    DoesNotHold,
    CannotTell,
    NotApplicable,
}

public static class Verdicts
{
    /// <summary>How the file, the result and the timeline spell it.</summary>
    public static string Name(this Verdict verdict) => verdict switch
    {
        Verdict.Holds => "holds",
        Verdict.DoesNotHold => "does-not-hold",
        Verdict.CannotTell => "cannot-tell",
        _ => "not-applicable",
    };

    public static Verdict Parse(string name) => name switch
    {
        "holds" => Verdict.Holds,
        "does-not-hold" => Verdict.DoesNotHold,
        "cannot-tell" => Verdict.CannotTell,
        "not-applicable" => Verdict.NotApplicable,
        _ => throw new ArgumentException($"'{name}' is not a verdict.", nameof(name)),
    };

    /// <summary>simple scenario run ends with this exit code.</summary>
    public static int ExitCode(this Verdict verdict) => verdict switch
    {
        Verdict.Holds => ExitCodes.Success,
        Verdict.DoesNotHold => ExitCodes.DoesNotHold,
        Verdict.CannotTell => ExitCodes.CannotTell,
        _ => ExitCodes.NotApplicable,
    };

    /// <summary>
    /// A scenario's verdict from its expectations' and whether it could be
    /// driven as written: one that does not hold decides; otherwise one that
    /// cannot be told, or a scenario that could not be driven, cannot be
    /// told; otherwise it holds if any expectation applied, and is not
    /// applicable if none did.
    /// </summary>
    public static Verdict Combine(IEnumerable<Verdict> expectations, bool driven)
    {
        ArgumentNullException.ThrowIfNull(expectations);
        var all = expectations.ToList();
        return all.Contains(Verdict.DoesNotHold) ? Verdict.DoesNotHold
            : all.Contains(Verdict.CannotTell) || !driven || all.Count == 0 ? Verdict.CannotTell
            : all.Contains(Verdict.Holds) ? Verdict.Holds
            : Verdict.NotApplicable;
    }
}
