using System.Text;
using PommeFlash.Host.Native;

namespace PommeFlash.Host
{
    /// <summary>
    /// Données envoyées par NPN_PostURL. Flash les fait précéder de ses en-têtes
    /// (« Content-Type: …\r\nContent-Length: …\r\n\r\n ») : ils sont séparés du corps.
    /// </summary>
    sealed class PostData
    {
        PostData(byte[] body, IReadOnlyList<KeyValuePair<string, string>> headers)
        {
            Body = body;
            Headers = headers;
        }

        public byte[] Body { get; }

        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

        public static unsafe PostData Read(nint buffer, uint length, bool isFile)
        {
            byte[] data;
            if (isFile)
            {
                string path = NpMemory.ReadUtf8(buffer, length);
                if (path.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                    path = new Uri(path).LocalPath;
                data = File.ReadAllBytes(path);
            }
            else
            {
                data = buffer == 0 ? Array.Empty<byte>() : new ReadOnlySpan<byte>((void*)buffer, (int)length).ToArray();
            }
            return Parse(data);
        }

        public static PostData Parse(byte[] data)
        {
            int end = IndexOf(data, "\r\n\r\n"u8);
            if (end < 0)
                return new PostData(data, Array.Empty<KeyValuePair<string, string>>());

            string head = Encoding.ASCII.GetString(data, 0, end);
            var headers = new List<KeyValuePair<string, string>>();
            foreach (string line in head.Split("\r\n"))
            {
                int colon = line.IndexOf(':');
                // Ce n'est pas un bloc d'en-têtes : tout est le corps.
                if (colon <= 0 || line.AsSpan(0, colon).ContainsAny(" \t"))
                    return new PostData(data, Array.Empty<KeyValuePair<string, string>>());
                headers.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }
            return new PostData(data[(end + 4)..], headers);
        }

        static int IndexOf(byte[] data, ReadOnlySpan<byte> pattern) => data.AsSpan().IndexOf(pattern);
    }
}
