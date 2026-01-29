using MyHomelabBrowser.classes;
using System;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace MyHomelabBrowser.controles
{
    public partial class ReportIssueView : UserControl
    {
        public event Action? CloseRequested;

        static DateTime _lastSend = DateTime.MinValue;
        static bool _sending = false;

        readonly ReportIssueOptions _options;

        public ReportIssueView(ReportIssueOptions options)
        {
            InitializeComponent();

            _options = new ReportIssueOptions
            {
                IncludeLogs = options.IncludeLogs,
                IncludePcInfo = options.IncludePcInfo,
                IncludeMode = options.IncludeMode,
                Context = options.Context
            };
        }

        public void Cancel_Click(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke();
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {

            var appVersion =
    System.Reflection.Assembly
        .GetExecutingAssembly()
        .GetName()
        .Version?
        .ToString()
    ?? "inconnue"; 

            if (_sending)
                return;

            if (DateTime.UtcNow - _lastSend < TimeSpan.FromSeconds(10))
            {
                ShowDialog("Trop rapide",
                    "Merci d’attendre quelques secondes avant un nouvel envoi.");
                return;
            }
            var extra = new StringBuilder();

            if (_options.IncludeLogs)
                extra.AppendLine("📄 Logs récents : inclus");

            if (_options.IncludePcInfo)
            {
                extra.AppendLine("💻 Informations PC :");
                extra.AppendLine($"- OS : {Environment.OSVersion}");
            }

            if (_options.IncludeMode)
            {
                var ctx = _options.Context;

                if (ctx == null)
                {
                    extra.AppendLine("🧭 Mode navigateur : inconnu");
                }
                else
                {
                    string mode =
                        ctx.IsLegacy ? "legacy" :
                        ctx.IsPrivate ? "privé" :
                        "normal";

                    extra.AppendLine($"🧭 Mode navigateur : {mode}");
                    extra.AppendLine($"⚡ Flash : {ctx.FlashMode}");

                    if (!string.IsNullOrWhiteSpace(ctx.CurrentUrl))
                        extra.AppendLine($"🌐 URL : {ctx.CurrentUrl}");

                    if (!string.IsNullOrWhiteSpace(ctx.PageTitle))
                        extra.AppendLine($"📝 Titre : {ctx.PageTitle}");

                    if (ctx.TabId != null)
                        extra.AppendLine($"🆔 Onglet : {ctx.TabId}");
                }
            }

            string extraBlock = extra.Length > 0
                ? "\n\n**Informations jointes :**\n" + extra
                : "";
            var title = TitleBox.Text.Trim();
            var desc = DescriptionBox.Text.Trim();
            var issueType = GetSelectedIssueType();

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(desc))
            {
                ShowDialog("Champs manquants",
                    "Merci de remplir le titre et la description avant l’envoi.");
                return;
            }

            if (desc.Length > 3500)
            {
                ShowDialog("Message trop long",
                    "La description est trop longue pour être envoyée.");
                return;
            }

            var confirm = MessageBox.Show(
                "Confirmer l’envoi du message.?",
                "Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
                return;

            var webhook = DiscordWebhooks.GetWebhook(issueType);
            if (string.IsNullOrWhiteSpace(webhook))
            {
                ShowDialog("Erreur interne",
                    "Aucun webhook n’est configuré pour ce type de demande.");
                return;
            }

            // 🆔 ID unique du rapport
            var reportId = GenerateReportId();

            SetSendingState(true);
            _sending = true;
            _lastSend = DateTime.UtcNow;


            var payload = new
            {
                embeds = new[]
                {
        new
        {
            title = $"📣 {issueType}",
           description =
$"""
🆔 **ID rapport :** `{reportId}`
🧭 **Version navigateur :** `{appVersion}`

**Titre :** {title}
**Type :** {issueType}

**Description :**
{desc}
{extraBlock}
""",
            color = 0xB03030,
            footer = new
            {
                text = "PommeBrowser – Rapport utilisateur"
            }
        }
    }
            };

            try
            {
                using var http = new HttpClient();

                HttpResponseMessage res;

                if (_options.IncludeLogs)
                {
                    var logs = RuntimeLogBuffer.GetSnapshot();

                    if (string.IsNullOrWhiteSpace(logs))
                    {
                        logs =
            $"""
PommeBrowser – Runtime snapshot
Version : {appVersion}
Date    : {DateTime.Now:yyyy-MM-dd HH:mm:ss}

Aucun log console n’a été généré pour cette session.
(Application WPF – Console non utilisée)
""";
                    }

                    var tempFile = Path.Combine(
                        Path.GetTempPath(),
                        $"pommebrowser-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

                    File.WriteAllText(tempFile, logs, Encoding.UTF8);

                    using var form = new MultipartFormDataContent();

                    form.Add(
                        new StringContent(
                            System.Text.Json.JsonSerializer.Serialize(payload),
                            Encoding.UTF8,
                            "application/json"),
                        "payload_json");

                    form.Add(
                        new ByteArrayContent(File.ReadAllBytes(tempFile)),
                        "file",
                        Path.GetFileName(tempFile));

                    res = await http.PostAsync(webhook, form);

                    File.Delete(tempFile);
                }
                else
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(payload);
                    res = await http.PostAsync(
                        webhook,
                        new StringContent(json, Encoding.UTF8, "application/json"));
                }

                if (!res.IsSuccessStatusCode)
                    throw new Exception("Erreur HTTP " + res.StatusCode);

                // ✅ SUCCÈS CONFIRMÉ ICI SEULEMENT
                ShowDialog(
                    "Message envoyé",
                    $"Merci pour le signalement.\n\nID du rapport : {reportId}");

                CloseRequested?.Invoke();
            }
            catch (Exception ex)
            {
                ShowDialog(
                    "Erreur d’envoi",
                    "Impossible d’envoyer le message.\n\n" + ex.Message);
            }
            finally
            {
                _sending = false;
                SetSendingState(false);
            }
        }
        
        // =====================
        // Helpers
        // =====================

        void SetSendingState(bool sending)
        {
            SendButton.IsEnabled = !sending;
            SendingPanel.Visibility = sending ? Visibility.Visible : Visibility.Collapsed;
        }

        void ShowDialog(string title, string message)
        {
            ThemedDialogWindow.Show(
                Window.GetWindow(this),
                title,
                message);
        }

        static string GenerateReportId()
        {
            var rnd = Guid.NewGuid().ToString("N")[..4].ToUpper();
            return $"PB-{DateTime.UtcNow:yyyyMMdd}-{rnd}";
        }

        public enum IssueType
        {
            Bug,
            MissingFeature,
            FeatureRequest,
            UiUx,
            Performance,
            Other
        }

        IssueType GetSelectedIssueType()
        {
            if (TypeBox.SelectedItem is ComboBoxItem item &&
                Enum.TryParse<IssueType>(item.Tag?.ToString(), out var type))
                return type;

            return IssueType.Other;
        }
    }

    static class DiscordWebhooks
    {
        public static string GetWebhook(ReportIssueView.IssueType type)
        {
            return type switch
            {
                ReportIssueView.IssueType.Bug =>
                    "https://discord.com/api/webhooks/1175892971103723520/JfZC2bVlenFQDghAqi8OJLqbFikpT_rBZU8VzDmY9U722H7rAXVsP-id6fjTjkI-OwOE",

                ReportIssueView.IssueType.MissingFeature =>
                    "https://discord.com/api/webhooks/1175892971103723520/JfZC2bVlenFQDghAqi8OJLqbFikpT_rBZU8VzDmY9U722H7rAXVsP-id6fjTjkI-OwOE",

                ReportIssueView.IssueType.FeatureRequest =>
                    "https://discord.com/api/webhooks/1175892971103723520/JfZC2bVlenFQDghAqi8OJLqbFikpT_rBZU8VzDmY9U722H7rAXVsP-id6fjTjkI-OwOE",

                ReportIssueView.IssueType.UiUx =>
                    "https://discord.com/api/webhooks/1175892971103723520/JfZC2bVlenFQDghAqi8OJLqbFikpT_rBZU8VzDmY9U722H7rAXVsP-id6fjTjkI-OwOE",

                ReportIssueView.IssueType.Performance =>
                    "https://discord.com/api/webhooks/1175892971103723520/JfZC2bVlenFQDghAqi8OJLqbFikpT_rBZU8VzDmY9U722H7rAXVsP-id6fjTjkI-OwOE",

                _ =>
                    "https://discord.com/api/webhooks/1175892971103723520/JfZC2bVlenFQDghAqi8OJLqbFikpT_rBZU8VzDmY9U722H7rAXVsP-id6fjTjkI-OwOE",
            };
        }
    }
}