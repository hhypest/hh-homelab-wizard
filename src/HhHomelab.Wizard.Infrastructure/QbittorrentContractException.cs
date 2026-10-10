using System.Net;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Sanitized diagnostic: never includes a URL, credentials or response body.</summary>
public sealed class QbittorrentContractException : InvalidOperationException
{
    internal QbittorrentContractException(string code, HttpStatusCode? statusCode = null) : base(code)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }
    public HttpStatusCode? StatusCode { get; }
}
