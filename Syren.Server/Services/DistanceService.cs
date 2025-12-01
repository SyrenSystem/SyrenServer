using System.Numerics;
using Syren.Server.Configuration;
using Syren.Server.Models;
using Microsoft.Extensions.Options;
using Syren.Server.Extensions;

namespace Syren.Server.Services;

public class DistanceService : IDistanceService, IAsyncDisposable
{
    private readonly Dictionary<string, Speaker> _speakers = [];
    private readonly Dictionary<string, SpeakerState> _speakerStates = [];

    private readonly SyrenSettings _syrenSettings;

    private readonly ISnapCastService _snapCastService;
    private readonly ILogger<DistanceService> _logger;

    public DistanceService(
        IOptions<SyrenSettings> syrenSettings,
        IOptions<SpeakersOptions> speakersOptions,
        ISnapCastService snapCastService,
        ILogger<DistanceService> logger)
    {
        _syrenSettings = syrenSettings.Value;
        _snapCastService = snapCastService;
        _logger = logger;

        _speakers = speakersOptions.Value.SpeakersInfo
            .Select(info => (
                    info.SensorId,
                    new Speaker
                    {
                        SensorId = info.SensorId,
                        SnapClientId = info.SnapClientId,
                        FullVolumeDistance = info.FullVolumeDistance,
                        MuteDistance = info.MuteDistance,
                    })
                )
            .ToDictionary();
        foreach (Speaker speaker in _speakers.Values)
        {
            _logger.LogInformation(
                "Configured speaker with SensorId \"{SensorId}\" and SnapClientId \"{SnapClientId}\"",
                speaker.SensorId, speaker.SnapClientId
            );
        }


        _logger.LogInformation("Setting all SnapClient volumes to 0");
        Task.WhenAll(
            _speakers.Values
                .Select(async speaker =>
                    await _snapCastService.SetClientVolumeAsync(speaker.SnapClientId, 0)
                )
        );
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogTrace("Shutting down DistanceService");

        await Task.WhenAll(
            _speakerStates.Values.Select(async state => await DisconnectSpeakerAsync(state.Speaker.SensorId))
        );

        GC.SuppressFinalize(this);
    }

    public async Task UpdateDistanceAsync(DistanceData distance)
    {
        _logger.LogTrace("Updating speaker \"{Id}\" distance to {Distance}", distance.SpeakerId, distance.Distance);

        if (!_speakers.ContainsKey(distance.SpeakerId))
        {
            _logger.LogError("Tried to update distance of unknown speaker with ID {ID}",
                            distance.SpeakerId);
            return;
        }

        SpeakerState state = _speakerStates[distance.SpeakerId];
        state.Distance = distance.Distance * _syrenSettings.DistanceSmoothingFactor
            + state.Distance * (1.0 - _syrenSettings.DistanceSmoothingFactor);

        double distanceVolumeModifier = GetDistanceVolumeModifier(distance.SpeakerId, distance.Distance);
        double volume = state.Volume * distanceVolumeModifier;
        _logger.LogInformation("DistanceVolumeModifier: {DistanceVolumeModifier}; Volume: {Volume}", distanceVolumeModifier, volume);

        await _snapCastService.SetClientVolumeAsync(state.Speaker.SnapClientId, (int)(volume * 100.0));
    }

    public async Task UpdateDistancesAsync(IReadOnlyCollection<DistanceData> distances)
    {
        _logger.LogTrace("Updating {distanceCount} distances", distances.Count);

        await Task.WhenAll(distances.Select(async
                distanceData => await UpdateDistanceAsync(distanceData)
            ));
    }

    public async Task SetSpeakerVolumeAsync(string sensorId, double volume)
    {
        _logger.LogTrace("Setting speaker \"{Id}\" volume to {Volume}", sensorId, volume);

        if (!_speakerStates.ContainsKey(sensorId))
        {
            _logger.LogWarning("Tried to set volume of disconnected speaker \"{Id}", sensorId);
            return;
        }

        SpeakerState state = _speakerStates[sensorId];
        state.Volume = volume;

        double distanceVolumeModifier = GetDistanceVolumeModifier(sensorId, state.Distance);
        double snapVolume = volume * distanceVolumeModifier;
        await _snapCastService.SetClientVolumeAsync(state.Speaker.SnapClientId, (int)(snapVolume * 100.0));
    }

