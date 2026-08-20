using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Syren.Server.Services;
using Xunit;

namespace Syren.Server.Tests;

public sealed class SnapCastServiceTests
{
    [Fact]
    public async Task JsonRpcErrorIsFailure()
    {
        var handler = new StubHttpMessageHandler(request =>
            StubHttpMessageHandler.Json("""{"jsonrpc":"2.0","id":"ignored","error":{"code":-1,"message":"Client not found"}}"""));
        var service = CreateService(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetClientVolumeAsync("client", 20));
    }

    [Fact]
    public async Task ValidResultIsAccepted()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            string requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            string requestId = JsonDocument.Parse(requestBody).RootElement.GetProperty("id").GetString()!;
            return StubHttpMessageHandler.Json(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = requestId,
                result = new { },
            }));
        });
        var service = CreateService(handler);

        await service.SetClientVolumeAsync("client", 20);
    }

    [Fact]
    public async Task MalformedJsonIsFailure()
    {
        var service = CreateService(new StubHttpMessageHandler(request =>
            StubHttpMessageHandler.Json("not json")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetClientVolumeAsync("client", 20));
    }

    private static SnapCastService CreateService(HttpMessageHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("http://snapserver/") },
        NullLogger<SnapCastService>.Instance
    );
}
