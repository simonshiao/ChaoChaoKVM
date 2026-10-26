using System;
using System.IO;
using System.Net.Sockets;

namespace ChaoChaoKvm.Core
{
    public static class PacketCodec
    {
        public static void Write(NetworkStream stream, byte kind, byte[] payload)
        {
            if (payload == null) payload = new byte[0];
            int size = checked(payload.Length + 1);
            if (size > Protocol.MaxPacketBytes) throw new InvalidDataException("KVM packet 太大");
            byte[] header = { (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size };
            stream.Write(header, 0, 4);
            stream.WriteByte(kind);
            if (payload.Length > 0) stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        public static Packet Read(NetworkStream stream)
        {
            byte[] header = ReadExactly(stream, 4);
            int size = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            if (size < 1 || size > Protocol.MaxPacketBytes) throw new InvalidDataException("無效的 KVM packet size");
            byte[] body = ReadExactly(stream, size);
            byte[] payload = new byte[size - 1];
            if (payload.Length > 0) Buffer.BlockCopy(body, 1, payload, 0, payload.Length);
            return new Packet(body[0], payload);
        }

        private static byte[] ReadExactly(Stream stream, int size)
        {
            byte[] data = new byte[size];
            int offset = 0;
            while (offset < size)
            {
                int read = stream.Read(data, offset, size - offset);
                if (read <= 0) throw new EndOfStreamException("Peer disconnected");
                offset += read;
            }
            return data;
        }
    }

    public sealed class Packet
    {
        public byte Kind { get; private set; }
        public byte[] Payload { get; private set; }
        public Packet(byte kind, byte[] payload) { Kind = kind; Payload = payload; }
    }
}
