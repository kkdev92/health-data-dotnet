namespace Kkdev92.HealthData.Http;

/// <summary>
/// The rules every address in this SDK is held to before a secret is sent to it.
/// </summary>
/// <remarks>
/// <para>
/// Four places asked the same two questions — is this address safe to send a credential to, and
/// how do I name it in the complaint — and answered them with four copies of the same code. That
/// is a poor arrangement for a check whose whole purpose is to be uniform: the next change to one
/// of them would have left the other three behind, and the one that decides whether a token goes
/// out in the clear is not the place to discover that.
/// </para>
/// <para>
/// Internal rather than public. It is a rule this SDK applies to itself, not a service offered to
/// callers, and the messages that quote it stay where the address came from — what a wrong keyset
/// URI means is not what a wrong token endpoint means.
/// </para>
/// </remarks>
internal static class SecureUri
{
    /// <summary>
    /// Whether an address can carry a credential.
    /// </summary>
    /// <remarks>
    /// HTTPS anywhere, or plain HTTP to loopback for a local test server. Loopback alone would not
    /// do: <see cref="Uri.IsLoopback"/> is true for <c>ftp://localhost</c> and <c>file://localhost</c>
    /// as well, and the host being this machine says nothing about whether the scheme puts the
    /// credential on a wire in the clear.
    /// </remarks>
    public static bool IsHttpsOrLoopback(Uri? uri)
        => uri is { IsAbsoluteUri: true }
           && (uri.Scheme == Uri.UriSchemeHttps
               || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    /// <summary>
    /// Whether two addresses name the same server.
    /// </summary>
    /// <remarks>
    /// Compared field by field rather than with <c>GetLeftPart(UriPartial.Authority)</c>, which
    /// includes userinfo: <c>https://attacker@health.googleapis.com</c> would otherwise fail to
    /// match while still resolving to Google, and the reverse trick is worse.
    /// <see cref="Uri.IdnHost"/> rather than <see cref="Uri.Host"/> so a Unicode spelling of the
    /// host cannot present as a different origin.
    /// </remarks>
    public static bool IsSameOrigin(Uri? address, Uri? origin)
        => address is { IsAbsoluteUri: true }
           && origin is { IsAbsoluteUri: true }
           && string.Equals(address.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(address.IdnHost, origin.IdnHost, StringComparison.OrdinalIgnoreCase)
           && address.Port == origin.Port;

    /// <summary>
    /// Names an address well enough to fix it, without repeating a credential put inside it.
    /// </summary>
    /// <remarks>
    /// A URI can carry a secret in its userinfo or its query, and the misconfiguration these
    /// messages complain about is precisely the one where somebody has done that. Printing the
    /// whole thing would write the credential to a log as the price of objecting to it. Scheme,
    /// host and non-default port are enough to recognise the address and carry nothing else.
    /// </remarks>
    public static string Describe(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true })
        {
            return "(not an absolute address)";
        }

        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";

        return $"{uri.Scheme}://{uri.IdnHost}{port}";
    }
}
