using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace ChaoChaoKvm.Core
{
    public sealed class HttpRequestData
    {
        public string Method;
        public string Path;
        public string Protocol;
        public Dictionary<string, string> Headers;
        public long ContentLength;
        public NetworkStream Stream;
    }

    // 直接沿用 ChaoTransfer 已驗證的 HTTP/1.1 核心概念：
    // TcpListener + Content-Length + Connection: close，不需要 HttpListener/URLACL/Kestrel。
    public static class HttpWire
    {
        public static HttpRequestData ReadRequest(NetworkStream stream)
        {
            byte[] headerBytes = ReadHeaderBlock(stream, 64 * 1024);
            string headerText = Encoding.ASCII.GetString(headerBytes);
            string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0])) throw new InvalidDataException("HTTP request line 缺失");

            string[] first = lines[0].Split(new[] { ' ' }, 3);
            if (first.Length < 2) throw new InvalidDataException("HTTP request line 格式錯誤");

            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrEmpty(line)) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            long contentLength = 0;
            string rawLength;
            if (headers.TryGetValue("Content-Length", out rawLength)) long.TryParse(rawLength, out contentLength);
            string expect;
            if (contentLength > 0 && headers.TryGetValue("Expect", out expect) && string.Equals(expect, "100-continue", StringComparison.OrdinalIgnoreCase))
            {
                byte[] proceed = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                stream.Write(proceed, 0, proceed.Length);
                stream.Flush();
            }

            return new HttpRequestData
            {
                Method = first[0].ToUpperInvariant(),
                Path = first[1],
                Protocol = first.Length > 2 ? first[2] : "HTTP/1.1",
                Headers = headers,
                ContentLength = contentLength,
                Stream = stream
            };
        }

        public static byte[] ReadBody(HttpRequestData request, int maxBytes)
        {
            if (request.ContentLength < 0 || request.ContentLength > maxBytes) throw new InvalidDataException("HTTP body 大小超出限制");
            byte[] data = new byte[(int)request.ContentLength];
            ReadExact(request.Stream, data, 0, data.Length);
            return data;
        }

        public static void CopyBodyToFile(HttpRequestData request, string filePath, Action<long, long> progress)
        {
            long remaining = request.ContentLength;
            long received = 0;
            byte[] buffer = new byte[Protocol.BufferSize];
            using (FileStream file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                while (remaining > 0)
                {
                    int wanted = (int)Math.Min(buffer.Length, remaining);
                    int read = request.Stream.Read(buffer, 0, wanted);
                    if (read <= 0) throw new EndOfStreamException("檔案傳輸途中連線中斷");
                    file.Write(buffer, 0, read);
                    remaining -= read;
                    received += read;
                    if (progress != null) progress(received, request.ContentLength);
                }
            }
        }

        public static void WriteJson(NetworkStream stream, int statusCode, object value)
        {
            byte[] body = JsonUtil.Utf8(value);
            WriteBytes(stream, statusCode, "application/json; charset=utf-8", body, null);
        }

        public static void WriteBytes(NetworkStream stream, int statusCode, string contentType, byte[] body, IDictionary<string, string> extraHeaders)
        {
            body = body ?? new byte[0];
            WriteHeaders(stream, statusCode, StatusText(statusCode), contentType, body.LongLength, extraHeaders);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        public static void WriteEmpty(NetworkStream stream, int statusCode, IDictionary<string, string> extraHeaders)
        {
            WriteHeaders(stream, statusCode, StatusText(statusCode), "application/octet-stream", 0, extraHeaders);
            stream.Flush();
        }

        public static void WriteFile(NetworkStream stream, string filePath, IDictionary<string, string> extraHeaders, Action<long, long> progress)
        {
            FileInfo info = new FileInfo(filePath);
            WriteHeaders(stream, 200, "OK", "application/octet-stream", info.Length, extraHeaders);
            byte[] buffer = new byte[Protocol.BufferSize];
            long sent = 0;
            using (FileStream file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int read;
                while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
                {
                    stream.Write(buffer, 0, read);
                    sent += read;
                    if (progress != null) progress(sent, info.Length);
                }
            }
            stream.Flush();
        }

        private static byte[] ReadHeaderBlock(NetworkStream stream, int maxBytes)
        {
            MemoryStream ms = new MemoryStream();
            int state = 0;
            while (ms.Length < maxBytes)
            {
                int value = stream.ReadByte();
                if (value < 0) throw new EndOfStreamException("HTTP header 尚未結束連線即中斷");
                byte b = (byte)value;
                ms.WriteByte(b);
                if (state == 0 && b == 13) state = 1;
                else if (state == 1 && b == 10) state = 2;
                else if (state == 2 && b == 13) state = 3;
                else if (state == 3 && b == 10)
                {
                    byte[] all = ms.ToArray();
                    byte[] result = new byte[all.Length - 4];
                    Buffer.BlockCopy(all, 0, result, 0, result.Length);
                    return result;
                }
                else state = b == 13 ? 1 : 0;
            }
            throw new InvalidDataException("HTTP header 太大");
        }

        private static void WriteHeaders(NetworkStream stream, int statusCode, string statusText, string contentType, long contentLength, IDictionary<string, string> extraHeaders)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(statusText).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(contentLength).Append("\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("Server: ChaoChaoKvm-Net472\r\n");
            if (extraHeaders != null)
                foreach (KeyValuePair<string, string> item in extraHeaders) sb.Append(item.Key).Append(": ").Append(item.Value).Append("\r\n");
            sb.Append("\r\n");
            byte[] header = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(header, 0, header.Length);
        }

        private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
                count -= read;
            }
        }

        private static string StatusText(int code)
        {
            switch (code)
            {
                case 200: return "OK";
                case 201: return "Created";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 409: return "Conflict";
                case 500: return "Internal Server Error";
                default: return "Status";
            }
        }
    }
}
