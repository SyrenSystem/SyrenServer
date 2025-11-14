using Microsoft.AspNetCore.SignalR;
using Syren.Server.Interfaces;
using Syren.Server.Models;

namespace Syren.Server.Hubs;

public class DistanceHub(IDistanceService distanceService) : Hub
{
    private readonly IDistanceService _distanceService = distanceService;

    public void SendDistance(int speakerId, double distance)
    {
        DistanceData distanceData = new() { SpeakerId = speakerId, Distance = distance };
        _distanceService.UpdateDistances([distanceData]);
    }

    // Expects the user's position to be on the new speaker's location
    public int AddSpeaker()
    {
        return _distanceService.AddSpeaker().Id;
    }

    public void RemoveSpeaker(int id)
    {
        _distanceService.RemoveSpeaker(id);
    }
}
