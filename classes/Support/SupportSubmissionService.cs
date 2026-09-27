using MyHomelabBrowser.classes.Support.Models;
using MyHomelabBrowser.classes.Support.Transports;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.Support
{
    /// <summary>
    /// Routeur unique du support. Le service en ligne est utilisé dès qu'une URL valide
    /// est fournie dans SupportApiConfiguration ou POMMEBROWSER_SUPPORT_API_URL ; sinon,
    /// ou s'il est injoignable, le rapport est enregistré localement.
    /// </summary>
    public sealed class SupportSubmissionService : IDisposable
    {
        private readonly SupportApiTransport _apiTransport;
        private readonly ISupportTransport _localTransport;
        private bool _disposed;

        public SupportSubmissionService(
            SupportApiTransport? apiTransport = null,
            ISupportTransport? localTransport = null)
        {
            _apiTransport = apiTransport ?? new SupportApiTransport();
            _localTransport = localTransport ?? new LocalSupportExportTransport();
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
                catch (SupportApiUnavailableException)
                {
                    SupportSubmissionResult local = await _localTransport
                        .SendAsync(report, attachment, cancellationToken)
                        .ConfigureAwait(false);

                    return new SupportSubmissionResult
                    {
                        Success = local.Success,
                        ReportId = local.ReportId,
                        Message = local.Message,
                        Channel = local.Channel,
                        FilePath = local.FilePath,
                        UsedFallback = true
                    };
                }
            }

            return await _localTransport.SendAsync(report, attachment, cancellationToken)
                .ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _apiTransport.Dispose();
        }
    }
}
