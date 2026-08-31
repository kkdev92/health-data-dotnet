namespace Kkdev92.HealthData.Tests;

/// <summary>
/// The floor under <see cref="HealthDataClientOptions.BaseAddress"/>.
/// </summary>
/// <remarks>
/// Every request the client sends to this address carries a bearer token for somebody's health
/// record. The property exists to be overridden, which is exactly why it needs a floor, and the
/// floor is worth pinning: it was the least-covered code in the assembly.
/// </remarks>
public sealed class HealthDataClientOptionsTests
{
    [Fact]
    public void TheDefaultIsGoogle()
        => Assert.Equal(HealthDataApiMetadata.DefaultBaseAddress, new HealthDataClientOptions().BaseAddress);

    [Theory]
    [InlineData("https://emulator.example.test/")]
    [InlineData("http://localhost:8080/")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://[::1]:8080/")]
    public void HttpsAnywhereAndPlainHttpToLoopbackAreAccepted(string address)
    {
        var options = new HealthDataClientOptions { BaseAddress = new Uri(address) };

        Assert.Equal(new Uri(address), options.BaseAddress);
    }

    [Theory]
    [InlineData("http://example.test/")]          // plaintext to a real host: the token would go in the clear
    [InlineData("ftp://localhost/")]              // IsLoopback is true here too, and the scheme is not HTTP
    [InlineData("file://localhost/")]
    public void AnAddressThatWouldPutTheTokenOnTheWireInTheClearIsRefused(string address)
    {
        var exception = Assert.Throws<ArgumentException>(() => new HealthDataClientOptions
        {
            BaseAddress = new Uri(address),
        });

        Assert.Equal(nameof(HealthDataClientOptions.BaseAddress), exception.ParamName);
        Assert.Contains("not HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelativeAddressIsRefused()
    {
        var exception = Assert.Throws<ArgumentException>(() => new HealthDataClientOptions
        {
            BaseAddress = new Uri("v4/", UriKind.Relative),
        });

        Assert.Contains("absolute", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullAddressIsRefused()
        => Assert.Throws<ArgumentNullException>(() => new HealthDataClientOptions { BaseAddress = null! });

    [Fact]
    public void TheRefusalNamesTheHostButNotACredentialPutInTheAddress()
    {
        // The misconfiguration this message complains about is precisely the one where somebody
        // has put a secret into the URI. Objecting to it must not repeat it.
        var exception = Assert.Throws<ArgumentException>(() => new HealthDataClientOptions
        {
            BaseAddress = new Uri("http://user:hunter2@example.test:8443/api?token=abc#frag"),
        });

        Assert.Contains("http://example.test:8443", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token=abc", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("frag", exception.Message, StringComparison.Ordinal);
    }
}
