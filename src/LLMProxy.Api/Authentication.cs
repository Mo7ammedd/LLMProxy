using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using LLMProxy.Application;
using LLMProxy.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LLMProxy.Api;

public sealed class GatewayKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, ApiKeyService keys) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "GatewayKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var raw = ReadBearer(Request.Headers.Authorization);
        if (raw is null) return AuthenticateResult.NoResult();
        var key = await keys.AuthenticateAsync(raw, Context.RequestAborted);
        if (key is null) return AuthenticateResult.Fail("Invalid API key.");
        Context.Items[RequestContext.ApiKeyItem] = key;
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, key.Id.ToString()), new Claim(ClaimTypes.Name, key.Owner)], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return ApiErrors.WriteAsync(Context, new GatewayException("A valid gateway API key is required.", "invalid_api_key", 401));
    }
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => ApiErrors.WriteAsync(Context,
        new GatewayException("This API key is not permitted to perform the request.", "permission_denied", 403));

    internal static string? ReadBearer(string? value) => value is { Length: <= 256 }
        && AuthenticationHeaderValue.TryParse(value, out var header)
        && header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(header.Parameter) ? header.Parameter : null;
}

public sealed class AdminKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, ApiOptions apiOptions, IManagementStore store, TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AdminKey";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var raw = GatewayKeyAuthenticationHandler.ReadBearer(Request.Headers.Authorization);
        if (raw is null) return AuthenticateResult.NoResult();
        var matches = apiOptions.AdminKey.Length > 0 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(raw)),
            SHA256.HashData(Encoding.UTF8.GetBytes(apiOptions.AdminKey)));
        if (matches)
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "bootstrap"),
                new Claim(ClaimTypes.Name, "bootstrap"), new Claim(ClaimTypes.Role, OperatorRoles.Administrator)], SchemeName);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
        }
        var account = raw.StartsWith("llmp_op_", StringComparison.Ordinal)
            ? await store.AuthenticateOperatorAsync(ApiKeyHasher.Hash(raw), time.GetUtcNow(), Context.RequestAborted) : null;
        if (account is null) return AuthenticateResult.Fail("Invalid operator session.");
        var operatorIdentity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
            new Claim(ClaimTypes.Name, account.Username), new Claim(ClaimTypes.Role, account.Role)], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(operatorIdentity), SchemeName));
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties) => ApiErrors.WriteAsync(Context,
        new GatewayException("An operator session or administrative key is required.", "invalid_admin_key", 401));
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => ApiErrors.WriteAsync(Context,
        new GatewayException("Your operator role does not allow this action.", "permission_denied", 403));
}
