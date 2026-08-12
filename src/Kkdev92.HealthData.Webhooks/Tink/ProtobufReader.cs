namespace Kkdev92.HealthData.Webhooks.Tink;

/// <summary>One field read from a protobuf message.</summary>
internal readonly struct ProtobufValue
{
    public ProtobufValue(ulong varint) => Varint = varint;

    public ProtobufValue(byte[] bytes) => Bytes = bytes;

    /// <summary>The value, when the field used varint encoding.</summary>
    public ulong Varint { get; }

    /// <summary>The value, when the field used length-delimited encoding.</summary>
    public byte[]? Bytes { get; }
}

/// <summary>
/// Reads the two protobuf wire types the Tink key material uses.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately minimal. A <c>google.crypto.tink.EcdsaPublicKey</c> is a handful of varints and
/// two byte fields; taking a protobuf runtime dependency to read that would cost more than it
/// buys, and the core promise of this repository is zero third-party runtime dependencies
/// (ADR-0002).
/// </para>
/// <para>
/// Unknown fields are skipped rather than rejected, which is what protobuf requires of any
/// reader, and what lets a future Tink field land without breaking verification.
/// </para>
/// </remarks>
internal static class ProtobufReader
{
    private const int WireTypeVarint = 0;
    private const int WireTypeFixed64 = 1;
    private const int WireTypeLengthDelimited = 2;
    private const int WireTypeFixed32 = 5;

    /// <summary>Enumerates the fields of a protobuf message.</summary>
    /// <exception cref="InvalidOperationException">The message is truncated or uses a removed wire type.</exception>
    public static IEnumerable<(int Field, ProtobufValue Value)> Read(byte[] message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var position = 0;

        while (position < message.Length)
        {
            var tag = ReadVarint(message, ref position);
            var field = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);

            switch (wireType)
            {
                case WireTypeVarint:
                    yield return (field, new ProtobufValue(ReadVarint(message, ref position)));
                    break;

                case WireTypeLengthDelimited:
                    var length = checked((int)ReadVarint(message, ref position));
                    EnsureAvailable(message, position, length);
                    yield return (field, new ProtobufValue(message[position..(position + length)]));
                    position += length;
                    break;

                case WireTypeFixed64:
                    EnsureAvailable(message, position, 8);
                    position += 8;
                    break;

                case WireTypeFixed32:
                    EnsureAvailable(message, position, 4);
                    position += 4;
                    break;

                default:
                    // Wire types 3 and 4 were the removed group encoding.
                    throw new InvalidOperationException($"Unsupported protobuf wire type {wireType}.");
            }
        }
    }

    private static ulong ReadVarint(byte[] message, ref int position)
    {
        ulong value = 0;
        var shift = 0;

        while (true)
        {
            if (position >= message.Length)
            {
                throw new InvalidOperationException("The protobuf message ended inside a varint.");
            }

            if (shift > 63)
            {
                throw new InvalidOperationException("The protobuf varint is longer than 64 bits.");
            }

            var current = message[position++];
            value |= (ulong)(current & 0x7F) << shift;

            if ((current & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }
    }

    private static void EnsureAvailable(byte[] message, int position, int length)
    {
        if (length < 0 || position + length > message.Length)
        {
            throw new InvalidOperationException("The protobuf message is truncated.");
        }
    }
}
