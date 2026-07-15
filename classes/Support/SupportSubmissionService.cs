using MyHomelabBrowser.classes.Support.Models;
using MyHomelabBrowser.classes.Support.Transports;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.Support
{
    /// <summary>
    /// Routeur unique du support. L'interface ne connaît plus Discord ni l'API.
    /// Le backend sera automatiquement utilisé dès qu'une URL valide sera fournie
    /// dans SupportApiConfiguration ou POMMEBROWSER_SUPPORT_API_URL.
    /// </summary>
    public sealed class SupportSubmissionService : IDisposable
    {
        private readonly SupportApiTransport _apiTransport;
        private readonly LegacyDiscordSupportTransport _legacyTransport;
        private bool _disposed;

        public SupportSubmissionService(
            SupportApiTransport? apiTransport = null,
            LegacyDiscordSupportTransport? legacyTransport = null)
        {
            _apiTransport = apiTransport ?? new SupportApiTransport();
            _legacyTransport = legacyTransport ?? new LegacyDiscordSupportTransport();
        }

        public async Task<SupportSubmissionResult> SendAsync(
            SupportReportRequest report,
            SupportAttachment? attachment,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(report);

            if (_apiTransport.CanAttempt)
            {
                try
                {
                    return await _apiTransport.SendAsync(report, attachment, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (SupportApiUnavailableException) when (
                    SupportApiConfiguration.AllowLegacyDiscordFallback)
                {
                    SupportSubmissionResult fallback = await _legacyTransport
                        .SendAsync(report, attachment, cancellationToken)
                        .ConfigureAwait(false);

                    return new SupportSubmissionResult
                    {
                        Success = fallback.Success,
                        ReportId = fallback.ReportId,
                        Message = fallback.Message,
                        Channel = fallback.Channel,
                        UsedFallback = true
                    };
                }
            }

            if (!SupportApiConfiguration.AllowLegacyDiscordFallback)
            {
                throw new SupportTransportException(
                    "Le service de support n’est pas encore disponible.");
            }

            return await _legacyTransport.SendAsync(report, attachment, cancellationToken)
                .ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _apiTransport.Dispose();
            _legacyTransport.Dispose();
        }
    }
}
