using System;
using System.Net;

namespace MyHomelabBrowser.classes.Support
{
    public class SupportTransportException : Exception
    {
        public SupportTransportException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// L'API n'est pas encore présente ou est temporairement inaccessible.
    /// Pendant la migration, ce type d'erreur peut déclencher le transport legacy.
    /// </summary>
    public sealed class SupportApiUnavailableException : SupportTransportException
    {
        public SupportApiUnavailableException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Le backend a bien répondu mais a refusé le rapport. Cette erreur ne doit pas
    /// être contournée par le fallback, notamment en cas de rate-limit ou de rejet.
    /// </summary>
    public sealed class SupportApiRejectedException : SupportTransportException
    {
        public HttpStatusCode StatusCode { get; }

        public SupportApiRejectedException(HttpStatusCode statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }
    }
}
