using System.Text.Json;
using System.Text.Json.Serialization;
using Kkdev92.HealthData.Http;

namespace Kkdev92.HealthData.Authentication.OAuth;

/// <summary>The token endpoint's response.</summary>
/// <remarks>Field names are Google's wire names and are not reshaped.</remarks>
public sealed class GoogleTokenResponse
{
    /// <summary>The access token.</summary>
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    /// <summary>Lifetime in seconds.</summary>
    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; init; }

    /// <summary>
    /// The refresh token.
    /// </summary>
    /// <remarks>
    /// Returned only when the authorization request included <c>access_type=offline</c>, and
    /// usually only on the first consent. Losing it means sending the user through consent again.
    /// </remarks>
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    /// <summary>The scopes actually granted, space-separated.</summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    /// <summary>The token type, normally <c>Bearer</c>.</summary>
    [JsonPropertyName("token_type")]
    public string? TokenType { get; init; }
}

[JsonSerializable(typeof(GoogleTokenResponse))]
[JsonSerializable(typeof(GoogleOAuthError))]
internal sealed partial class OAuthJsonContext : JsonSerializerContext;

/// <summary>
/// The OAuth 2.0 authorization code flow against Google.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately small: build an authorization URL, exchange a code, refresh a token. There is no
/// token store, no background refresh loop and no browser automation. Those belong to the
/// application.
/// </para>
/// <para>
/// Endpoints and parameters verified against the Google Health API setup guide on 2026-08-10.
/// </para>
/// </remarks>
public sealed class GoogleOAuthClient(HttpClient httpClient, GoogleOAuthOptions options)
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    private readonly GoogleOAuthOptions _options = GoogleOAuthOptions.RequireGoogleOrLocalEndpoints(
        options ?? throw new ArgumentNullException(nameof(options)));

    /// <summary>
    /// Builds the URL to send the user to for consent.
    /// </summary>
    /// <param name="options">What to ask for. See <see cref="GoogleAuthorizationUrlOptions"/>.</param>
    /// <remarks>
    /// One options object rather than six parameters. Two of them were <see langword="bool"/>, and
    /// <c>CreateAuthorizationUrl(scopes, state, pkce, true, false)</c> reads as nothing at all at
    /// the call site — while getting them the wrong way round produces a grant with no refresh
    /// token, which fails hours later and somewhere else.
    /// </remarks>
    public Uri CreateAuthorizationUrl(GoogleAuthorizationUrlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Scopes);

        var scopes = options.Scopes;
        var state = options.State;
        var pkce = options.Pkce;
        var offlineAccess = options.OfflineAccess;
        var forceConsent = options.ForceConsent;
        var loginHint = options.LoginHint;

        var parameters = new List<KeyValuePair<string, string>>
        {
            new("client_id", _options.ClientId),

            // OriginalString, not ToString(): Uri normalises, and Google compares this to the
            // registration character for character. ToString() adds a trailing slash to a bare
            // origin and decodes percent-escapes, either of which turns a correct registration
            // into redirect_uri_mismatch.
            new("redirect_uri", _options.RedirectUri.OriginalString),
            new("response_type", "code"),

            // Space-separated, as the setup guide's example shows.
            new("scope", string.Join(' ', scopes)),
        };

        if (offlineAccess)
        {
            parameters.Add(new KeyValuePair<string, string>("access_type", "offline"));
        }

        if (forceConsent)
        {
            parameters.Add(new KeyValuePair<string, string>("prompt", "consent"));
        }

        if (!string.IsNullOrEmpty(state))
        {
            parameters.Add(new KeyValuePair<string, string>("state", state));
        }

        if (pkce is not null)
        {
            parameters.Add(new KeyValuePair<string, string>("code_challenge", pkce.CodeChallenge));
            parameters.Add(new KeyValuePair<string, string>("code_challenge_method", PkceCodeChallenge.CodeChallengeMethod));
        }

        if (!string.IsNullOrEmpty(loginHint))
        {
            parameters.Add(new KeyValuePair<string, string>("login_hint", loginHint));
        }

        var query = string.Join('&', parameters.Select(p =>
            $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

        return new Uri($"{_options.AuthorizationEndpoint}?{query}");
    }

    /// <summary>Exchanges an authorization code for tokens.</summary>
    /// <param name="code">The code returned to the redirect URI.</param>
    /// <param name="pkce">The same challenge used on the authorization request, if any.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public Task<GoogleTokenResponse> ExchangeCodeAsync(
        string code,
        PkceCodeChallenge? pkce = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code),
            new("client_id", _options.ClientId),

            // The same string that was sent on the authorization request, for the same reason.
            new("redirect_uri", _options.RedirectUri.OriginalString),
        };

        if (pkce is not null)
        {
            form.Add(new KeyValuePair<string, string>("code_verifier", pkce.CodeVerifier));
        }

        if (!string.IsNullOrEmpty(_options.ClientSecret))
        {
            form.Add(new KeyValuePair<string, string>("client_secret", _options.ClientSecret));
        }

        return PostAsync(form, cancellationToken);
    }

    /// <summary>Exchanges a refresh token for a new access token.</summary>
    /// <remarks>
    /// The response usually omits <c>refresh_token</c>: the original stays valid. In testing mode
    /// Google expires refresh tokens after seven days, which is a common source of confusion
    /// before an app is verified.
    /// </remarks>
    public Task<GoogleTokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
            new("client_id", _options.ClientId),
        };

        if (!string.IsNullOrEmpty(_options.ClientSecret))
        {
            form.Add(new KeyValuePair<string, string>("client_secret", _options.ClientSecret));
        }

        return PostAsync(form, cancellationToken);
    }

    /// <summary>Converts a token response into the type the SDK's pipeline consumes.</summary>
    public static HealthDataAccessToken ToAccessToken(GoogleTokenResponse response, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (string.IsNullOrEmpty(response.AccessToken))
        {
            throw new InvalidOperationException("The token response contained no access token.");
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();

        return new HealthDataAccessToken(
            response.AccessToken,
            response.ExpiresIn is { } seconds ? now.AddSeconds(seconds) : null,
            response.Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? []);
    }

    /// <summary>The largest token endpoint response this will read, in bytes.</summary>
    /// <remarks>
    /// A token response is a few hundred bytes and an RFC 6749 error is smaller. This is a ceiling
    /// rather than an expectation: the endpoint is configurable, the response arrives over the
    /// network, and a client that buffers whatever turns up has no answer to one that does not
    /// stop.
    /// </remarks>
    private const int MaximumResponseBytes = 64 * 1024;

    private async Task<GoogleTokenResponse> PostAsync(
        IEnumerable<KeyValuePair<string, string>> form,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint) { Content = content };

        // ResponseHeadersRead, so the body is read by ReadBodyAsync under its limit rather than
        // buffered whole by HttpClient before this method is given the response at all.
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new GoogleOAuthException(
                response.StatusCode,
                await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false));
        }

        var token = JsonSerializer.Deserialize(
            await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false),
            OAuthJsonContext.Default.GoogleTokenResponse);

        return token ?? throw new InvalidOperationException("The token endpoint returned an empty response.");
    }

    /// <summary>
    /// Reads the RFC 6749 section 5.2 error response, if the server sent one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A failing authorization server is under no obligation to return well-formed JSON, and an
    /// exception raised while explaining another exception helps nobody — so anything unreadable
    /// becomes "no details" and the status code still gets reported.
    /// </para>
    /// <para>
    /// Cancellation is not one of those things. It used to read the error body without the
    /// caller's token, so a caller that gave up went on waiting for an explanation it had already
    /// stopped wanting; a cancelled read now surfaces as cancellation rather than as "no details".
    /// </para>
    /// </remarks>
    private static async Task<GoogleOAuthError?> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return JsonSerializer.Deserialize(
                await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false),
                OAuthJsonContext.Default.GoogleOAuthError);
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or HttpRequestException
                or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Reads a response body, refusing one that is implausibly large.</summary>
    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Read before the body is: once it has been buffered, Content-Length answers with the
        // real size rather than what the server declared.
        var declared = response.Content.Headers.ContentLength;

        var body = await BoundedBody
            .ReadAsync(response.Content, MaximumResponseBytes, cancellationToken)
            .ConfigureAwait(false);

        // A response that declared more than this was refused before a byte of it arrived; one
        // that merely turned out to be larger declared nothing, or declared something untrue.
        return body ?? throw new InvalidOperationException(
            declared > MaximumResponseBytes
                ? $"The token endpoint declared more than {MaximumResponseBytes} bytes."
                : $"The token endpoint returned more than {MaximumResponseBytes} bytes.");
    }
}
