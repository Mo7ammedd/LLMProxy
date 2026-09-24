namespace LLMProxy.Domain;

public class GatewayException(string message, string code, int statusCode = 400, string? param = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public string? Param { get; } = param;
    public int? RetryAfterSeconds { get; init; }
    public string ErrorType => StatusCode switch
    {
        401 => "authentication_error",
        403 => "permission_error",
        429 => "rate_limit_error",
        >= 500 => "server_error",
        _ => "invalid_request_error"
    };
}

// Messages are generated locally. Never put an upstream response body or credentials here.
public sealed class ProviderException(string code, bool isTransient, int statusCode = 502)
    : GatewayException("The upstream provider could not complete the request.", code, statusCode)
{
    public bool IsTransient { get; } = isTransient;
}
