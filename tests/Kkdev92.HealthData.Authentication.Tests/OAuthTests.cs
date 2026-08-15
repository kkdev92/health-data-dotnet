using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Kkdev92.HealthData.Authentication.OAuth;

namespace Kkdev92.HealthData.Authentication.Tests;

/// <summary>
/// Pins the OAuth request shapes verified against Google's Health API setup guide on 2026-08-10.
/// </summary>
public sealed class OAuthTests
{
    private static GoogleOAuthOptions Options => new()
    {
        ClientId = "client-123.apps.googleusercontent.com",
        RedirectUri = new Uri("https://example.test/callback"),
    };

    private static GoogleOAuthClient CreateClient(HttpMessageHandler? handler = null)
        => new(new HttpClient(handler ?? new StubTokenHandler()), Options);

    private sealed class StubTokenHandler : HttpMessageHandler
    {
        public IReadOnlyDictionary<string, string>? LastForm { get; private set; }

        public Uri? LastUri { get; private set; }

        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        /// <summary>The body to answer with, when the default success response will not do.</summary>
        public string? Body { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var parsed = HttpUtility.ParseQueryString(body);
            LastForm = parsed.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => parsed[k]!, StringComparer.Ordinal);

            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(
                    Body ??
                    """
                    {"access_token":"ya29.token","expires_in":3599,"refresh_token":"1//refresh",
                     "scope":"https://www.googleapis.com/auth/googlehealth.profile.readonly","token_type":"Bearer"}
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    /// <summary>
    /// Answers with more than the client is willing to read, with or without declaring how much.
    /// </summary>
    /// <remarks>
    /// Both paths matter: the declared length is a cheap early refusal, and a server that declares
    /// nothing — which chunked transfer encoding is — has to be caught while reading instead.
    /// </remarks>
    private sealed class OversizedHandler(int bytes, bool declareLength, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Well-formed as both a token response and an RFC 6749 error, so that refusing to read
            // it is the only reason either path could come back with nothing.
            var payload = Encoding.UTF8.GetBytes(
                $$"""{"error":"invalid_grant","access_token":"{{new string('a', bytes)}}"}""");

            HttpContent content = declareLength
                ? new ByteArrayContent(payload)
                : new StreamContent(new UndeclaredLengthStream(payload));

            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    /// <summary>A stream that will not say how long it is, the way a chunked response does not.</summary>
    private sealed class UndeclaredLengthStream(byte[] payload) : Stream
    {
        private readonly MemoryStream _inner = new(payload);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Cancels the caller's token, then answers — so the outstanding work is the body read.</summary>
    private sealed class CancellingHandler(CancellationTokenSource source, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            source.Cancel();

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>
    /// A token response larger than the limit is refused rather than buffered.
    /// </summary>
    /// <remarks>
    /// The token endpoint is configurable, so this is not only about trusting Google: whatever is
    /// at the other end can answer with as much as it likes, and reading all of it into memory is
    /// a decision rather than a default.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnOversizedTokenResponseIsRefused(bool declareLength)
    {
        using var handler = new OversizedHandler(200 * 1024, declareLength);
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RefreshAsync("1//refresh", TestContext.Current.CancellationToken));

        Assert.Contains("bytes", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>An oversized error body becomes "no details", not an oversized allocation.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnOversizedErrorBodyIsRefusedAndTheStatusStillReported(bool declareLength)
    {
        using var handler = new OversizedHandler(200 * 1024, declareLength, HttpStatusCode.BadRequest);
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<GoogleOAuthException>(() =>
            client.RefreshAsync("1//refresh", TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Null(exception.ErrorCode);
        Assert.DoesNotContain("aaaa", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A caller that cancels while the error is being read gets cancellation, not an exception.
    /// </summary>
    /// <remarks>
    /// The error read used to be started without the caller's token, so the one path that runs
    /// when something has already gone wrong was also the one path that ignored being stopped.
    /// </remarks>
    [Fact]
    public async Task CancellingWhileTheErrorIsReadCancels()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new CancellingHandler(cancellation, HttpStatusCode.BadRequest);
        var client = CreateClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.RefreshAsync("1//refresh", cancellation.Token));
    }

    /// <summary>
    /// Nothing the server echoed back reaches the exception message.
    /// </summary>
    /// <remarks>
    /// RFC 6749 lets a server put anything in <c>error_description</c>. A server, or a proxy in
    /// front of one, that reflects a submitted value would otherwise have printed an authorization
    /// code, a PKCE verifier, a refresh token or a client secret into whatever log the message
    /// reaches — and the documentation tells people to paste that message into a bug report.
    /// </remarks>
    [Theory]
    [InlineData("4/0AXve-authorization-code-echoed-back")]
    [InlineData("1//refresh-token-echoed-back")]
    [InlineData("GOCSPX-client-secret-echoed-back")]
    [InlineData("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")]
    public async Task AReflectedSecretDoesNotReachTheMessage(string reflected)
    {
        var body = $$"""{"error":"invalid_grant","error_description":"Bad Request: {{reflected}}"}""";

        using var handler = new StubTokenHandler { Status = HttpStatusCode.BadRequest, Body = body };
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<GoogleOAuthException>(() =>
            client.RefreshAsync(reflected, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(reflected, exception.Message, StringComparison.Ordinal);

        // The code is still there, because that is the part a caller can act on.
        Assert.Contains("invalid_grant", exception.Message, StringComparison.Ordinal);

        // And nothing was thrown away: the server's own words stay on Error.
        Assert.Contains(reflected, exception.Error!.ErrorDescription!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A secret reflected into <c>error</c> itself does not reach the message either.
    /// </summary>
    /// <remarks>
    /// The first version of this guard checked the shape of the code — sixty-four characters of
    /// ASCII letters, digits, underscore, hyphen and dot. A Google client secret, an authorization
    /// code and a base64url bearer token all fit inside that, because a secret is shaped exactly
    /// like an identifier. The test that went with it only ever reflected into
    /// <c>error_description</c>, so the hole it left was invisible.
    /// </remarks>
    [Theory]
    [InlineData("GOCSPX-client-secret-echoed-back")]
    [InlineData("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")]
    [InlineData("1//refresh-token-echoed-back")]
    [InlineData("ya29.a0Ae4-access-token")]
    public async Task ASecretReflectedIntoTheErrorCodeDoesNotReachTheMessage(string reflected)
    {
        using var handler = new StubTokenHandler
        {
            Status = HttpStatusCode.BadRequest,
            Body = $$"""{"error":"{{reflected}}"}""",
        };

        var exception = await Assert.ThrowsAsync<GoogleOAuthException>(() =>
            CreateClient(handler).RefreshAsync(reflected, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(reflected, exception.Message, StringComparison.Ordinal);
        Assert.EndsWith("returned 400 BadRequest.", exception.Message, StringComparison.Ordinal);

        // Nor through ToString, which is what a logging framework reaches for.
        Assert.DoesNotContain(reflected, exception.Error!.ToString(), StringComparison.Ordinal);

        // Still available to code that wants to look at it deliberately.
        Assert.Equal(reflected, exception.ErrorCode);
    }

    /// <summary>Every code the specifications define still reaches the message.</summary>
    /// <remarks>
    /// The allowlist is only worth having if it does not cost the codes a caller came for.
    /// </remarks>
    [Theory]
    [InlineData("invalid_request")]
    [InlineData("invalid_client")]
    [InlineData("invalid_grant")]
    [InlineData("unauthorized_client")]
    [InlineData("unsupported_grant_type")]
    [InlineData("invalid_scope")]
    [InlineData("access_denied")]
    [InlineData("unsupported_response_type")]
    [InlineData("server_error")]
    [InlineData("temporarily_unavailable")]
    [InlineData("authorization_pending")]
    [InlineData("slow_down")]
    [InlineData("expired_token")]
    public async Task ADefinedErrorCodeIsReported(string code)
    {
        using var handler = new StubTokenHandler
        {
            Status = HttpStatusCode.BadRequest,
            Body = $$"""{"error":"{{code}}","error_description":"anything at all"}""",
        };

        var exception = await Assert.ThrowsAsync<GoogleOAuthException>(() =>
            CreateClient(handler).RefreshAsync("1//refresh", TestContext.Current.CancellationToken));

        Assert.Contains(code, exception.Message, StringComparison.Ordinal);
        Assert.Equal(code, exception.Error!.ToString());
        Assert.DoesNotContain("anything at all", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>An error code the specifications do not define is left out rather than printed.</summary>
    [Theory]
    [InlineData("not a code: 1//refresh-token")]
    [InlineData("invalid grant with spaces")]
    [InlineData("<script>alert(1)</script>")]
    public async Task AnErrorCodeThatIsNotACodeIsLeftOut(string code)
    {
        var escaped = code.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

        using var handler = new StubTokenHandler
        {
            Status = HttpStatusCode.BadRequest,
            Body = $$"""{"error":"{{escaped}}"}""",
        };

        var exception = await Assert.ThrowsAsync<GoogleOAuthException>(() =>
            CreateClient(handler).RefreshAsync("1//refresh", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(code, exception.Message, StringComparison.Ordinal);
        Assert.EndsWith("returned 400 BadRequest.", exception.Message, StringComparison.Ordinal);

        // Still reachable for a caller that wants it.
        Assert.Equal(code, exception.ErrorCode);
    }

    [Fact]
    public void AuthorizationUrlUsesGooglesEndpointAndParameters()
    {
        var url = CreateClient().CreateAuthorizationUrl(new GoogleAuthorizationUrlOptions
        {
            Scopes = [HealthDataScopes.ProfileReadonly, HealthDataScopes.SleepReadonly],
            State = "xyz",
        });

        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url.ToString(), StringComparison.Ordinal);

        var query = HttpUtility.ParseQueryString(url.Query);

        Assert.Equal("client-123.apps.googleusercontent.com", query["client_id"]);
        Assert.Equal("https://example.test/callback", query["redirect_uri"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("xyz", query["state"]);

        // The setup guide's example sends access_type=offline to obtain a refresh token.
        Assert.Equal("offline", query["access_type"]);

        // Scopes are space separated.
        Assert.Equal(
            $"{HealthDataScopes.ProfileReadonly} {HealthDataScopes.SleepReadonly}",
            query["scope"]);
    }

    [Fact]
    public void OfflineAccessAndForcedConsentAreOptional()
    {
        var url = CreateClient().CreateAuthorizationUrl(new GoogleAuthorizationUrlOptions
        {
            Scopes = [HealthDataScopes.ProfileReadonly],
            OfflineAccess = false,
            ForceConsent = true,
        });

        var query = HttpUtility.ParseQueryString(url.Query);

        Assert.Null(query["access_type"]);

        // The guide names prompt=consent as the way to re-request after changing scopes.
        Assert.Equal("consent", query["prompt"]);
    }

    [Fact]
    public void AuthorizationUrlCarriesThePkceChallenge()
    {
        var pkce = PkceCodeChallenge.Create();
        var url = CreateClient().CreateAuthorizationUrl(new GoogleAuthorizationUrlOptions
        {
            Scopes = [HealthDataScopes.ProfileReadonly],
            Pkce = pkce,
        });
        var query = HttpUtility.ParseQueryString(url.Query);

        Assert.Equal(pkce.CodeChallenge, query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);

        // The verifier is the secret and must never appear on the authorization request.
        Assert.DoesNotContain(pkce.CodeVerifier, url.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodeExchangePostsTheExpectedForm()
    {
        var handler = new StubTokenHandler();
        var pkce = PkceCodeChallenge.Create();

        var response = await CreateClient(handler).ExchangeCodeAsync("auth-code", pkce, TestContext.Current.CancellationToken);

        Assert.Equal("https://oauth2.googleapis.com/token", handler.LastUri!.ToString());
        Assert.Equal("authorization_code", handler.LastForm!["grant_type"]);
        Assert.Equal("auth-code", handler.LastForm["code"]);
        Assert.Equal(pkce.CodeVerifier, handler.LastForm["code_verifier"]);
        Assert.Equal("https://example.test/callback", handler.LastForm["redirect_uri"]);

        // A public client sends no secret.
        Assert.False(handler.LastForm.ContainsKey("client_secret"));

        Assert.Equal("ya29.token", response.AccessToken);
        Assert.Equal("1//refresh", response.RefreshToken);
    }

    [Fact]
    public async Task RefreshPostsTheRefreshGrant()
    {
        var handler = new StubTokenHandler();

        await CreateClient(handler).RefreshAsync("1//refresh", TestContext.Current.CancellationToken);

        Assert.Equal("refresh_token", handler.LastForm!["grant_type"]);
        Assert.Equal("1//refresh", handler.LastForm["refresh_token"]);
        Assert.False(handler.LastForm.ContainsKey("code"));
    }

    /// <summary>
    /// The server's explanation is the point of RFC 6749 section 5.2 — it exists to tell the
    /// client developer what is wrong with their own configuration, and there is no health data
    /// in it.
    /// </summary>
    [Fact]
    public async Task TokenEndpointFailureReportsTheServersError()
    {
        var handler = new StubTokenHandler
        {
            Status = HttpStatusCode.BadRequest,
            Body = """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""",
        };

        var exception = await Assert.ThrowsAsync<GoogleOAuthException>(() =>
            CreateClient(handler).RefreshAsync("1//stale", TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("invalid_grant", exception.ErrorCode);
        Assert.Contains("invalid_grant", exception.Message, StringComparison.Ordinal);

        // The description is not in the message any more. It is the field a server can reflect an
        // authorization code into, and the message is the field the documentation tells people to
        // paste into a public issue. Both of those stay true only if they are different fields.
        Assert.DoesNotContain("expired or revoked", exception.Message, StringComparison.Ordinal);
        Assert.Equal("Token has been expired or revoked.", exception.Error!.ErrorDescription);
    }

    /// <summary>
    /// The property that has to survive explaining more: nothing the caller sent comes back out.
    /// </summary>
    [Fact]
    public async Task TokenEndpointFailureNeverEchoesTheRequest()
    {
        var handler = new StubTokenHandler
        {
            Status = HttpStatusCode.BadRequest,
            Body = """{"error":"invalid_request","error_description":"Malformed request."}""",
        };

        var exception = await Assert.ThrowsAsync<GoogleOAuthException>(() =>
            CreateClient(handler).ExchangeCodeAsync(
                "bad-code",
                PkceCodeChallenge.Create(),
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain("bad-code", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ya29", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("client-123", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failing server owes nothing, least of all valid JSON. The status still has to arrive.
    /// </summary>
    [Fact]
    public async Task TokenEndpointFailureSurvivesAnUnreadableBody()
    {
        var handler = new StubTokenHandler { Status = HttpStatusCode.BadGateway, Body = "<html>502</html>" };

        var exception = await Assert.ThrowsAsync<GoogleOAuthException>(() =>
            CreateClient(handler).RefreshAsync("1//refresh", TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Null(exception.ErrorCode);
    }

    /// <summary>
    /// Nothing an authorization server writes reaches a log by accident.
    /// </summary>
    /// <remarks>
    /// This used to assert that the description was filtered and truncated on its way into the
    /// message and into <c>ToString</c>. Filtering was the wrong control: it stops a newline
    /// forging a log line and a server filling a log, and it does nothing about a server echoing
    /// back the refresh token it was just sent. Neither surface carries the description at all
    /// now, so what has to be pinned is that — including through <c>ToString</c>, which is what a
    /// logging framework calls without being asked.
    /// </remarks>
    [Fact]
    public async Task NothingTheServerChoseTheWordsForReachesTheMessageOrToString()
    {
        var handler = new StubTokenHandler
        {
            Status = HttpStatusCode.BadRequest,
            Body = $$"""{"error":"invalid_request","error_description":"{{new string('x', 400)}}\nINJECTED 1//refresh-token","error_uri":"https://example.test/1//refresh-token"}""",
        };

        var exception = await Assert.ThrowsAsync<GoogleOAuthException>(() =>
            CreateClient(handler).RefreshAsync("1//refresh-token", TestContext.Current.CancellationToken));

        foreach (var surface in new[] { exception.Message, exception.Error!.ToString() })
        {
            Assert.DoesNotContain("INJECTED", surface, StringComparison.Ordinal);
            Assert.DoesNotContain("1//refresh-token", surface, StringComparison.Ordinal);
            Assert.DoesNotContain("example.test", surface, StringComparison.Ordinal);
            Assert.DoesNotContain("xxxx", surface, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', surface);
        }

        // The code survives, because it is the part a caller can act on.
        Assert.Contains("invalid_request", exception.Message, StringComparison.Ordinal);

        // And the properties still hold what the server sent, for deliberate use.
        Assert.Contains("INJECTED", exception.Error.ErrorDescription!, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankClientIdIsRejectedWhereItIsSetRatherThanAtGoogle()
    {
        Assert.Throws<ArgumentException>(() => new GoogleOAuthOptions
        {
            ClientId = "  ",
            RedirectUri = new Uri("https://example.test/callback"),
        });
    }

    [Fact]
    public void ARelativeRedirectUriIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new GoogleOAuthOptions
        {
            ClientId = "client-123.apps.googleusercontent.com",
            RedirectUri = new Uri("/callback", UriKind.Relative),
        });
    }

    /// <summary>
    /// Google compares the redirect URI to the registration literally, and Uri.ToString() does not
    /// round-trip: a bare origin gains a trailing slash and percent-escapes are decoded.
    /// </summary>
    [Fact]
    public void TheRedirectUriIsSentExactlyAsConfigured()
    {
        const string Registered = "https://example.test/a%20b/callback";

        var client = new GoogleOAuthClient(
            new HttpClient(new StubTokenHandler()),
            new GoogleOAuthOptions
            {
                ClientId = "client-123.apps.googleusercontent.com",
                RedirectUri = new Uri(Registered),
            });

        var query = HttpUtility.ParseQueryString(client.CreateAuthorizationUrl(new GoogleAuthorizationUrlOptions { Scopes = ["scope"] }).Query);

        Assert.Equal(Registered, query["redirect_uri"]);
    }

    /// <summary>
    /// The redirect and the callback are two requests. A verifier held only in memory confines the
    /// flow to one process that never restarts, which no real server is.
    /// </summary>
    [Fact]
    public void AChallengeCanBeRebuiltFromAStoredVerifier()
    {
        var original = PkceCodeChallenge.Create();
        var restored = PkceCodeChallenge.FromVerifier(original.CodeVerifier);

        Assert.Equal(original.CodeVerifier, restored.CodeVerifier);
        Assert.Equal(original.CodeChallenge, restored.CodeChallenge);
    }

    [Theory]
    [InlineData("too-short")]
    [InlineData("has spaces in it and is long enough to pass the length check aaaaa")]
    public void AVerifierThatIsNotOneIsRejected(string verifier)
        => Assert.ThrowsAny<ArgumentException>(() => PkceCodeChallenge.FromVerifier(verifier));

    [Fact]
    public void TokenResponseConvertsToAnAccessTokenWithExpiry()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeClock(now);

        var token = GoogleOAuthClient.ToAccessToken(
            new GoogleTokenResponse
            {
                AccessToken = "ya29.token",
                ExpiresIn = 3599,
                Scope = "scope-a scope-b",
            },
            time);

        Assert.Equal("ya29.token", token.Value);
        Assert.Equal(now.AddSeconds(3599), token.ExpiresAtUtc);
        Assert.Equal(["scope-a", "scope-b"], token.GrantedScopes);
    }

    [Fact]
    public void ATokenResponseWithoutAnAccessTokenIsRejected()
        => Assert.Throws<InvalidOperationException>(() =>
            GoogleOAuthClient.ToAccessToken(new GoogleTokenResponse { ExpiresIn = 60 }));

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

public sealed class PkceTests
{
    [Fact]
    public void ChallengeIsTheBase64UrlSha256OfTheVerifier()
    {
        var pkce = PkceCodeChallenge.Create();

        var expected = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(pkce.CodeVerifier)));

        Assert.Equal(expected, pkce.CodeChallenge);

        // base64url, and unpadded, as RFC 7636 requires.
        Assert.DoesNotContain('+', pkce.CodeChallenge);
        Assert.DoesNotContain('/', pkce.CodeChallenge);
        Assert.DoesNotContain('=', pkce.CodeChallenge);
    }

    [Theory]
    [InlineData(43)]
    [InlineData(64)]
    [InlineData(128)]
    public void VerifierLengthIsHonoured(int length)
        => Assert.Equal(length, PkceCodeChallenge.Create(length).CodeVerifier.Length);

    [Theory]
    [InlineData(42)]
    [InlineData(129)]
    public void RejectsLengthsOutsideTheSpecifiedRange(int length)
        => Assert.Throws<ArgumentOutOfRangeException>(() => PkceCodeChallenge.Create(length));

    [Fact]
    public void VerifierUsesOnlyUnreservedCharacters()
    {
        var verifier = PkceCodeChallenge.Create(128).CodeVerifier;

        Assert.All(verifier, c => Assert.True(
            char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~',
            $"'{c}' is not in the RFC 7636 unreserved set."));
    }

    [Fact]
    public void EachChallengeIsUnique()
    {
        var verifiers = Enumerable.Range(0, 100).Select(_ => PkceCodeChallenge.Create().CodeVerifier).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(100, verifiers.Count);
    }

    [Fact]
    public void ToStringNeverContainsTheVerifier()
    {
        var pkce = PkceCodeChallenge.Create();
        Assert.DoesNotContain(pkce.CodeVerifier, pkce.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>
/// Where Google credentials are allowed to be sent.
/// </summary>
/// <remarks>
/// The token endpoint receives the authorization code, the refresh token and the client secret.
/// Requiring HTTPS says only that nobody in the middle reads them; it says nothing about who is at
/// the other end, which is the question that matters for a credential.
/// </remarks>
public sealed class OAuthEndpointTrustTests
{
    private static GoogleOAuthOptions Base(Uri tokenEndpoint, bool allowCustom = false) => new()
    {
        ClientId = "client-123.apps.googleusercontent.com",
        RedirectUri = new Uri("https://example.test/callback"),
        TokenEndpoint = tokenEndpoint,
        AllowCustomCredentialEndpoints = allowCustom,
    };

    private static GoogleOAuthClient Create(GoogleOAuthOptions options)
        => new(new HttpClient(new HttpClientHandler()), options);

    [Fact]
    public void TheGoogleTokenEndpointNeedsNoOptIn()
    {
        var client = Create(Base(new Uri("https://oauth2.googleapis.com/token")));
        Assert.NotNull(client);
    }

    [Theory]
    [InlineData("https://example.test/token")]
    [InlineData("https://oauth2.googleapis.com.example.test/token")]   // a suffix, not the host
    [InlineData("https://oauth2.googleapis.com:8443/token")]           // right host, another port
    public void AnHttpsEndpointSomewhereElseIsRefused(string endpoint)
    {
        var failure = Assert.Throws<ArgumentException>(
            () => Create(Base(new Uri(endpoint))));

        Assert.Contains("AllowCustomCredentialEndpoints", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACustomEndpointWorksOnceItIsDeclared()
    {
        var client = Create(Base(new Uri("https://oauth.example.test/token"), allowCustom: true));
        Assert.NotNull(client);
    }

    [Fact]
    public void ALoopbackEndpointNeedsNoOptIn()
    {
        // Credentials that reach only this machine have not been disclosed, and requiring a flag
        // for every local emulator would make the flag routine — which is how a flag stops working.
        var client = Create(Base(new Uri("http://localhost:8080/token")));
        Assert.NotNull(client);
    }

    [Fact]
    public void TheOrderOfTheInitialiserDoesNotChangeTheAnswer()
    {
        // The check runs when the client is built, not in a property setter, so it cannot depend
        // on whether AllowCustomCredentialEndpoints was written above or below TokenEndpoint.
        var flagFirst = new GoogleOAuthOptions
        {
            AllowCustomCredentialEndpoints = true,
            ClientId = "client-123.apps.googleusercontent.com",
            RedirectUri = new Uri("https://example.test/callback"),
            TokenEndpoint = new Uri("https://oauth.example.test/token"),
        };

        var flagLast = new GoogleOAuthOptions
        {
            ClientId = "client-123.apps.googleusercontent.com",
            RedirectUri = new Uri("https://example.test/callback"),
            TokenEndpoint = new Uri("https://oauth.example.test/token"),
            AllowCustomCredentialEndpoints = true,
        };

        Assert.NotNull(Create(flagFirst));
        Assert.NotNull(Create(flagLast));
    }
}
