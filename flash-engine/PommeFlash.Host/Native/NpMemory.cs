using System.Runtime.InteropServices;
using System.Text;

namespace PommeFlash.Host.Native
{
    /// <summary>
    /// Mémoire partagée avec le module : ce que l'un alloue (NPN_MemAlloc), l'autre le libère
    /// (NPN_MemFree, NPN_ReleaseVariantValue). Un seul allocateur des deux côtés.
    /// </summary>
    static unsafe class NpMemory
    {
        public static nint Alloc(nuint size) => (nint)NativeMemory.Alloc(size == 0 ? 1 : size);

        public static void Free(nint pointer)
        {
            if (pointer != 0)
                NativeMemory.Free((void*)pointer);
        }

        public static nint AllocZeroed(nuint size) => (nint)NativeMemory.AllocZeroed(size == 0 ? 1 : size);

        /// <summary>Chaîne UTF-8 terminée par un zéro, libérable par le module.</summary>
        public static nint Utf8(string text) => Utf8(text, out _);

        public static nint Utf8(string text, out uint length)
        {
            int count = Encoding.UTF8.GetByteCount(text);
            byte* bytes = (byte*)Alloc((nuint)count + 1);
            fixed (char* chars = text)
                Encoding.UTF8.GetBytes(chars, text.Length, bytes, count);
            bytes[count] = 0;
            length = (uint)count;
            return (nint)bytes;
        }

        public static string? ReadUtf8(nint pointer) => pointer == 0 ? null : Marshal.PtrToStringUTF8(pointer);

        public static string ReadUtf8(nint pointer, uint length)
            => pointer == 0 || length == 0 ? string.Empty : Encoding.UTF8.GetString((byte*)pointer, (int)length);
    }

    /// <summary>
    /// Identifiants NPAPI (noms de propriétés et de méthodes) : une chaîne a toujours le même
    /// identifiant, l'adresse d'une copie de son texte ; un entier est codé dans l'identifiant
    /// lui-même (bit de poids faible à 1, jamais le cas d'une adresse).
    /// </summary>
    static class NpIdentifiers
    {
        static readonly object Gate = new();
        static readonly Dictionary<string, nint> ByName = new(StringComparer.Ordinal);
        static readonly Dictionary<nint, string> Names = new();

        public static nint FromString(string name)
        {
            lock (Gate)
            {
                if (ByName.TryGetValue(name, out nint id))
                    return id;
                id = NpMemory.Utf8(name);
                ByName[name] = id;
                Names[id] = name;
                return id;
            }
        }

        public static nint FromInt(int value) => ((nint)value << 1) | 1;

        public static bool IsString(nint id) => (id & 1) == 0 && id != 0;

        public static int ToInt(nint id) => (int)(id >> 1);

        public static string? ToName(nint id)
        {
            if (!IsString(id))
                return null;
            lock (Gate)
                return Names.TryGetValue(id, out string? name) ? name : null;
        }

        /// <summary>Nom lisible (entier ou chaîne), pour les objets de l'hôte.</summary>
        public static string Describe(nint id) => ToName(id) ?? ToInt(id).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
