using System.Text.Json;
using System.Text.Json.Serialization;
using Kkdev92.HealthData.Serialization;

namespace Kkdev92.HealthData.Webhooks;

/// <summary>
/// A notification delivered to a subscriber endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Handwritten, not generated. The notification payload has no schema in the Discovery document,
/// so there is nothing to generate it from. The similarly named
/// <c>...WebhookNotificationCloudLog</c> in Discovery is a Cloud Logging record, not this.
/// </para>
/// <para>
/// Shape verified against the Webhooks guide on 2026-08-10.
/// </para>
/// </remarks>
public sealed class HealthDataNotification
{
    /// <summary>The notification payload.</summary>
    [JsonPropertyName("data")]
    public HealthDataNotificationData? Data { get; init; }
}

/// <summary>The body of a notification.</summary>
public sealed class HealthDataNotificationData
{
    /// <summary>The payload version, currently <c>"1"</c>.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>The subscription name the client supplied when creating the subscription.</summary>
    [JsonPropertyName("clientProvidedSubscriptionName")]
    public string? ClientProvidedSubscriptionName { get; init; }

    /// <summary>
    /// The Google Health user id whose data changed.
    /// </summary>
    /// <remarks>
    /// A user identifier. Do not log it.
    /// </remarks>
    [JsonPropertyName("healthUserId")]
    public string? HealthUserId { get; init; }

    /// <summary>
    /// What happened: <c>UPSERT</c> for any addition or modification, <c>DELETE</c> when a user
    /// deletes data.
    /// </summary>
    /// <remarks>
    /// A string rather than an enum, for the same reason wire enums are open elsewhere: a value
    /// added later must not break an existing receiver (ADR-0005).
    /// </remarks>
    [JsonPropertyName("operation")]
    public string? Operation { get; init; }

    /// <summary>The data type identifier, for example <c>steps</c>.</summary>
    [JsonPropertyName("dataType")]
    public string? DataType { get; init; }

    /// <summary>The time ranges affected.</summary>
    [JsonPropertyName("intervals")]
    public IReadOnlyList<HealthDataNotificationInterval>? Intervals { get; init; }

    /// <summary>True when this notification reports an addition or modification.</summary>
    public bool IsUpsert => string.Equals(Operation, HealthDataNotificationOperations.Upsert, StringComparison.Ordinal);

    /// <summary>True when this notification reports a deletion.</summary>
    public bool IsDelete => string.Equals(Operation, HealthDataNotificationOperations.Delete, StringComparison.Ordinal);
}

/// <summary>Known values of <see cref="HealthDataNotificationData.Operation"/>.</summary>
public static class HealthDataNotificationOperations
{
    /// <summary>Any data addition or modification.</summary>
    public const string Upsert = "UPSERT";

    /// <summary>A user deleted data.</summary>
    public const string Delete = "DELETE";
}

/// <summary>One affected time range, given three ways.</summary>
public sealed class HealthDataNotificationInterval
{
    /// <summary>The range as instants in UTC.</summary>
    [JsonPropertyName("physicalTimeInterval")]
    public HealthDataPhysicalTimeInterval? PhysicalTimeInterval { get; init; }

    /// <summary>The range as structured civil date and time.</summary>
    [JsonPropertyName("civilDateTimeInterval")]
    public HealthDataCivilDateTimeInterval? CivilDateTimeInterval { get; init; }

    /// <summary>The range as ISO 8601 civil timestamps without an offset.</summary>
    [JsonPropertyName("civilIso8601TimeInterval")]
    public HealthDataCivilIso8601TimeInterval? CivilIso8601TimeInterval { get; init; }
}

/// <summary>A range of instants.</summary>
public sealed class HealthDataPhysicalTimeInterval
{
    /// <summary>The inclusive start.</summary>
    [JsonPropertyName("startTime")]
    [JsonConverter(typeof(GoogleTimestampConverter))]
    public GoogleTimestamp? StartTime { get; init; }

    /// <summary>The exclusive end.</summary>
    [JsonPropertyName("endTime")]
    [JsonConverter(typeof(GoogleTimestampConverter))]
    public GoogleTimestamp? EndTime { get; init; }
}

/// <summary>A range of structured civil date-times.</summary>
public sealed class HealthDataCivilDateTimeInterval
{
    /// <summary>The inclusive start.</summary>
    [JsonPropertyName("startDateTime")]
    public HealthDataCivilDateTime? StartDateTime { get; init; }

    /// <summary>The exclusive end.</summary>
    [JsonPropertyName("endDateTime")]
    public HealthDataCivilDateTime? EndDateTime { get; init; }
}

/// <summary>A civil date and time, with no offset.</summary>
public sealed class HealthDataCivilDateTime
{
    /// <summary>The calendar date.</summary>
    [JsonPropertyName("date")]
    public HealthDataCivilDate? Date { get; init; }

    /// <summary>The wall-clock time.</summary>
    [JsonPropertyName("time")]
    public HealthDataCivilTime? Time { get; init; }
}

/// <summary>A calendar date.</summary>
public sealed class HealthDataCivilDate
{
    /// <summary>The year.</summary>
    [JsonPropertyName("year")]
    public int? Year { get; init; }

    /// <summary>The month, 1 to 12.</summary>
    [JsonPropertyName("month")]
    public int? Month { get; init; }

    /// <summary>The day of month.</summary>
    [JsonPropertyName("day")]
    public int? Day { get; init; }
}

/// <summary>A wall-clock time.</summary>
public sealed class HealthDataCivilTime
{
    /// <summary>The hour, 0 to 23.</summary>
    [JsonPropertyName("hours")]
    public int? Hours { get; init; }

    /// <summary>The minute.</summary>
    [JsonPropertyName("minutes")]
    public int? Minutes { get; init; }

    /// <summary>The second.</summary>
    [JsonPropertyName("seconds")]
    public int? Seconds { get; init; }
}

/// <summary>A range of ISO 8601 civil timestamps.</summary>
public sealed class HealthDataCivilIso8601TimeInterval
{
    /// <summary>The inclusive start, for example <c>2026-03-07T17:29:00</c>.</summary>
    [JsonPropertyName("startTime")]
    public string? StartTime { get; init; }

    /// <summary>The exclusive end.</summary>
    [JsonPropertyName("endTime")]
    public string? EndTime { get; init; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(HealthDataNotification))]
internal sealed partial class WebhookJsonContext : JsonSerializerContext;
