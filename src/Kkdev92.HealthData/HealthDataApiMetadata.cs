namespace Kkdev92.HealthData;

/// <summary>
/// Version-neutral metadata about the Google Health API service.
/// </summary>
/// <remarks>
/// <para>
/// This type deliberately carries only values that do not change with the Google Health API
/// version. The runtime does not special-case <c>v4</c>; the versioned path segment belongs to
/// the generated contract instead.
/// </para>
/// <para>
/// The API version and Discovery revision are emitted by the code generator from
/// <c>spec/&lt;version&gt;/metadata.json</c>, so that they cannot drift away from the committed
/// specification snapshot.
/// </para>
/// </remarks>
public static class HealthDataApiMetadata
{
    /// <summary>
    /// The default service endpoint, <c>https://health.googleapis.com/</c>.
    /// </summary>
    /// <remarks>
    /// The trailing slash is significant: generated endpoint paths are relative
    /// (for example <c>v4/users/me/profile</c>) and are resolved against this address.
    /// </remarks>
    public static Uri DefaultBaseAddress { get; } = new("https://health.googleapis.com/");

    /// <summary>
    /// The name of the <see cref="System.Diagnostics.ActivitySource"/> used by this SDK.
    /// </summary>
    public const string ActivitySourceName = "Kkdev92.HealthData";
}
