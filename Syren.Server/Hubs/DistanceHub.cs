using Microsoft.AspNetCore.SignalR;
using Syren.Server.Interfaces;
using Syren.Server.Models;

namespace Syren.Server.Hubs;

public class DistanceHub(IDistanceService distanceService) : Hub
{
    private readonly IDistanceService _distanceService = distanceService;

    public void SendDistance(uint speakerId, float distance)
    {
        DistanceData distanceData = new() { SpeakerId = speakerId, Distance = distance };
        _distanceService.UpdateDistances([distanceData]);
    }

    // Expects the user's position to be on the new speaker's location
    public uint AddSpeaker()
    {
        return _distanceService.AddSpeaker().Id;
    }

    public void RemoveSpeaker(uint id)
    {
        _distanceService.RemoveSpeaker(id);
    }
}
