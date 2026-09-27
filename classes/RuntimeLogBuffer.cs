using System;
using System.IO;
using System.Text;

namespace MyHomelabBrowser.classes
{
    /// <summary>
    /// Journal en mémoire joint aux rapports de bug. Borné pour ne pas grossir
    /// indéfiniment pendant une longue session.
    /// </summary>
    public static class RuntimeLogBuffer
    {
        const int MaxChars = 256 * 1024;

        static readonly StringBuilder _buffer = new();
        static readonly object _lock = new();
        static bool _initialized;

        public static void Init()
        {
            lock (_lock)
            {
                if (_initialized)
                    return;

                _initialized = true;
            }

            Console.SetOut(new InterceptWriter(Console.Out));
            Console.SetError(new InterceptWriter(Console.Error));
        }

        public static void Append(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            lock (_lock)
            {
                _buffer.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ").AppendLine(message);

                if (_buffer.Length > MaxChars)
                    _buffer.Remove(0, _buffer.Length - MaxChars * 3 / 4);
            }
        }

        public static string GetSnapshot()
        {
            lock (_lock)
                return _buffer.ToString();
        }

        class InterceptWriter : TextWriter
        {
            readonly TextWriter _original;

            public InterceptWriter(TextWriter original)
            {
                _original = original;
            }

            public override Encoding Encoding => _original.Encoding;

            public override void WriteLine(string? value)
            {
                Append(value);
                _original.WriteLine(value);
            }
        }
    }
}
