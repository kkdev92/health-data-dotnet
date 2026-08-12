namespace Kkdev92.HealthData.Http;

/// <summary>
/// Carries the operation descriptor along with an outgoing request.
/// </summary>
/// <remarks>
/// Delegating handlers need to know which operation they are looking at in order to select a
/// credential, decide whether a retry is allowed, or tag an activity. Reading it from a typed
/// request option is exact; re-parsing the URL would not be.
/// </remarks>
public static class HttpRequestMessageExtensions
{
    private static readonly HttpRequestOptionsKey<HealthDataOperationDescriptor> DescriptorKey =
        new("Kkdev92.HealthData.Operation");

    /// <summary>Attaches the operation descriptor to a request.</summary>
    public static void SetHealthDataOperation(
        this HttpRequestMessage request,
        HealthDataOperationDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(descriptor);

        request.Options.Set(DescriptorKey, descriptor);
    }

    /// <summary>Reads the operation descriptor from a request, if one was attached.</summary>
    public static HealthDataOperationDescriptor? GetHealthDataOperation(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Options.TryGetValue(DescriptorKey, out var descriptor) ? descriptor : null;
    }
}
