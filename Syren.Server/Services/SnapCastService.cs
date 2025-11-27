using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Syren.Server.Configuration;
using Syren.Server.Models.SnapCast;

namespace Syren.Server.Services;

public class SnapCastService : ISnapCastService
{
    private readonly HttpClient _httpClient;
    private readonly SnapCastOptions _options;
    private readonly ILogger<SnapCastService> _logger;

    public SnapCastService(
        IOptions<SnapCastOptions> options,
        ILogger<SnapCastService> logger)
    {
        _options = options.Value;
        _logger = logger; 

        _httpClient = new()
        {
            BaseAddress = new Uri($"http://{_options.ServerHost}:{_options.HttpPort}"),
        };
    }

    public async Task<Status?> GetStatusAsync()
    {
        _logger.LogTrace("Getting SnapCast status");

        HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
            "jsonrpc",
            new GetStatusRequest
            {
                Id = 1,
                JsonRpcVersion = "2.0",
                Method = "Server.GetStatus",
            }
        );

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to get SnapCast status!");
            return null;
        }

        _logger.LogInformation("SnapCast status:\n{Status}", await response.Content.ReadAsStringAsync());

        return await response.Content.ReadFromJsonAsync<Status>();
    }

    public async Task SetVolumeAsync(int percent, bool muted)
    {
        _logger.LogTrace("Setting SnapCast volume to {Percent}%; muting: {Muted}", percent, muted);

        StringContent jsonContent = new(
            JsonSerializer.Serialize(new
            {
                muted = muted,
                percent = percent,
            }),
            Encoding.UTF8,
            "application/json"
        );

        HttpResponseMessage response = await _httpClient.PostAsync(
            "jsonrpc",
            jsonContent
        );

        _logger.LogInformation(await response.Content.ReadAsStringAsync());

        return;
    }
}
