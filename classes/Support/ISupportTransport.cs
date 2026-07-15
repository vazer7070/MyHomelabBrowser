using MyHomelabBrowser.classes.Support.Models;
using System.Threading;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.Support
{
    public interface ISupportTransport
    {
        string Name { get; }

        Task<SupportSubmissionResult> SendAsync(
            SupportReportRequest report,
            SupportAttachment? attachment,
            CancellationToken cancellationToken = default);
    }
}
