using System;

namespace MyHomelabBrowser.classes.Support.Models
{
    public sealed class SupportAttachment
    {
        public string FileName { get; init; } = "pommebrowser-log.txt";
        public string ContentType { get; init; } = "text/plain";
        public byte[] Content { get; init; } = Array.Empty<byte>();

        public bool IsEmpty => Content.Length == 0;
    }
}
