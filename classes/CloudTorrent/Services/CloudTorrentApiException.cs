using System;
using System.Net;

namespace MyHomelabBrowser.classes.CloudTorrent.Services
{
    public sealed class CloudTorrentApiException : Exception
    {
        public CloudTorrentApiException(
            string message,
            HttpStatusCode? statusCode = null,
            string? errorCode = null,
            Exception? innerException = null)
            : base(message, innerException)
        {
            StatusCode = statusCode;
            ErrorCode = errorCode;
        }

        public HttpStatusCode? StatusCode { get; }
        public string? ErrorCode { get; }
    }
}
