using System.Text.Json;
using LLMProxy.Application;
using LLMProxy.Domain;

namespace LLMProxy.UnitTests;

public sealed class AuthenticationTests
{
    [Fact]
    public async Task Keys_are_random_and_only_hashes_are_persisted()
    {
        var store = new TestStore();
        var clock = new TestClock();
        var service = new ApiKeyService(store, new ModelRegistry(TestData.Options()), clock);
        var first = await service.CreateAsync(new CreateApiKey("owner", ["fast"]), default);
        var second = await service.CreateAsync(new CreateApiKey("owner", ["fast"]), default);
        Assert.StartsWith("llmp_sk_", first.Key);
        Assert.Equal(51, first.Key.Length);
        Assert.NotEqual(first.Key, second.Key);
        Assert.DoesNotContain(first.Key, JsonSerializer.Serialize(store.Keys));
        Assert.True(ApiKeyHasher.Matches(first.Key, store.Keys[first.Details.Id].KeyHash));
        Assert.False(ApiKeyHasher.Matches(second.Key, store.Keys[first.Details.Id].KeyHash));
        Assert.NotNull(await service.AuthenticateAsync(first.Key, default));
        Assert.Equal(clock.GetUtcNow(), store.Keys[first.Details.Id].LastUsedAt);
    }

    [Fact]
    public async Task Disabled_and_unknown_keys_are_rejected()
    {
        var store = new TestStore();
        var service = new ApiKeyService(store, new ModelRegistry(TestData.Options()), new TestClock());
        var created = await service.CreateAsync(new CreateApiKey("owner", ["fast"]), default);
        await service.UpdateAsync(created.Details.Id, new UpdateApiKey(false, ["fast"]), default);
        Assert.Null(await service.AuthenticateAsync(created.Key, default));
        Assert.Null(await service.AuthenticateAsync(ApiKeyHasher.Generate(), default));
        Assert.Null(await service.AuthenticateAsync("sk-invalid", default));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(60, -1)]
    public async Task Invalid_limits_cannot_be_saved(int rpm, long tokens)
    {
        var service = new ApiKeyService(new TestStore(), new ModelRegistry(TestData.Options()), new TestClock());
        await Assert.ThrowsAsync<GatewayException>(() => service.CreateAsync(new CreateApiKey("owner", ["fast"], rpm, tokens), default));
    }

    [Fact]
    public void Per_model_permissions_are_enforced()
    {
        var validator = new RequestValidator(TestData.Options(), new ModelRegistry(TestData.Options()));
        var key = TestData.Key();
        key.AllowedModels = ["another-model"];
        Assert.Equal(403, Assert.Throws<GatewayException>(() => validator.Validate(TestData.Request(), key)).StatusCode);
        key.AllowedModels = ["*"];
        Assert.NotNull(validator.Validate(TestData.Request(), key));
    }
}
