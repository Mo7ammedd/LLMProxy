using System.Security.Cryptography;
using LLMProxy.Domain;

namespace LLMProxy.Application;

public sealed record OperatorSummary(Guid Id, string Username, string Role, bool Enabled, DateTimeOffset CreatedAt)
{
    public static OperatorSummary From(OperatorAccount value) => new(value.Id, value.Username, value.Role, value.Enabled, value.CreatedAt);
}
public sealed record CreateOperator(string Username, string Password, string Role = OperatorRoles.Operator);
public sealed record UpdateOperator(bool Enabled, string Role, string? Password = null);
public sealed record OperatorLogin(string Username, string Password);
public sealed record OperatorLoginResult(string Token, DateTimeOffset ExpiresAt, OperatorSummary Operator);

public sealed class OperatorService(IManagementStore store, TimeProvider time)
{
    private const int Iterations = 600_000;
    private static readonly string DummyHash = $"v1:{Convert.ToBase64String(new byte[24])}:{Convert.ToBase64String(new byte[32])}";

    public async Task<OperatorLoginResult> LoginAsync(OperatorLogin request, CancellationToken cancellationToken)
    {
        var username = request.Username?.Trim().ToLowerInvariant() ?? "";
        var account = username.Length <= 128 ? await store.FindOperatorAsync(username, cancellationToken) : null;
        var matches = Verify(request.Password, account?.PasswordHash ?? DummyHash);
        if (!matches || account is not { Enabled: true })
            throw new GatewayException("Invalid operator credentials.", "invalid_credentials", 401);
        var token = "llmp_op_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expires = time.GetUtcNow().AddHours(8);
        await store.SaveSessionAsync(new OperatorSession { TokenHash = ApiKeyHasher.Hash(token), OperatorId = account.Id, ExpiresAt = expires }, cancellationToken);
        return new(token, expires, OperatorSummary.From(account));
    }

    public async Task<OperatorSummary> CreateAsync(CreateOperator command, CancellationToken cancellationToken)
    {
        var username = command.Username?.Trim().ToLowerInvariant() ?? "";
        if (username.Length is < 1 or > 128 || !username.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@'))
            throw new GatewayException("Use 1–128 letters, digits or . _ - @ in the username.", "invalid_username");
        ValidateRole(command.Role);
        var account = new OperatorAccount { Username = username, Role = command.Role, PasswordHash = Hash(command.Password), CreatedAt = time.GetUtcNow() };
        await store.SaveOperatorAsync(account, true, cancellationToken);
        return OperatorSummary.From(account);
    }

    public async Task<OperatorSummary> UpdateAsync(Guid id, UpdateOperator command, CancellationToken cancellationToken)
    {
        ValidateRole(command.Role);
        var account = (await store.ListOperatorsAsync(cancellationToken)).FirstOrDefault(x => x.Id == id)
            ?? throw new GatewayException("Operator not found.", "not_found", 404);
        account.Enabled = command.Enabled;
        account.Role = command.Role;
        if (command.Password is not null) account.PasswordHash = Hash(command.Password);
        await store.SaveOperatorAsync(account, false, cancellationToken);
        return OperatorSummary.From(account);
    }

    private static void ValidateRole(string role)
    {
        if (!OperatorRoles.Valid(role)) throw new GatewayException("Use administrator, operator or auditor.", "invalid_role");
    }
    private static string Hash(string? password)
    {
        if (password is not { Length: >= 12 and <= 1024 }) throw new GatewayException("Passwords must contain 12–1024 characters.", "invalid_password");
        var salt = RandomNumberGenerator.GetBytes(24);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"v1:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }
    private static bool Verify(string? password, string encoded)
    {
        if (password is null || password.Length > 1024) return false;
        var parts = encoded.Split(':');
        if (parts.Length != 3 || parts[0] != "v1") return false;
        try
        {
            var salt = Convert.FromBase64String(parts[1]);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(parts[2]));
        }
        catch (FormatException) { return false; }
    }
}
