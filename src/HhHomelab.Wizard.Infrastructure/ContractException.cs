using System.Net;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Safe diagnostic shared by API adapters. Contains only a fixed code, HTTP status
/// and exchange classification. Contract does not prove rejection before a mutation.</summary>
public abstract class ContractException : InvalidOperationException
{
    protected ContractException(
        string code,
        HttpStatusCode? statusCode,
        ContractFailureKind kind
    ) : base(code)
    {
        Code = code;
        StatusCode = statusCode;
        Kind = kind;
    }

    public string Code { get; }

    public HttpStatusCode? StatusCode { get; }

    public ContractFailureKind Kind { get; }
}
