using System.Net.Http.Headers;

namespace Kkdev92.HealthData.Http;

/// <summary>
/// Reads a <c>Retry-After</c> header as the wait it asks for.
/// </summary>
/// <remarks>
/// The transport reads this header to report it on an exception, and the retry handler reads it to
/// decide whether to wait. With a copy each, a fix to one left the other behind — which has
/// happened — so both read it here.
/// </remarks>
internal static class RetryAfter
{
    extension(RetryConditionHeaderValue? header)
    {
        /// <summary>
        /// The wait the header asks for, in whichever form it was sent, or <see langword="null"/>
        /// when there is no header.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>Retry-After</c> is either a delay in seconds or an HTTP-date, and
        /// <see cref="RetryConditionHeaderValue"/> puts them in different properties, so a reader
        /// that looks at one silently ignores the other. A date already in the past means now rather
        /// than a negative delay.
        /// </para>
        /// <para>
        /// The clock is a parameter because the HTTP-date form is a subtraction from now, and reading
        /// the system clock here would leave that arithmetic with no way to be tested.
        /// </para>
        /// </remarks>
        public TimeSpan? ToDelay(TimeProvider clock)
        {
            if (header?.Delta is { } delta)
            {
                return delta;
            }

            if (header?.Date is { } date)
            {
                var wait = date - clock.GetUtcNow();
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }

            return null;
        }
    }
}
