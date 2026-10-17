using System.Reflection;

namespace Simple.Contracts.Tests;

/// <summary>
/// The contracts as a catalogue: every one names its source, and every one
/// that stands for an item of #11 names the ADR that rests on it, so a result
/// on real RIoT leads straight to the decision to revisit.
/// </summary>
public sealed class ContractCatalogueTests
{
    private static readonly IReadOnlyList<MethodInfo> Contracts = typeof(ContractTest).Assembly.GetTypes()
        .Where(t => t.IsSubclassOf(typeof(ContractTest)) && !t.IsAbstract)
        .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        .Where(m => m.GetCustomAttributes<FactAttribute>().Any())
        .ToList();

    [Fact]
    public void EveryContractNamesItsSource()
    {
        Assert.NotEmpty(Contracts);
        Assert.All(Contracts, m => Assert.True(
            m.GetCustomAttribute<ContractAttribute>() is { Sources.Count: > 0 },
            $"{m.DeclaringType!.Name}.{m.Name} 没有写出处（[Contract]）。"));
    }

    [Fact]
    public void EveryContractForAnItemOf11NamesTheAdrThatRestsOnIt()
    {
        var from11 = Contracts.Where(m => m.GetCustomAttribute<ContractAttribute>()?.Sources.Contains("#11") == true).ToList();

        Assert.NotEmpty(from11);
        Assert.All(from11, m => Assert.True(
            m.GetCustomAttribute<ContractAttribute>()!.Sources.Any(s => s.StartsWith("ADR ", StringComparison.Ordinal)),
            $"{m.DeclaringType!.Name}.{m.Name} 来自 #11，却没写依赖它的 ADR。"));
    }
}
