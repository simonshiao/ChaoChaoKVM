using System.Buffers.Binary;
using System.IO;
using System.Text.Json;

namespace ChaoChaoKvm.Transport;

public static class PacketIo
{
    public const int MaxHandshakePacket = 64 * 1024;

    public static async Task WritePlainJsonAsync(
        Stream stream,
        object payload,
        CancellationToken cancellationToken = default)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (json.Length > MaxHandshakePacket)
            throw new InvalidDataException("Handshake packet is too large.");

        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)json.Length));
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(json, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<JsonDocument> ReadPlainJsonAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        byte[] header = await ReadExactlyAsync(stream, 4, cancellationToken);
        uint size = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (size == 0 || size > MaxHandshakePacket)
            throw new InvalidDataException("Invalid handshake packet size.");

        byte[] json = await ReadExactlyAsync(stream, checked((int)size), cancellationToken);
        return JsonDocument.Parse(json);
    }

    public static async Task<byte[]> ReadExactlyAsync(
        Stream stream,
        int size,
        CancellationToken cancellationToken = default)
    {
        if (size < 0)
            throw new ArgumentOutOfRangeException(nameof(size));

        byte[] buffer = new byte[size];
        int offset = 0;
        while (offset < size)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, size - offset), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("Peer disconnected.");
            offset += read;
        }
        return buffer;
    }
}
