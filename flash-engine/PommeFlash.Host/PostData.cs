using System.Text;
using PommeFlash.Host.Native;

namespace PommeFlash.Host
{
    /// <summary>
    /// Données envoyées par NPN_PostURL. Flash les fait précéder de ses en-têtes
    /// (« Content-Type: …\r\nContent-Length: …\r\n\r\n ») : ils sont séparés du corps. Le corps
    /// s'arrête à la longueur que le module annonce (Content-Length), comme un serveur le lirait :
    /// des octets au-delà (zéro final d'une chaîne C) fausseraient le dernier champ d'un formulaire.
    /// </summary>
    sealed class PostData
    {
        PostData(byte[] body, IReadOnlyList<KeyValuePair<string, string>> headers, int dropped = 0)
        {
            Body = body;
            Headers = headers;
            Dropped = dropped;
        }

        public byte[] Body { get; }

        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

        /// <summary>Octets retirés après la longueur annoncée par le module.</summary>
        public int Dropped { get; }

        /// <summary>En-tête donné par le module (null s'il ne l'a pas donné).</summary>
        public string? Header(string name)
            => Headers.FirstOrDefault(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

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
            byte[] body = data[(end + 4)..];
            string? length = headers.FirstOrDefault(header => header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Value;
            if (int.TryParse(length, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int announced) &&
                announced < body.Length)
                return new PostData(body[..announced], headers, body.Length - announced);
            return new PostData(body, headers);
        }

        static int IndexOf(byte[] data, ReadOnlySpan<byte> pattern) => data.AsSpan().IndexOf(pattern);
    }
}