    public async Task<SpeakerState?> ConnectSpeakerAsync(string sensorId)
    {
        _logger.LogTrace("Connecting speaker with ID {speakerId}; new speaker count: {speakerCount}",
                            sensorId, _speakerStates.Count);

        if (!_speakers.ContainsKey(sensorId))
        {
            _logger.LogError("Tried to connect an undefined speaker with ID \"{Id}\" to the system", sensorId);
            return null;
        }

        if (_speakerStates.ContainsKey(sensorId))
        {
            _logger.LogWarning("Tried to connect existing speaker \"{Id}\"", sensorId);
            return _speakerStates[sensorId];
        }

        Vector3 position = _speakerStates.Count switch
        {
            0 => GetAddedFirstSpeakerPosition(),
            1 => GetAddedSecondSpeakerPosition(),
            2 => GetAddedThirdSpeakerPosition(),
            _ => GetAddedTrilateratedSpeakerPosition(),
        };

        SpeakerState speakerState = new()
        {
            Speaker = _speakers[sensorId],
            Position = position,
            Distance = 0.0,
            Volume = _snapCastService
                .GetClientVolume(_speakers[sensorId].SnapClientId)
                .Result
                .GetValueOrDefault(1.0),
        };

        _speakerStates.Add(sensorId, speakerState);
        await _snapCastService.SetClientVolumeAsync(_speakers[sensorId].SnapClientId, (int)(speakerState.Volume * 100));

        _logger.LogDebug("Connected speaker with ID {speakerId} at position {position}",
                            sensorId, speakerState.Position);
        return speakerState;
    }

    private static Vector3 GetAddedFirstSpeakerPosition() => Vector3.Zero;

    private Vector3 GetAddedSecondSpeakerPosition()
    {
        _logger.LogTrace("Computing second speaker position");

        // Second speaker can be on any arbitrary point on the sphere
        // around the first at the distance between the two
        SpeakerState otherSpeaker = _speakerStates.First().Value;

        return otherSpeaker.Position + new Vector3((float)otherSpeaker.Distance, 0.0f, 0.0f);
    }

    private Vector3 GetAddedThirdSpeakerPosition()
    {
        _logger.LogTrace("Computing third speaker position");

        // Third speaker can be on any arbitrary point on the circle
        // where the distances to both other speakers is correct
        SpeakerState[] speakers = _speakerStates
            .Values
            .Take(2)
            .ToArray();

        // Direction vector from the second speaker to the first
        Vector3 direction = Vector3.Normalize(speakers[1].Position - speakers[0].Position);
        float twoSpeakerDistance = Vector3.Distance(speakers[0].Position, speakers[1].Position);

        // Points on the distance spheres closest and farthest away from the other speaker's position
        (Vector3 Near, Vector3 Far)[] extrema = [
            (
                speakers[0].Position + direction * (float)speakers[0].Distance,
                speakers[0].Position - direction * (float)speakers[0].Distance
            ),
            (
                speakers[1].Position - direction * (float)speakers[1].Distance,
                speakers[1].Position + direction * (float)speakers[1].Distance
            )
        ];

        if (twoSpeakerDistance > speakers[0].Distance + speakers[1].Distance)
        {
            // Two speakers' distance spheres are disjoint
            // Place speaker halfway between the two circles' perimeters
            return (extrema[0].Near + extrema[1].Near) / 2.0f;
        } else if (speakers[0].Distance > (speakers[0].Position - extrema[1].Far).Length())
        {
            // Second speaker's distance sphere is inside the first's distance sphere
            // Place speaker closest to both boundaries
            return (extrema[0].Near + extrema[1].Far) / 2.0f;
        } else if (speakers[1].Distance > (speakers[1].Position - extrema[0].Far).Length())
        {
            // First speaker's distance sphere is inside the second's distance sphere
            // Place speaker closest to both boundaries
            return (extrema[1].Near + extrema[0].Far) / 2.0f;
        } else
        {
            // Two distance spheres intersect
            // Place speaker at an intersection point

            // Law of cosines
            float speaker1Angle = MathF.Acos(
                (
                    MathF.Pow((float)speakers[0].Distance, 2.0f) +
                    MathF.Pow(twoSpeakerDistance, 2.0f) -
                    MathF.Pow((float)speakers[1].Distance, 2.0f)
                ) / (
                    2.0f * (float)speakers[0].Distance * twoSpeakerDistance
                )
            );

            Vector3 orthogonal = Vector3.Normalize(new(
                direction.Y + direction.Z,
                direction.Z - direction.X,
                -direction.X - direction.Y
            ));
            return speakers[0].Position +
                (float)speakers[0].Distance * (
                    orthogonal * MathF.Sin(speaker1Angle) +
                    direction * MathF.Cos(speaker1Angle)
                );
        }
    }

