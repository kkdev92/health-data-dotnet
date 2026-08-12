using System.Buffers;
using System.Text.Json;

namespace Kkdev92.HealthData;

/// <summary>
/// Parses the Google error envelope from a response body.
/// </summary>
/// <remarks>
/// Reads at most a configured number of bytes. An error body is not under our control and must
/// never be buffered without a bound. A body that is truncated,
/// empty, or not JSON yields <see langword="null"/> rather than an exception: the status code
/// already conveys the outcome, and failing to parse an error is not itself worth throwing over.
/// </remarks>
public static class HealthDataErrorParser
{
    /// <summary>Parses an error envelope from a stream, reading no more than <paramref name="maxBytes"/>.</summary>
    public static async Task<HealthDataError?> ParseAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var buffer = ArrayPool<byte>.Shared.Rent(maxBytes);

        try
        {
            var read = await stream
                .ReadAtLeastAsync(buffer.AsMemory(0, maxBytes), maxBytes, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);

            return read == 0 ? null : Parse(buffer.AsSpan(0, read));
        }
        finally
        {
            // clearArray: the body that was in here is an error from a health API, and Google
            // writes user ids and data types into those. Handing the array to the next renter
            // with the bytes still in it would put them somewhere nobody thought to look.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <summary>Parses an error envelope from UTF-8 bytes.</summary>
    public static HealthDataError? Parse(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());

            if (!document.RootElement.TryGetProperty("error", out var error))
            {
                return null;
            }

            return new HealthDataError
            {
                Code = error.TryGetProperty("code", out var code) && code.TryGetInt32(out var codeValue) ? codeValue : 0,
                Message = ReadString(error, "message"),
                Status = ReadString(error, "status"),
                Details = ReadDetails(error),
            };
        }
        catch (JsonException)
        {
            // Truncated by the byte bound, or simply not JSON.
            return null;
        }
    }

    private static IReadOnlyList<HealthDataErrorDetail> ReadDetails(JsonElement error)
    {
        if (!error.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<HealthDataErrorDetail>();

        foreach (var detail in details.EnumerateArray())
        {
            if (detail.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = ReadString(detail, "@type");

            result.Add(new HealthDataErrorDetail
            {
                Type = type,
                Reason = ReadString(detail, "reason"),
                Domain = ReadString(detail, "domain"),
                RetryDelay = GoogleDuration.TryParse(ReadString(detail, "retryDelay"), out var delay) ? delay : null,

                // Cloned so the value outlives the JsonDocument it came from.
                Raw = detail.Clone(),
            });
        }

        return result;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
