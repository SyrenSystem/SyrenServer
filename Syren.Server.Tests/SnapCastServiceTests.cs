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
        var service = CreateService(EchoingHandler(requestId => JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = requestId,
            result = new { },
        })));

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

    [Fact]
    public async Task NullResultIsFailure()
    {
        var service = CreateService(EchoingHandler(requestId =>
            $$"""{"jsonrpc":"2.0","id":"{{requestId}}","result":null}"""));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetClientVolumeAsync("client", 20));
    }

    [Fact]
    public async Task MissingResultIsFailure()
    {
        var service = CreateService(EchoingHandler(requestId =>
            $$"""{"jsonrpc":"2.0","id":"{{requestId}}"}"""));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetClientVolumeAsync("client", 20));
    }

    [Fact]
    public async Task TimeoutBecomesSnapServerUnavailable()
    {
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return StubHttpMessageHandler.Json("{}");
        });
        var service = CreateService(handler, timeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<SnapServerUnavailableException>(() =>
            service.SetClientVolumeAsync("client", 20));
    }

    [Fact]
    public async Task ConnectionFailureBecomesSnapServerUnavailable()
    {
        var service = CreateService(new StubHttpMessageHandler(request =>
            throw new HttpRequestException("Connection refused")));

        await Assert.ThrowsAsync<SnapServerUnavailableException>(() =>
            service.SetClientVolumeAsync("client", 20));
    }

    [Fact]
    public async Task CallerCancellationIsNotWrapped()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            await cancellation.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return StubHttpMessageHandler.Json("{}");
        });
        var service = CreateService(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SetClientVolumeAsync("client", 20, cancellation.Token));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("stream_id")]
    public async Task AddStreamAcceptsSupportedResponseFields(string responseField)
    {
        var service = CreateService(EchoingHandler(requestId => JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = requestId,
            result = new Dictionary<string, string> { [responseField] = "priority" },
        })));

        string streamId = await service.AddStreamAsync("meta:///spotify/laptop?name=priority");

        Assert.Equal("priority", streamId);
    }

    private static StubHttpMessageHandler EchoingHandler(Func<string, string> body) => new(request =>
    {
        string requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        string requestId = JsonDocument.Parse(requestBody).RootElement.GetProperty("id").GetString()!;
        return StubHttpMessageHandler.Json(body(requestId));
    });

    private static SnapCastService CreateService(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://snapserver/") };
        if (timeout.HasValue)
        {
            httpClient.Timeout = timeout.Value;
        }
        return new SnapCastService(httpClient, NullLogger<SnapCastService>.Instance);
    }
}
