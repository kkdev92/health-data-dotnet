using Kkdev92.HealthData;
using Kkdev92.HealthData.Authentication;
using Kkdev92.HealthData.Models;
using Kkdev92.HealthData.Names;
using Kkdev92.HealthData.Requests;

// Reads a user's step data.
//
//   PowerShell    $env:GOOGLEHEALTH_ACCESS_TOKEN = "ya29...."
//   bash / zsh    export GOOGLEHEALTH_ACCESS_TOKEN=ya29....
//   cmd.exe       set GOOGLEHEALTH_ACCESS_TOKEN=ya29....
//
//   dotnet run --project samples/ReadSteps.Console
//
// The token comes from the environment on purpose. This SDK ships no token store, and a sample
// that wrote one to disk would be the wrong thing to copy.
// See docs/authentication.md for how to obtain one.
//
// One scope, because the sample does one thing. Asking for more than the code uses is the habit
// that turns a consent screen into a list nobody reads.

var accessToken = Environment.GetEnvironmentVariable("GOOGLEHEALTH_ACCESS_TOKEN");

if (string.IsNullOrWhiteSpace(accessToken))
{
    Console.Error.WriteLine("Set GOOGLEHEALTH_ACCESS_TOKEN first. See docs/authentication.md.");
    Console.Error.WriteLine($"The token needs {HealthDataScopes.ActivityAndFitnessReadonly}.");
    return 2;
}

// The authorization handler resolves a token per request from the operation descriptor, so one
// client is safe to share. Here there is only ever one user, so a static provider will do.
var authorization = new HealthDataAuthorizationHandler(new StaticAccessTokenProvider(accessToken))
{
    InnerHandler = new HttpClientHandler(),
};

using var httpClient = new HttpClient(authorization)
{
    BaseAddress = HealthDataApiMetadata.DefaultBaseAddress,
};

var client = new HealthDataClient(httpClient);

using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));

try
{
    // The raw list call returns one page. EnumerateAsync pages lazily on top of it, so this
    // stops after 20 items instead of pulling an entire history.
    var yesterday = DateTimeOffset.UtcNow.AddDays(-1);

    // The prefix is the data type's snake_case filter name, and it is not the kebab-case id in
    // the resource name below. Steps hides the difference, because for steps they are the same
    // word: heart rate is dataTypes/heart-rate in the URL and heart_rate.interval.start_time in
    // the filter. A bare start_time is never right. docs/data-points.md has the table.
    var filter = $"steps.interval.start_time >= \"{new GoogleTimestamp(yesterday)}\"";

    Console.WriteLine($"steps since {yesterday:yyyy-MM-dd HH:mm}Z");

    var shown = 0;

    await foreach (var point in client.Users.DataPoints.EnumerateAsync(
        new ListDataPointsRequest
        {
            Parent = UserName.Me.DataType("steps"),
            Filter = filter,
            PageSize = 100,
        },
        cancellation.Token))
    {
        if (point.Steps is { Count: { } count })
        {
            var at = point.Steps.Interval?.StartTime?.ToString() ?? "(no interval)";
            Console.WriteLine($"  {at}  {count,8:N0} steps");
        }

        if (++shown >= 20)
        {
            Console.WriteLine("... stopping after 20 (nothing further was fetched)");
            break;
        }
    }

    if (shown == 0)
    {
        Console.WriteLine("  (no data points in range)");
    }

    return 0;
}
catch (HealthDataApiException ex)
{
    // The message carries the operation, status and reason, never the payload.
    Console.Error.WriteLine(ex.Message);

    if (ex.Reason == HealthDataErrorReasons.MissingOauthScope)
    {
        Console.Error.WriteLine($"The token is missing {HealthDataScopes.ActivityAndFitnessReadonly}.");
    }
    else if (ex.Reason == HealthDataErrorReasons.AccountNotLinked)
    {
        Console.Error.WriteLine("This Google account has no linked Google Health data.");
    }

    return 1;
}
