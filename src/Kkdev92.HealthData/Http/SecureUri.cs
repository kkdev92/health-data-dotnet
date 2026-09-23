using System.Diagnostics.CodeAnalysis;

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
/// <para>
/// Extension members, so that a call site reads as a question about the address it holds. That
/// form is kept to internal classes: the compiler emits a public nested type for every extension
/// block, which on a public class would appear in the package's public surface. The public
/// extension classes in this SDK use the classic form for that reason.
/// </para>
/// </remarks>
internal static class SecureUri
{
    extension([NotNullWhen(true)] Uri? uri)
    {
        /// <summary>
        /// Whether an address can carry a credential.
        /// </summary>
        /// <remarks>
        /// HTTPS anywhere, or plain HTTP to loopback for a local test server. Loopback alone would
        /// not do: <see cref="Uri.IsLoopback"/> is true for <c>ftp://localhost</c> and
        /// <c>file://localhost</c> as well, and the host being this machine says nothing about
        /// whether the scheme puts the credential on a wire in the clear.
        /// </remarks>
        public bool IsHttpsOrLoopback()
            => uri is { IsAbsoluteUri: true }
               && (uri.Scheme == Uri.UriSchemeHttps
                   || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

        /// <summary>
        /// Whether this address names the same server as <paramref name="origin"/>.
        /// </summary>
        /// <remarks>
        /// Compared field by field rather than with <c>GetLeftPart(UriPartial.Authority)</c>, which
        /// includes userinfo: <c>https://attacker@health.googleapis.com</c> would otherwise fail to
        /// match while still resolving to Google, and the reverse trick is worse.
        /// <see cref="Uri.IdnHost"/> rather than <see cref="Uri.Host"/> so a Unicode spelling of the
        /// host cannot present as a different origin.
        /// </remarks>
        public bool IsSameOriginAs([NotNullWhen(true)] Uri? origin)
            => uri is { IsAbsoluteUri: true }
               && origin is { IsAbsoluteUri: true }
               && string.Equals(uri.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(uri.IdnHost, origin.IdnHost, StringComparison.OrdinalIgnoreCase)
               && uri.Port == origin.Port;
    }

    extension(Uri? uri)
    {
        /// <summary>
        /// Names an address well enough to fix it, without repeating a credential put inside it.
        /// </summary>
        /// <remarks>
        /// A URI can carry a secret in its userinfo or its query, and the misconfiguration these
        /// messages complain about is precisely the one where somebody has done that. Printing the
        /// whole thing would write the credential to a log as the price of objecting to it. Scheme,
        /// host and non-default port are enough to recognise the address and carry nothing else.
        /// </remarks>
        public string Describe()
        {
            if (uri is not { IsAbsoluteUri: true })
            {
                return "(not an absolute address)";
            }

            var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";

            return $"{uri.Scheme}://{uri.IdnHost}{port}";
        }
    }
}
