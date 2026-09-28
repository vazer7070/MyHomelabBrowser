using System.Collections.Generic;
using System.Text;

namespace MyHomelabBrowser.classes.Flash
{
    /// <summary>
    /// Ligne de commande Windows construite argument par argument, selon les règles
    /// de découpage de CommandLineToArgvW : une adresse ou un chemin ne peut pas
    /// injecter d'argument supplémentaire.
    /// </summary>
    public static class WindowsCommandLine
    {
        public static string Build(string executable, IEnumerable<string> arguments)
        {
            var builder = new StringBuilder();
            Append(builder, executable);
            foreach (string argument in arguments)
                Append(builder, argument);
            return builder.ToString();
        }

        static void Append(StringBuilder builder, string argument)
        {
            if (builder.Length > 0)
                builder.Append(' ');

            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                builder.Append(argument);
                return;
            }

            builder.Append('"');
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                // Les barres obliques inverses ne sont spéciales que devant un guillemet.
                builder.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
                builder.Append(c);
                backslashes = 0;
            }

            builder.Append('\\', backslashes * 2);
            builder.Append('"');
        }
    }
}
