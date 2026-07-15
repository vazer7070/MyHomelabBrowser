using MyHomelabBrowser.classes.Support.Models;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.Support.Transports
{
    /// <summary>
    /// Transport historique conservé uniquement pendant la migration vers l'API.
    /// À supprimer avec les webhooks dès que le backend de support est validé.
    /// </summary>
    public sealed class LegacyDiscordSupportTransport : ISupportTransport, IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;

        public LegacyDiscordSupportTransport(HttpClient? httpClient = null)
        {
            if (httpClient != null)
            {
                _httpClient = httpClient;
                _ownsHttpClient = false;
            }
            else
            {
                _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                _ownsHttpClient = true;
            }
        }

        public string Name => "Support historique Discord";

        public async Task<SupportSubmissionResult> SendAsync(
            SupportReportRequest report,
            SupportAttachment? attachment,
            CancellationToken cancellationToken = default)
        {
            string webhook = LegacyDiscordWebhooks.GetWebhook(report.Category);
            if (string.IsNullOrWhiteSpace(webhook))
                throw new SupportTransportException("Aucun canal de support historique n’est configuré.");

            var payload = new
            {
                username = "PommeBrowser Support",
                embeds = new[]
                {
                    new
                    {
                        title = $"{GetModuleIcon(report.Module)} {report.ModuleLabel} — {report.CategoryLabel}",
                        description =
$"""
🆔 **ID rapport :** `{report.ClientReportId}`
🧭 **Version navigateur :** `{report.ClientVersion}`
🧩 **Module :** {report.ModuleLabel}
🏷️ **Type :** {report.CategoryLabel}

**Titre :** {report.Title}

**Description :**
{report.Description}
""",
                        color = GetModuleColor(report.Module),
                        fields = new[]
                        {
                            new
                            {
                                name = "Informations techniques",
                                value = LimitForDiscordField(report.TechnicalInformation),
                                inline = false
                            }
                        },
                        footer = new { text = "PommeBrowser – Support utilisateur" },
                        timestamp = report.CreatedAtUtc
                    }
                }
            };

            HttpResponseMessage response;
            if (attachment is { IsEmpty: false })
            {
                using var form = new MultipartFormDataContent();
                form.Add(
                    new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                    "payload_json");

                var fileContent = new ByteArrayContent(attachment.Content);
                fileContent.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(
                    attachment.ContentType);
                form.Add(fileContent, "file", attachment.FileName);

                response = await _httpClient.PostAsync(webhook, form, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                string json = JsonSerializer.Serialize(payload);
                response = await _httpClient.PostAsync(
                    webhook,
                    new StringContent(json, Encoding.UTF8, "application/json"),
                    cancellationToken).ConfigureAwait(false);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync(cancellationToken)
                        .ConfigureAwait(false);
                    string details = string.IsNullOrWhiteSpace(body)
                        ? response.ReasonPhrase ?? "Réponse inconnue"
                        : body.Trim();

                    if (details.Length > 300)
                        details = details[..300];

                    throw new SupportTransportException(
                        $"Erreur HTTP {(int)response.StatusCode} : {details}");
                }
            }

            return new SupportSubmissionResult
            {
                Success = true,
                ReportId = report.ClientReportId,
                Message = "Le rapport a été transmis au support.",
                Channel = SupportDeliveryChannel.LegacyDiscord,
                UsedFallback = false
            };
        }

        private static string GetModuleIcon(string module) => module switch
        {
            "adblock" => "🛡️",
            "cloudtorrent" => "☁️",
            _ => "🌐"
        };

        private static int GetModuleColor(string module) => module switch
        {
            "adblock" => 0x3973C6,
            "cloudtorrent" => 0x2F80ED,
            _ => 0xB03030
        };

        private static string LimitForDiscordField(string? value)
        {
            const int maxLength = 1024;
            string text = string.IsNullOrWhiteSpace(value)
                ? "Aucune information technique jointe."
                : value.Trim();

            return text.Length <= maxLength
                ? text
                : text[..(maxLength - 1)] + "…";
        }

        public void Dispose()
        {
            if (_ownsHttpClient)
                _httpClient.Dispose();
        }
    }

    internal static class LegacyDiscordWebhooks
    {
        public static string GetWebhook(string category)
        {
            return category switch
            {
                "bug" => "https://discord.com/api/webhooks/1466856163688845413/SNN4ZMroiwUejaUgIHDLtbvb8ovVebpT1tigZjuS_zGIc2CE0SLkT78cIJH3NS_gDxC4",
                "missing_feature" => "https://discord.com/api/webhooks/1466856296002228235/HguCY5QSmLSzbVaomvsWuJGusjmg8dK_axxkBGWYCmIMXIszVPO6QKKNBrtvc2x-iiTo",
                "feature_request" => "https://discord.com/api/webhooks/1466856426881421364/Q9tepiS8kF1jV0wetkr4TKtN0vZNOP_5FRlEwzeycfbzu_1_kvZbciYiR1toLjCkL19L",
                "ui_ux" => "https://discord.com/api/webhooks/1466856539980566764/Zo5Q8BRkNH2XxrZmQ2jneF3GSDsdvpVco-JxggullC8XiCxrKGHBayvY5y6lC4S_mywg",
                "performance" => "https://discord.com/api/webhooks/1466856656121106614/2xq3neD_NLWcWbHmX37HyMHq90utWOtY-wlVhBoSUXmJakBL01o6BC6pVxEAdFbWAIrX",
                _ => "PommeBrowser"
            };
        }
    }
}