    private Vector3 GetAddedTrilateratedSpeakerPosition()
    {
        _logger.LogTrace("Computing >= 4th speaker position");

        // Fourth speaker onward can be located with gradient descent
        DistanceData[] distances = _speakerStates
            .Select(keyValue =>
                new DistanceData() { SpeakerId = keyValue.Key, Distance = keyValue.Value.Distance }
            ).ToArray();
        return GetUserPosition(distances);
    }


    public async Task DisconnectSpeakerAsync(string sensorId)
    {
        _logger.LogTrace("Disconnecting speaker {ID}", sensorId);

        if (!_speakerStates.ContainsKey(sensorId))
        {
            _logger.LogWarning("Tried to remove speaker with unknown ID {ID}", sensorId);
            return;
        }

        await _snapCastService.SetClientVolumeAsync(_speakers[sensorId].SnapClientId, 0);
        _speakerStates.Remove(sensorId);
    }


    private readonly static Vector3[] _directions = [
        new( 1.0f,  0.0f,  0.0f),
        new( 0.0f,  1.0f,  0.0f),
        new( 0.0f,  0.0f,  1.0f),
        new(-1.0f,  0.0f,  0.0f),
        new( 0.0f, -1.0f,  0.0f),
        new( 0.0f,  0.0f, -1.0f)
    ];

    public Vector3 GetUserPosition(IReadOnlyCollection<DistanceData> distances)
    {
        _logger.LogTrace("Calculating user position");

        if (_speakers.Count < 3)
        {
            throw new InvalidOperationException($"Tried to calculate user position with only {_speakers.Count} < 3 speakers defined.");
        }

        var spheres = distances
            .Where(distance => _speakers.ContainsKey(distance.SpeakerId))
            .GroupBy(distance => distance.SpeakerId)
            .Select(speakerDistances => new Sphere() {
                Center = _speakerStates[speakerDistances.Key].Position,
                Radius = speakerDistances.Single().Distance
            }).ToArray();

        if (spheres.Length < 3)
        {
            throw new ArgumentException($"Tried to calculate user position using {spheres.Length} < 3 data points.");
        }

        Vector3 center = _speakerStates.Values.ToList().Center();
        return PoorMansGradientDescent(center, vector => MeanAbsError(vector, spheres));
    }

    private static double MeanAbsError(Vector3 position, Sphere[] spheres)
    {
        return spheres.Select(sphere =>
        {
            float distance = Vector3.Distance(sphere.Center, position);
            return Math.Abs(sphere.Radius - distance);
        }).Sum() / spheres.Length;
    }

    private Vector3 PoorMansGradientDescent(Vector3 initialValue, Func<Vector3, double> errorFunc)
    {
        Vector3 result = initialValue;
        double errorVal = errorFunc(result);
        float eta = (float)errorVal;

        const double threshold = 0.1f;
        const uint maxIterations = 100;

        // Poor man's gradient descent
        for (uint iterationCt = 0; iterationCt < maxIterations; iterationCt++)
        {
            if (errorVal <= threshold) {
                _logger.LogInformation(@"User position {Position} has error value {Error:0.00} <= {Threshold:0.00}
                    in {IterationCt} iterations", result, errorVal, threshold, iterationCt);
                return result;
            }

            (var newResult, var newErrorVal) = _directions
                .Select(gradient => result + gradient * eta)
                .Select(position => (position, errorFunc(position)))
                .MinBy<(Vector3 Position, double ErrorVal), double>(tuple => tuple.ErrorVal);
            
            if (newErrorVal < errorVal)
            {
                // New value is better; keep going with the new value
                result = newResult;
                errorVal = newErrorVal;
                eta = (float)errorVal;
            } else
            {
                // We overshot; reduce eta
                eta /= 2.0f;
            }
        }

        _logger.LogWarning(@"User position {Position} has error value of {Error:0.00} > {Threshold:0.00}
            in {IterationCt} iterations", result, errorVal, threshold, maxIterations);

        return result;
    }

    private double GetDistanceVolumeModifier(string speakerSensorId, double distance)
    {
        double muteDistance = _speakers[speakerSensorId].MuteDistance;
        double fullVolumeDistance = _speakers[speakerSensorId].FullVolumeDistance;
        distance = Math.Clamp(distance, fullVolumeDistance, muteDistance);

        return 1.0 - (distance - fullVolumeDistance) / (muteDistance - fullVolumeDistance);
    }
}
