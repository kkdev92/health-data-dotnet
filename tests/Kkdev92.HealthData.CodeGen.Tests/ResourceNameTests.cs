using Kkdev92.HealthData.CodeGen.Discovery;
using Kkdev92.HealthData.CodeGen.IntermediateModel;
using Kkdev92.HealthData.CodeGen.Normalization;
using Kkdev92.HealthData.CodeGen.Specifications;
using Kkdev92.HealthData.CodeGen.Validation;

namespace Kkdev92.HealthData.CodeGen.Tests;

/// <summary>
/// The resource name types, and the claim that they are read out of the contract.
/// </summary>
/// <remarks>
/// Every <c>name</c> and <c>parent</c> parameter in Discovery carries a pattern, and the generator
/// used to render it as prose above a <c>string</c>. These tests hold the other reading: the
/// pattern says what the name is made of, so the type, its ids and its parent all come from the
/// expression rather than from a list somebody maintains.
/// </remarks>
public sealed class ResourceNameTests
{
    private static ApiContract Contract => DiscoveryParser.Parse(SpecLoader.Load(RepositoryRoot.Value, "v4"));

    [Fact]
    public void EveryNamePatternInTheContractBecomesAType()
    {
        var contract = Contract;

        var patterns = contract.Operations
            .SelectMany(operation => operation.Parameters)
            .Where(parameter => parameter.Pattern is not null)
            .Select(parameter => parameter.Pattern!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // 11 distinct patterns over 25 uses, in revision 20260826. The count is asserted rather
        // than derived so that a pattern arriving with a new revision is a failure to look at
        // rather than a silent extra type.
        Assert.Equal(11, patterns.Length);
        Assert.Equal(11, contract.ResourceNames.Count);

        Assert.Equal(
            [.. patterns.OrderBy(pattern => pattern, StringComparer.Ordinal)],
            [.. contract.ResourceNames.Select(name => name.Pattern).OrderBy(pattern => pattern, StringComparer.Ordinal)]);
    }

    [Theory]
    [InlineData("^users/[^/]+$", "UserName", null, "User", "userId")]
    [InlineData("^users/[^/]+/profile$", "ProfileName", "UserName", "Profile", null)]
    [InlineData("^users/[^/]+/settings$", "SettingsName", "UserName", "Settings", null)]
    [InlineData("^users/[^/]+/identity$", "IdentityName", "UserName", "Identity", null)]
    [InlineData("^users/[^/]+/irnProfile$", "IrnProfileName", "UserName", "IrnProfile", null)]
    [InlineData("^users/[^/]+/pairedDevices/[^/]+$", "PairedDeviceName", "UserName", "PairedDevice", "pairedDeviceId")]
    [InlineData("^users/[^/]+/dataTypes/[^/]+$", "DataTypeName", "UserName", "DataType", "dataTypeId")]
    [InlineData("^users/[^/]+/dataTypes/[^/]+/dataPoints/[^/]+$", "DataPointName", "DataTypeName", "DataPoint", "dataPointId")]
    [InlineData("^projects/[^/]+$", "ProjectName", null, "Project", "projectId")]
    [InlineData("^projects/[^/]+/subscribers/[^/]+$", "SubscriberName", "ProjectName", "Subscriber", "subscriberId")]
    [InlineData("^projects/[^/]+/subscribers/[^/]+/subscriptions/[^/]+$", "SubscriptionName", "SubscriberName", "Subscription", "subscriptionId")]
    public void EachPatternResolvesToTheTypeItDescribes(
        string pattern, string typeName, string? parent, string member, string? idParameter)
    {
        var name = Assert.Single(Contract.ResourceNames, candidate => candidate.Pattern == pattern);

        Assert.Equal(typeName, name.CSharpName);
        Assert.Equal(parent, name.ParentCSharpName);
        Assert.Equal(member, name.MemberName);
        Assert.Equal(idParameter, name.IdParameterName);
    }

    [Fact]
    public void TheParentIsTheNearestAncestorRatherThanTheRoot()
    {
        // users/{user}/dataTypes/{dataType}/dataPoints/{dataPoint} has two ancestors in the
        // contract. Descending from the user would make the data type id an argument of the leaf
        // and lose the type in between.
        var dataPoint = Assert.Single(Contract.ResourceNames, name => name.CSharpName == "DataPointName");

        Assert.Equal("DataTypeName", dataPoint.ParentCSharpName);
        Assert.Equal(["userId", "dataTypeId", "dataPointId"], dataPoint.IdParameterNames);
    }

    [Fact]
    public void ASingletonCarriesNoIdOfItsOwn()
    {
        var profile = Assert.Single(Contract.ResourceNames, name => name.CSharpName == "ProfileName");

        Assert.Null(profile.IdParameterName);
        Assert.Equal(["userId"], profile.IdParameterNames);
        Assert.Equal("users/{userId}/profile", profile.Example);
    }

    [Fact]
    public void ParentsAreResolvedBeforeTheirChildren()
    {
        // The emitter writes one file per name and refers to the parent type from the child. The
        // order is not required by the compiler, but a generated set where a parent appears after
        // its child reads as though the hierarchy were arbitrary.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in Contract.ResourceNames)
        {
            if (name.ParentCSharpName is { } parent)
            {
                Assert.Contains(parent, seen);
            }

            seen.Add(name.CSharpName);
        }
    }

    [Theory]
    [InlineData("users/[^/]+$")]
    [InlineData("^users/[^/]+")]
    [InlineData("^users/(me|[0-9]+)$")]
    [InlineData("^[^/]+/profile$")]
    public void APatternThisGeneratorCannotReadIsRejected(string pattern)
    {
        // Silence would be worse than the exception: a pattern with alternation or a leading id
        // would produce a type claiming a structure the service never agreed to, and the compile
        // error a caller then gets would be about the wrong thing entirely.
        Assert.Throws<InvalidOperationException>(() => ResourceNameResolver.Parse(pattern));
    }

    /// <summary>A contract with nothing in it but the resource names under test.</summary>
    private static ApiContract Crafted(
        IReadOnlyList<ResourceNameContract> names,
        IReadOnlyList<OperationContract>? operations = null)
        => new()
        {
            Name = "health",
            Title = "Test",
            ApiVersion = "v4",
            Revision = "test",
            RootUrl = new Uri("https://health.googleapis.com/"),
            SpecSha256 = "test",
            Scopes = [],
            Operations = operations ?? [],
            ResourceNames = names,
            DataTypes = [],
            Schemas = [],
            ErrorReasons = [],
            OpenEnums = [],
        };

    private static ResourceNameContract Name(
        string csharpName, string pattern, string member, string? parent = null, string? id = null)
        => new()
        {
            CSharpName = csharpName,
            Pattern = pattern,
            Segments = ResourceNameResolver.Parse(pattern),
            ParentCSharpName = parent,
            MemberName = member,
            IdParameterName = id,
            IdParameterNames = [.. ResourceNameResolver.Parse(pattern).Where(s => s.IsVariable).Select((_, i) => $"id{i}")],
            Example = pattern,
        };

    [Fact]
    public void TwoPatternsResolvingToOneTypeAreRejected()
    {
        // One type accepting two shapes of name is the hole these types exist to close. It cannot
        // happen from the current contract; a revision that added users/{u}/devices/{d} beside
        // pairedDevices would produce it, and silently.
        var errors = new List<string>();

        ContractValidator.ValidateResourceNames(
            Crafted(
            [
                Name("DeviceName", "^users/[^/]+/devices/[^/]+$", "Device", "UserName", "deviceId"),
                Name("DeviceName", "^users/[^/]+/gadgets/[^/]+$", "Gadget", "UserName", "gadgetId"),
            ]),
            errors);

        Assert.Contains(errors, e => e.Contains("DeviceName", StringComparison.Ordinal)
            && e.Contains("2 different", StringComparison.Ordinal));
    }

    [Fact]
    public void APatternWithNoTypeIsRejectedRatherThanEmittedAsAString()
    {
        var errors = new List<string>();

        ContractValidator.ValidateResourceNames(
            Crafted(
                [],
                [
                    new OperationContract
                    {
                        Id = "health.users.get",
                        ResourcePath = "users",
                        CSharpName = "Get",
                        HttpMethod = "GET",
                        PathTemplate = "v4/{+name}",
                        Parameters =
                        [
                            new ParameterContract
                            {
                                WireName = "name",
                                CSharpName = "Name",
                                Location = ParameterLocation.Path,
                                IsRequired = true,
                                Type = new TypeContract { Kind = TypeKind.Primitive, CSharpType = "string", WireType = "string" },
                                Pattern = "^users/[^/]+$",
                            },
                        ],
                        Scopes = [],
                        ResponseKind = ResponseKind.Json,
                        RetryClassification = RetryClassification.Safe,
                    },
                ]),
            errors);

        Assert.Contains(errors, e => e.Contains("unchecked string", StringComparison.Ordinal));
    }

    [Fact]
    public void AChildMemberThatWouldCollideWithTheTypesOwnApiIsRejected()
    {
        // A collection called "pattern" would emit PairedDeviceName.Pattern(id) beside the const
        // Pattern every name has, which is CS0102 in generated code.
        var errors = new List<string>();

        ContractValidator.ValidateResourceNames(
            Crafted(
            [
                Name("UserName", "^users/[^/]+$", "User"),
                Name("PatternName", "^users/[^/]+/patterns/[^/]+$", "Pattern", "UserName", "patternId"),
            ]),
            errors);

        Assert.Contains(errors, e => e.Contains("CS0102", StringComparison.Ordinal));
    }

    [Fact]
    public void TheContractItselfPassesEveryGuard()
    {
        var errors = new List<string>();

        ContractValidator.ValidateResourceNames(Contract, errors);

        Assert.Empty(errors);
    }

    [Fact]
    public void TheSegmentsAreTheOnesThePatternStates()
    {
        var segments = ResourceNameResolver.Parse("^users/[^/]+/pairedDevices/[^/]+$");

        Assert.Equal(4, segments.Count);
        Assert.Equal(new ResourceNameSegment("users", IsVariable: false), segments[0]);
        Assert.True(segments[1].IsVariable);
        Assert.Equal(new ResourceNameSegment("pairedDevices", IsVariable: false), segments[2]);
        Assert.True(segments[3].IsVariable);
    }

    /// <summary>
    /// A collection whose plural is not just a trailing <c>s</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not hypothetical. <c>dataSourceFamilies</c> is in this contract already — it appears in the
    /// description of <c>reconcile</c> and both roll-ups as
    /// <c>users/me/dataSourceFamilies/{data_source_family}</c> — and it is a resource with no
    /// operations of its own yet. A revision that gives it one arrives here.
    /// </para>
    /// <para>
    /// Stripping the trailing <c>s</c> produced <c>DataSourceFamilieName</c>: a type that compiles,
    /// ships, and reads as a typo in every call site that names it. The rule handles the two
    /// regular English plurals and nothing else, which is why the guard below exists.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("^users/[^/]+/dataSourceFamilies/[^/]+$", "DataSourceFamilyName", "dataSourceFamilyId")]
    [InlineData("^users/[^/]+/pairedDevices/[^/]+$", "PairedDeviceName", "pairedDeviceId")]
    [InlineData("^projects/[^/]+$", "ProjectName", "projectId")]
    public void ACollectionIsSingularizedByTheRulesEnglishActuallyHas(
        string pattern, string typeName, string idParameter)
    {
        var resolved = Assert.Single(ResourceNameResolver.Resolve(
        [
            new OperationContract
            {
                Id = "health.test.get",
                ResourcePath = "test",
                CSharpName = "Get",
                HttpMethod = "GET",
                PathTemplate = "v4/{+name}",
                Parameters =
                [
                    new ParameterContract
                    {
                        WireName = "name",
                        CSharpName = "Name",
                        Location = ParameterLocation.Path,
                        IsRequired = true,
                        Type = new TypeContract { Kind = TypeKind.Primitive, CSharpType = "string", WireType = "string" },
                        Pattern = pattern,
                    },
                ],
                Scopes = [],
                ResponseKind = ResponseKind.Json,
                RetryClassification = RetryClassification.Safe,
            },
        ]));

        Assert.Equal(typeName, resolved.CSharpName);
        Assert.Equal(idParameter, resolved.IdParameterName);
    }

    /// <summary>
    /// A collection segment that is not a plural at all is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>data</c> is a mass noun and <c>address</c> is singular; neither has an <c>s</c> to
    /// remove, so there is no rule to apply and the generator says so rather than naming a type
    /// after the word it was given.
    /// </para>
    /// <para>
    /// <strong>An irregular plural is not caught, and cannot be by any rule over the ending.</strong>
    /// <c>indices</c> would become <c>IndiceName</c> — wrong, and accepted. The reason is in this
    /// contract: <c>pairedDevices</c> ends in <c>ices</c> too and is a perfectly regular
    /// <c>device</c> plus <c>s</c>. Refusing that ending would refuse a resource this SDK already
    /// generates. What catches an irregular plural is a person reading the generated diff, which
    /// this repository commits for exactly that reason.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("^users/[^/]+/data/[^/]+$")]
    [InlineData("^users/[^/]+/address/[^/]+$")]
    public void ASegmentThatIsNotAPluralIsRefusedRatherThanNamedAfterItself(string pattern)
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => ResourceNameResolver.Parse(pattern));

        Assert.Contains("not a plural this generator can undo", thrown.Message, StringComparison.Ordinal);
    }
}
