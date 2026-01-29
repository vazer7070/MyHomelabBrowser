using System;
using System.IO;
using System.Text;

namespace MyHomelabBrowser.classes
{
    public static class RuntimeLogBuffer
    {
        static readonly StringBuilder _buffer = new();
        static readonly object _lock = new();

        public static void Init()
        {
            Console.SetOut(new InterceptWriter(Console.Out));
            Console.SetError(new InterceptWriter(Console.Error));
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
                lock (_lock)
                {
                    _buffer.AppendLine(
                        $"[{DateTime.Now:HH:mm:ss}] {value}"
                    );
                }

                _original.WriteLine(value);
            }
        }
    }
}