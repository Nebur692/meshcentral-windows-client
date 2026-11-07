namespace MeshCentralClient.Core;

/// <summary>Why a connection or a request failed. / Por qué falló una conexión o una petición.</summary>
public enum MeshFailure
{
    InvalidAddress,
    InvalidCredentials,
    TwoFactorRequired,
    InvalidTwoFactorCode,
    TooManyAttempts,
    ToolsNotAllowed,
    EmailValidationRequired,
    UntrustedCertificate,
    NameNotResolved,
    ServerUnreachable,
    HttpError,
    Rejected,
    Timeout,
    ConnectionLost,
    UnexpectedResponse,
    /// <summary>A single sign-on session was refused or has expired. / Una sesión de inicio único se rechazó o caducó.</summary>
    SessionExpired,
}

/// <summary>
/// A failure with its burburito key, so the interface can show the reference code.
/// Un fallo con su clave burburito, para que la interfaz pueda enseñar el código de referencia.
/// </summary>
public sealed class MeshClientException : Exception
{
    public MeshClientException(MeshFailure failure, string burburitoKey, string message, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
        BurburitoKey = burburitoKey;
    }

    public MeshFailure Failure { get; }
    public string BurburitoKey { get; }
    public string BurburitoCode => Burburito.Code(BurburitoKey);

    /// <summary>Raw message from the server, if any. / Mensaje tal cual del servidor, si lo hay.</summary>
    public string? ServerMessage { get; init; }
    public int? HttpStatus { get; init; }
    public string? CertificateSha256 { get; init; }
    public string? CertificateSubject { get; init; }

    /// <summary>Second-factor channels the server offers. / Canales de segundo factor que ofrece el servidor.</summary>
    public bool EmailCodeAvailable { get; init; }
    public bool SmsCodeAvailable { get; init; }
    public bool EmailCodeSent { get; init; }
}
