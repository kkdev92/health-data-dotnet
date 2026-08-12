namespace Kkdev92.HealthData.Tests;

public sealed class HealthDataApiMetadataTests
{
    [Fact]
    public void DefaultBaseAddressIsTheServiceEndpoint()
    {
        Assert.Equal(new Uri("https://health.googleapis.com/"), HealthDataApiMetadata.DefaultBaseAddress);
    }

    [Fact]
    public void DefaultBaseAddressEndsWithSlashSoRelativePathsResolveCorrectly()
    {
        // Without the trailing slash, new Uri(base, "v4/users/me/profile") would drop the last
        // segment of the base address. Generated endpoint paths are always relative.
        Assert.EndsWith("/", HealthDataApiMetadata.DefaultBaseAddress.AbsoluteUri, StringComparison.Ordinal);

        var resolved = new Uri(HealthDataApiMetadata.DefaultBaseAddress, "v4/users/me/profile");
        Assert.Equal("https://health.googleapis.com/v4/users/me/profile", resolved.AbsoluteUri);
    }

    [Fact]
    public void MetadataCarriesNoApiVersion()
    {
        // The runtime is version-neutral. The versioned path segment belongs to the generated
        // contract, so no "v4" may appear in the handwritten runtime.
        Assert.DoesNotContain("v4", HealthDataApiMetadata.DefaultBaseAddress.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivitySourceNameMatchesTheDocumentedName()
    {
        Assert.Equal("Kkdev92.HealthData", HealthDataApiMetadata.ActivitySourceName);
    }
}
