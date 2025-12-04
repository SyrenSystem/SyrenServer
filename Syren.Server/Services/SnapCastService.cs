using Microsoft.Extensions.Options;
using Syren.Server.Configuration;
using Syren.Server.Models;
using Syren.Server.Models.SnapCast;

namespace Syren.Server.Services;

public class SnapCastService : ISnapCastService
{
    private readonly HttpClient _httpClient;
    private readonly SnapCastOptions _options;
    private readonly ILogger<SnapCastService> _logger;

    public SnapCastService(
        IOptions<SnapCastOptions> options,
        ILogger<SnapCastService> logger,
        HttpClient httpClient)
    {
        _options = options.Value;
        _logger = logger; 

        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri($"http://{_options.ServerHost}:{_options.HttpPort}");
    }

    public async Task<SystemStatus?> GetStatusAsync()
    {
        _logger.LogTrace("Getting SnapCast status");

        HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
            "jsonrpc",
            new JsonRpcRequest
            {
                RequestId = "getStatusRequest",
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

        try
        {
            return response.Content.ReadFromJsonAsync<JsonRpcResponse<GetStatusResult>>()
                .Result
                .Result
                .SystemStatus;
        } catch (Exception e)
        {
            _logger.LogError("Failed to get SnapCast status: {ErrorMsg}", e);
            return null;
        }
    }

    public async Task SetClientVolumeAsync(string id, int percent)
    {
        id = id.ToLower();
        _logger.LogTrace("Setting SnapClient \"{Id}\" volume to {Percent}%", id, percent);

        HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
            "jsonrpc",
            new JsonRpcRequest<SetVolumeRequest>
            {
                RequestId = "setVolumeRequest",
                JsonRpcVersion = "2.0",
                Method = SetVolumeRequest.MethodName,
                Parameters = new SetVolumeRequest
                {
                    Id = id,
                    Volume = new Volume
                    {
                        Muted = false,
                        Percentage = percent,
                    },
                },
            }
        );

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to set SnapClient \"{Id}\" volume to {Percent}%", id, percent);
            return;
        }

        _logger.LogInformation(await response.Content.ReadAsStringAsync());

        return;
    }

    public async Task<double?> GetClientVolume(string id)
    {
        _logger.LogTrace("Getting SnapClient volumes");

        var snapStatus = await GetStatusAsync();
        if (!snapStatus.HasValue) return null;

        var client = snapStatus.Value.Clients()
            .Select(client => (ClientStatus?)client)
            .FirstOrDefault(client => client?.Id == id, null);

        return client.HasValue && !client.Value.Config.Volume.Muted ?
            client.Value.Config.Volume.Percentage / 100.0 : 0.0;
    }
}
