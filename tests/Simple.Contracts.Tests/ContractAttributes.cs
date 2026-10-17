using Xunit.v3;

namespace Simple.Contracts.Tests;

/// <summary>
/// Names where a contract comes from: a contract of the RIoT behaviour lab
/// (<c>BC-ORDER-004</c>), a lab round or question (<c>round 24</c>,
/// <c>Q-033</c>), or a field record of the previous line (<c>cs#404</c>).
/// Becomes the trait <c>Contract</c>, one per source, so
/// <c>--filter-trait "Contract=BC-ORDER-004"</c> selects its tests.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ContractAttribute(params string[] sources) : Attribute, ITraitAttribute
{
    public IReadOnlyList<string> Sources { get; } = sources;

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        Sources.Select(source => new KeyValuePair<string, string>("Contract", source)).ToList();
}

/// <summary>
/// The behaviour is someone's statement or an inference, not something seen
/// on real RIoT. The fake does it so that the code built on it can be
/// tested; whether real RIoT does it is checked under #11. Becomes the trait
/// <c>RealRiot=NotVerified</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class NotVerifiedOnRealRiotAttribute(string statement) : Attribute, ITraitAttribute
{
    public string Statement { get; } = statement;

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        [new("RealRiot", "NotVerified"), new("Statement", Statement)];
}
