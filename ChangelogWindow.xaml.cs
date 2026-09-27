using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser
{
    public partial class ChangelogWindow : Window
    {
        public ChangelogWindow(string version, string changelog)
        {
            InitializeComponent();

            TitleText.Text = Tr("Historique des versions");

            if (string.IsNullOrWhiteSpace(changelog))
                return;

            var normalized = NormalizeChangelogInput(changelog);
            if (!string.IsNullOrWhiteSpace(normalized))
                RenderMultiVersionChangelog(normalized);

        }

        void RenderMultiVersionChangelog(string raw)
        {
            raw = raw.Replace("\r\n", "\n");

            var blocks = raw.Split("## ")
                             .Where(b => !string.IsNullOrWhiteSpace(b))
                             .ToList();

            foreach (var block in blocks)
            {
                var lines = block.Split('\n');
                var versionLine = lines[0].Trim();
                var content = string.Join("\n", lines.Skip(1));

                AddVersionHeader(versionLine);
                RenderSingleVersion(content);
            }
        }

        void AddVersionHeader(string version)
        {
            var title = new TextBlock
            {
                Text = Tr("🚀 Version {0}", version),
                FontSize = 17,
                FontWeight = FontWeights.Bold
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

            var header = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 20, 0, 6),
                Child = title
            };
            header.SetResourceReference(Border.BackgroundProperty, "SurfaceRaisedBrush");

            ChangelogHost.Children.Add(header);
        }
        string NormalizeChangelogInput(string raw)
        {
            // Si c’est du JSON → on le transforme en markdown-like
            if (raw.TrimStart().StartsWith("{"))
            {
                try
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(raw);
                    if (dict == null || dict.Count == 0)
                        return "";

                    var sb = new StringBuilder();

                    foreach (var v in dict
                        .Select(kv =>
                        {
                            var ok = Version.TryParse(kv.Key, out var ver);
                            return new { kv.Key, Items = kv.Value, Parsed = ok ? ver : null };
                        })
                        .Where(x => x.Parsed != null)
                        .OrderByDescending(x => x.Parsed))
                    {
                        sb.AppendLine($"## {v.Key}");

                        if (v.Items != null)
                        {
                            foreach (var item in v.Items)
                                sb.AppendLine(item);
                        }

                        sb.AppendLine();
                    }

                    return sb.ToString();
                }
                catch
                {
                    return "";
                }
            }

            // Sinon → on suppose que c’est déjà du texte
            return raw;
        }


        void RenderSingleVersion(string content)
        {
            content = content.Replace("\r\n", "\n");

            var sections = new Dictionary<string, (string icon, string title)>
            {
                ["add"] = ("✨", "Ajouts"),
                ["added"] = ("✨", "Ajouts"),
                ["new"] = ("✨", "Ajouts"),

                ["fix"] = ("🐛", "Corrections"),
                ["fixed"] = ("🐛", "Corrections"),

                ["change"] = ("🔧", "Modifications"),
                ["changed"] = ("🔧", "Modifications"),

                ["misc"] = ("📦", "Divers"),
                ["other"] = ("📦", "Divers")
            };

            string? currentSection = null;

            foreach (var line in content.Split('\n'))
            {
                var trimmed = line.Trim().TrimStart('-', '•');
                if (trimmed.Length == 0)
                    continue;

                var lower = trimmed.ToLowerInvariant();

                var section = sections
                    .FirstOrDefault(s => lower.StartsWith(s.Key + ":"));

                if (!section.Equals(default(KeyValuePair<string, (string, string)>)))
                {
                    currentSection = section.Key;
                    AddSection(section.Value.icon, section.Value.title);
                    AddBullet(trimmed.Substring(section.Key.Length + 1).Trim());
                }
                else
                {
                    AddBullet(trimmed);
                }
            }
        }


        void AddSection(string icon, string title)
        {
            var text = new TextBlock
            {
                Text = $"{icon} {title}",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 16, 0, 6)
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            ChangelogHost.Children.Add(text);
        }

        void AddBullet(string text)
        {
            var bullet = new TextBlock
            {
                Text = "• " + text,
                FontSize = 14,
                Margin = new Thickness(12, 2, 0, 2),
                TextWrapping = TextWrapping.Wrap
            };
            bullet.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            ChangelogHost.Children.Add(bullet);
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}