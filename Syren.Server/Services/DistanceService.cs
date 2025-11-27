using System.Numerics;
using Syren.Server.Models;

namespace Syren.Server.Services;

public class DistanceService : IDistanceService
{
    private readonly Dictionary<string, SpeakerState> _speakers = [];

    private readonly ISnapCastService _snapCastService;
    private readonly ILogger<DistanceService> _logger;

    public DistanceService(
        ISnapCastService snapCastService,
        ILogger<DistanceService> logger)
    {
        _snapCastService = snapCastService;
        _logger = logger;
    }


    public void UpdateDistances(IReadOnlyCollection<DistanceData> distances)
    {
        _logger.LogTrace("Updating {distanceCount} distances", distances.Count);

        foreach (DistanceData distanceData in distances)
        {
            if (!_speakers.ContainsKey(distanceData.SpeakerId))
            {
                _logger.LogError("Tried to update distance of unknown speaker with ID {ID}",
                                distanceData.SpeakerId);
                continue;
            }

            _speakers[distanceData.SpeakerId].Distance = distanceData.Distance;
        }
    }

    public Speaker AddSpeaker(string id)
    {
        _logger.LogTrace("Adding speaker with ID {speakerId}; new speaker count: {speakerCount}",
                            id, _speakers.Count);

        if (_speakers.ContainsKey(id))
        {
            _logger.LogWarning("Tried to add existing speaker \"{Id}\"", id);
            return _speakers[id].Speaker;
        }

        Vector3 position = _speakers.Count switch
        {
            0 => GetAddedFirstSpeakerPosition(),
            1 => GetAddedSecondSpeakerPosition(),
            2 => GetAddedThirdSpeakerPosition(),
            _ => GetAddedTrilateratedSpeakerPosition(),
        };

        _snapCastService.GetStatusAsync();

        Speaker speaker = new()
        {
            Id = id,
            Position = position,
        };

        _speakers.Add(speaker.Id, new SpeakerState{Speaker = speaker, Distance = 0.0});

        _logger.LogDebug("Added speaker with ID {speakerId} at position {position}",
                            speaker.Id, speaker.Position);
        return speaker;
    }

    private static Vector3 GetAddedFirstSpeakerPosition() => Vector3.Zero;

    private Vector3 GetAddedSecondSpeakerPosition()
    {
        // Second speaker can be on any arbitrary point on the sphere
        // around the first at the distance between the two
        SpeakerState otherSpeaker = _speakers.First().Value;

        return otherSpeaker.Speaker.Position + new Vector3((float)otherSpeaker.Distance, 0.0f, 0.0f);
    }

    private Vector3 GetAddedThirdSpeakerPosition()
    {
        // Third speaker can be on any arbitrary point on the circle
        // where the distances to both other speakers is correct
        SpeakerState[] speakers = _speakers
            .Values
            .Take(2)
            .ToArray();

        // Direction vector from the second speaker to the first
        Vector3 direction = Vector3.Normalize(speakers[1].Speaker.Position - speakers[0].Speaker.Position);
        float twoSpeakerDistance = Vector3.Distance(speakers[0].Speaker.Position, speakers[1].Speaker.Position);

        // Points on the distance spheres closest and farthest away from the other speaker's position
        (Vector3 Near, Vector3 Far)[] extrema = [
            (
                speakers[0].Speaker.Position + direction * (float)speakers[0].Distance,
                speakers[0].Speaker.Position - direction * (float)speakers[0].Distance
            ),
            (
                speakers[1].Speaker.Position - direction * (float)speakers[1].Distance,
                speakers[1].Speaker.Position + direction * (float)speakers[1].Distance
            )
        ];

        if (twoSpeakerDistance > speakers[0].Distance + speakers[1].Distance)
        {
            // Two speakers' distance spheres are disjoint
            // Place speaker halfway between the two circles' perimeters
            return (extrema[0].Near + extrema[1].Near) / 2.0f;
        } else if (speakers[0].Distance > (speakers[0].Speaker.Position - extrema[1].Far).Length())
        {
            // Second speaker's distance sphere is inside the first's distance sphere
            // Place speaker closest to both boundaries
            return (extrema[0].Near + extrema[1].Far) / 2.0f;
        } else if (speakers[1].Distance > (speakers[1].Speaker.Position - extrema[0].Far).Length())
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
            return speakers[0].Speaker.Position +
                (float)speakers[0].Distance * (
                    orthogonal * MathF.Sin(speaker1Angle) +
                    direction * MathF.Cos(speaker1Angle)
                );
        }
    }

    private Vector3 GetAddedTrilateratedSpeakerPosition()
    {
        // Fourth speaker onward can be located with gradient descent
        DistanceData[] distances = _speakers
            .Select(keyValue =>
                new DistanceData() { SpeakerId = keyValue.Key, Distance = keyValue.Value.Distance }
            ).ToArray();
        return GetUserPosition(distances);
    }


    public void RemoveSpeaker(string id)
    {
        _logger.LogTrace("Removing speaker {ID}", id);

        if (!_speakers.ContainsKey(id))
        {
            _logger.LogWarning("Tried to remove speaker with unknown ID {ID}", id);
            return;
        }

        _speakers.Remove(id);
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
                Center = _speakers[speakerDistances.Key].Speaker.Position,
                Radius = speakerDistances.Single().Distance
            }).ToArray();

        if (spheres.Length < 3)
        {
            throw new ArgumentException($"Tried to calculate user position using {spheres.Length} < 3 data points.");
        }

        Vector3 center = _speakers
            .Values
            .Select(state => state.Speaker)
            .ToList()
            .Center();
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
}

public sealed record SpeakerState
{
    public Speaker Speaker;
    public double Distance;
}
