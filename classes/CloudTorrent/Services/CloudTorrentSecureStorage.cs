using MyHomelabBrowser.classes.Profiles;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MyHomelabBrowser.classes.CloudTorrent.Services
{
    /// <summary>
    /// Stocke la clé API dans un fichier protégé par DPAPI pour l'utilisateur Windows courant.
    /// Le fichier reste séparé pour chaque profil du navigateur grâce à AppDataContext.Root.
    /// </summary>
    public sealed class CloudTorrentSecureStorage
    {
        private const int CryptProtectUiForbidden = 0x1;
        private const string SecretFileName = "cloudtorrent.key.dpapi";

        private string SecretPath => AppDataContext.GetPath(SecretFileName);

        public bool HasSecret => File.Exists(SecretPath);

        public string? ReadApiKey()
        {
            string path = SecretPath;
            if (!File.Exists(path))
                return null;

            byte[] protectedBytes = File.ReadAllBytes(path);
            if (protectedBytes.Length == 0)
                return null;

            byte[] clearBytes = Unprotect(protectedBytes);
            try
            {
                string value = Encoding.UTF8.GetString(clearBytes).Trim();
                return value.Length == 0 ? null : value;
            }
            finally
            {
                Array.Clear(clearBytes, 0, clearBytes.Length);
            }
        }

        public void WriteApiKey(string apiKey)
        {
            string value = (apiKey ?? string.Empty).Trim();
            if (value.Length == 0)
                throw new ArgumentException("La clé API est vide.", nameof(apiKey));

            byte[] clearBytes = Encoding.UTF8.GetBytes(value);
            byte[] protectedBytes;
            try
            {
                protectedBytes = Protect(clearBytes);
            }
            finally
            {
                Array.Clear(clearBytes, 0, clearBytes.Length);
            }

            string path = SecretPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            string temporaryPath = path + ".tmp";
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, path, overwrite: true);
        }

        public void DeleteApiKey()
        {
            string path = SecretPath;
            if (File.Exists(path))
                File.Delete(path);
        }

        private static byte[] Protect(byte[] clearBytes)
        {
            DataBlob input = CreateBlob(clearBytes);
            DataBlob output = default;

            try
            {
                if (!CryptProtectData(
                        ref input,
                        "Clé API CloudTorrent - MyHomelabBrowser",
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        CryptProtectUiForbidden,
                        out output))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Windows n’a pas pu protéger la clé API CloudTorrent.");
                }

                return CopyBlob(output);
            }
            finally
            {
                FreeInputBlob(input);
                FreeOutputBlob(output);
            }
        }

        private static byte[] Unprotect(byte[] protectedBytes)
        {
            DataBlob input = CreateBlob(protectedBytes);
            DataBlob output = default;
            IntPtr description = IntPtr.Zero;

            try
            {
                if (!CryptUnprotectData(
                        ref input,
                        out description,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        CryptProtectUiForbidden,
                        out output))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Windows n’a pas pu déchiffrer la clé API CloudTorrent de ce profil.");
                }

                return CopyBlob(output);
            }
            finally
            {
                if (description != IntPtr.Zero)
                    LocalFree(description);

                FreeInputBlob(input);
                FreeOutputBlob(output);
            }
        }

        private static DataBlob CreateBlob(byte[] data)
        {
            if (data.Length == 0)
                return default;

            IntPtr pointer = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, pointer, data.Length);
            return new DataBlob { Size = data.Length, Data = pointer };
        }

        private static byte[] CopyBlob(DataBlob blob)
        {
            if (blob.Size <= 0 || blob.Data == IntPtr.Zero)
                return Array.Empty<byte>();

            byte[] result = new byte[blob.Size];
            Marshal.Copy(blob.Data, result, 0, blob.Size);
            return result;
        }

        private static void FreeInputBlob(DataBlob blob)
        {
            if (blob.Data != IntPtr.Zero)
                Marshal.FreeHGlobal(blob.Data);
        }

        private static void FreeOutputBlob(DataBlob blob)
        {
            if (blob.Data != IntPtr.Zero)
                LocalFree(blob.Data);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref DataBlob dataIn,
            string? dataDescription,
            IntPtr optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            int flags,
            out DataBlob dataOut);

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DataBlob dataIn,
            out IntPtr dataDescription,
            IntPtr optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            int flags,
            out DataBlob dataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
