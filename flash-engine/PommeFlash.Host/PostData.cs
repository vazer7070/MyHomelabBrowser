using System.Text;
using PommeFlash.Host.Native;

namespace PommeFlash.Host
{
    /// <summary>
    /// Données envoyées par NPN_PostURL, découpées comme Firefox (et donc Basilisk) le fait
    /// (nsPluginHost::ParsePostBufferToFixHeaders) : le module peut faire précéder le corps de ses
    /// en-têtes (« Content-Type: …\nContent-Length: …\n\n », fins de ligne \r\n ou \n seules),
    /// reconnus à leur Content-Length et finis par une ligne vide ; une ligne vide en tête veut dire
    /// « pas d'en-têtes » (règle de NPAPI). Demon Slayer écrit les siens avec des \n seules : lus
    /// comme du corps, ils partaient au serveur collés devant le formulaire, qui n'avait plus de
    /// champ « site ». Le corps s'arrête à la longueur annoncée (Content-Length), comme un serveur
    /// le lirait : un octet au-delà (zéro final d'une chaîne C) fausserait le dernier champ.
    /// </summary>
    sealed class PostData
    {
        PostData(byte[] body, IReadOnlyList<KeyValuePair<string, string>> headers, string layout, int dropped = 0)
        {
            Body = body;
            Headers = headers;
            Layout = layout;
            Dropped = dropped;
        }

        public byte[] Body { get; }

        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

        /// <summary>Forme du tampon du module, pour le journal (en-têtes et fins de ligne).</summary>
        public string Layout { get; }

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
            // Ligne vide en tête : pas d'en-têtes, le corps suit.
            if (data.Length > 0 && data[0] == (byte)'\n')
                return new PostData(data[1..], Array.Empty<KeyValuePair<string, string>>(), "sans en-têtes (ligne vide en tête)");
            if (data.AsSpan().StartsWith("\r\n"u8))
                return new PostData(data[2..], Array.Empty<KeyValuePair<string, string>>(), "sans en-têtes (ligne vide en tête)");

            int bodyStart = FirefoxBodyStart(data);
            if (bodyStart < 0)
                bodyStart = StrictBodyStart(data);
            if (bodyStart < 0)
                return new PostData(data, Array.Empty<KeyValuePair<string, string>>(), "sans en-têtes");

            string head = Encoding.ASCII.GetString(data, 0, bodyStart);
            var headers = new List<KeyValuePair<string, string>>();
            foreach (string raw in head.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int colon = line.IndexOf(':');
                if (colon > 0 && !line.AsSpan(0, colon).ContainsAny(" \t"))
                    headers.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }
            string layout = head.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n') ? "en-têtes (fins de ligne \\n)" : "en-têtes (fins de ligne \\r\\n)";
            byte[] body = data[bodyStart..];
            string? length = headers.FirstOrDefault(header => header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Value;
            if (int.TryParse(length, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int announced) &&
                announced < body.Length)
                return new PostData(body[..announced], headers, layout, body.Length - announced);
            return new PostData(body, headers, layout);
        }

        /// <summary>
        /// Début du corps selon Firefox : en-têtes seulement s'il y a un Content-Length (suivi d'un
        /// chiffre en fin de ligne), finis par la première ligne vide qui suit (\r\n\r\n ou \n\n).
        /// -1 : pas d'en-têtes.
        /// </summary>
        static int FirefoxBodyStart(ReadOnlySpan<byte> data)
        {
            ReadOnlySpan<byte> name = "content-length"u8;
            bool found = false;
            for (int i = 0; i < data.Length; i++)
            {
                if (!found && (data[i] | 0x20) == 'c' && i + name.Length < data.Length && Ascii.EqualsIgnoreCase(data.Slice(i, name.Length), name))
                {
                    int end = data[(i + name.Length)..].IndexOfAny((byte)'\r', (byte)'\n');
                    if (end < 0)
                        return -1;
                    end += i + name.Length;
                    if (data[end - 1] is < (byte)'0' or > (byte)'9')
                        return -1;
                    found = true;
                    i = end;
                }
                if (!found)
                    continue;
                if (data[i] == '\r' && data[i..].StartsWith("\r\n\r\n"u8))
                    return i + 4;
                if (data[i] == '\n' && i + 1 < data.Length && data[i + 1] == '\n')
                    return i + 2;
            }
            return -1;
        }

        /// <summary>Sans Content-Length : un bloc d'en-têtes bien formé (lignes « Nom: valeur », \r\n) suivi d'une ligne vide.</summary>
        static int StrictBodyStart(byte[] data)
        {
            int end = data.AsSpan().IndexOf("\r\n\r\n"u8);
            if (end < 0)
                return -1;
            foreach (string line in Encoding.ASCII.GetString(data, 0, end).Split("\r\n"))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0 || line.AsSpan(0, colon).ContainsAny(" \t"))
                    return -1;
            }
            return end + 4;
        }
    }
}
