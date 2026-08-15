namespace Kkdev92.HealthData.Tests;

/// <summary>
/// The scopes, sorted into the three kinds a consumer actually branches on.
/// </summary>
/// <remarks>
/// <para>
/// An application that offers reads and gates writes has to decide which scopes to ask for, and the
/// only handle it had was the constant names or the URLs themselves. The sample built on this SDK
/// ended up matching on the string <c>".writeonly"</c> — a rule about Google's naming that nothing
/// in the package promised, in an application that would keep working right up until it did not.
/// </para>
/// <para>
/// These lists are generated from the contract, so the rule lives on the side that reads the
/// contract.
/// </para>
/// </remarks>
public sealed class ScopeClassificationTests
{
    [Fact]
    public void EveryScopeIsInExactlyOneList()
    {
        // 20: nine read, ten write, and cloud-platform. Discovery declares 19 of them and
        // semantics.json adds nutrition.readonly, which the REST reference documents and Discovery
        // revision 20260805 does not. Asserted rather than derived, so a scope Google adds is a
        // failure to look at rather than one silently missing from every list.
        var all = HealthDataScopes.All;

        Assert.Equal(20, all.Count);

        var classified = HealthDataScopes.ReadOnly
            .Concat(HealthDataScopes.WriteOnly)
            .Concat(HealthDataScopes.Project)
            .ToArray();

        Assert.Equal(all.Count, classified.Length);
        Assert.Equal([.. all.Order(StringComparer.Ordinal)], [.. classified.Order(StringComparer.Ordinal)]);
        Assert.Equal(classified.Length, classified.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheCountsAreTheOnesTheContractStates()
    {
        Assert.Equal(9, HealthDataScopes.ReadOnly.Count);
        Assert.Equal(10, HealthDataScopes.WriteOnly.Count);
        Assert.Equal(HealthDataScopes.CloudPlatform, Assert.Single(HealthDataScopes.Project));
    }

    [Fact]
    public void AReadScopeIsNotAWriteScope()
    {
        Assert.Contains(HealthDataScopes.SleepReadonly, HealthDataScopes.ReadOnly);
        Assert.DoesNotContain(HealthDataScopes.SleepReadonly, HealthDataScopes.WriteOnly);

        Assert.Contains(HealthDataScopes.SleepWriteonly, HealthDataScopes.WriteOnly);
        Assert.DoesNotContain(HealthDataScopes.SleepWriteonly, HealthDataScopes.ReadOnly);
    }

    [Fact]
    public void CloudPlatformIsNotCountedAsAReadScope()
    {
        // It reads on a consent screen as "see, edit, configure and delete your Google Cloud data",
        // so an application asking for read access must not end up asking for this one because a
        // name-based rule found no ".writeonly" in it.
        Assert.DoesNotContain(HealthDataScopes.CloudPlatform, HealthDataScopes.ReadOnly);
        Assert.DoesNotContain(HealthDataScopes.CloudPlatform, HealthDataScopes.WriteOnly);
    }
}
