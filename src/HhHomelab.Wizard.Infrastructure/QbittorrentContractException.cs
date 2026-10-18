using System.Net;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Sanitized diagnostic: never includes a URL, credentials or response body.</summary>
public sealed class QbittorrentContractException : ContractException
{
    internal QbittorrentContractException(
        string code,
        HttpStatusCode? statusCode = null,
        ContractFailureKind kind = ContractFailureKind.Contract
    )
        : base(code, statusCode, kind) { }
}
