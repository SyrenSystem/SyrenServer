using System.Numerics;
using Syren.Server.Interfaces;
using Syren.Server.Models;

namespace Syren.Server.Services;

public class DistanceService : IDistanceService
{
    private readonly Dictionary<int, Speaker> _speakers = [];
    private readonly Dictionary<int, double> _speakerDistances = [];
    private int _maxSpeakerId = 0;

    private readonly ILogger<DistanceService> _logger;

    public DistanceService(ILogger<DistanceService> logger)
    {
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

            _speakerDistances.Add(distanceData.SpeakerId, distanceData.Distance);
        }
    }

    public Speaker AddSpeaker()
    {
        _logger.LogTrace("Adding speaker with ID {speakerId}; new speaker count: {speakerCount}",
                            _maxSpeakerId, _speakers.Count);

        int id = _maxSpeakerId++;
        Vector3 position = _speakers.Count switch
        {
            0 => GetAddedFirstSpeakerPosition(),
            1 => GetAddedSecondSpeakerPosition(),
            2 => GetAddedThirdSpeakerPosition(),
            _ => GetAddedTrilateratedSpeakerPosition(),
        };

        Speaker speaker = new()
        {
            Id = id,
            Position = position,
        };

        _speakers.Add(speaker.Id, speaker);
        return speaker;
    }

    private static Vector3 GetAddedFirstSpeakerPosition() => Vector3.Zero;

    private Vector3 GetAddedSecondSpeakerPosition()
    {
        // Second speaker can be on any arbitrary point on the sphere
        // around the first at the distance between the two
        Speaker otherSpeaker = _speakers.First().Value;
        double otherDistance = _speakerDistances[otherSpeaker.Id];

        return otherSpeaker.Position + new Vector3((float)otherDistance, 0.0f, 0.0f);
    }

    private Vector3 GetAddedThirdSpeakerPosition()
    {
        // Third speaker can be on any arbitrary point on the circle
        // where the distances to both other speakers is correct
        (Speaker Speaker, double Distance)[] speakers = _speakers
            .Take(2)
            .Select(keyValuePair =>
                (keyValuePair.Value, _speakerDistances[keyValuePair.Key])
            ).ToArray();

        // Direction vector from the second speaker to the first
        Vector3 direction = speakers[1].Speaker.Position - speakers[0].Speaker.Position;
        double twoSpeakerDistance = direction.Length();
        direction = Vector3.Normalize(direction);

        // Points on the distance spheres farthest away from the other speaker's position
        Tuple<Vector3, Vector3> farPoints = new(
            speakers[0].Speaker.Position + direction * (float)speakers[0].Distance,
            speakers[1].Speaker.Position - direction * (float)speakers[1].Distance
        );
        // Points on the distance spheres closest to the other speaker's position
        Tuple<Vector3, Vector3> nearPoints = new(
            speakers[0].Speaker.Position - direction * (float)speakers[0].Distance,
            speakers[1].Speaker.Position + direction * (float)speakers[1].Distance
        );

        if (twoSpeakerDistance > speakers[0].Distance + speakers[1].Distance)
        {
            // Two speakers' distance spheres are disjoint
            // Place speaker on the connecting line at the right ratio
            float distanceRatio = (float)(speakers[0].Distance / (speakers[0].Distance + speakers[1].Distance));
            return nearPoints.Item1 * distanceRatio + nearPoints.Item2 * (1.0f - distanceRatio);
        } else if (speakers[0].Distance > (speakers[0].Speaker.Position - farPoints.Item2).Length())
        {
            // Second speaker's distance sphere is inside the first's distance sphere
            // Place speaker closest to both boundaries
            return (nearPoints.Item1 + farPoints.Item2) / 2.0f;
        } else if (speakers[1].Distance > (speakers[1].Speaker.Position - farPoints.Item1).Length())
        {
            // First speaker's distance sphere is inside the second's distance sphere
            // Place speaker closest to both boundaries
            return (nearPoints.Item2 + farPoints.Item1) / 2.0f;
        } else
        {
            // Two distance spheres intersect
            // Place speaker at an intersection point
            float speaker1Angle = MathF.Atan((float)(speakers[1].Distance / speakers[0].Distance));
            Vector3 orthogonal = Vector3.Normalize(new(
                direction.Y + direction.Z,
                direction.Z - direction.X,
                -direction.X - direction.Y
            ));
            return speakers[0].Speaker.Position +
                direction * MathF.Sin(speaker1Angle) +
                orthogonal * MathF.Cos(speaker1Angle);
        }
    }

    private Vector3 GetAddedTrilateratedSpeakerPosition()
    {
        // Fourth speaker onward can be located with gradient descent
        DistanceData[] distances = _speakerDistances
            .Select(keyValue =>
                new DistanceData() { SpeakerId = keyValue.Key, Distance = keyValue.Value }
            ).ToArray();
        return GetUserPosition(distances);
    }


    public void RemoveSpeaker(int id)
    {
        _logger.LogTrace("Removing speaker {ID}", id);

        if (!_speakers.ContainsKey(id))
        {
            _logger.LogWarning("Tried to remove speaker with unknown ID {ID}", id);
        }

        _speakers.Remove(id);
        _speakerDistances.Remove(id);
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
            .Select(speakerDistances => (
                Center: _speakers[speakerDistances.Key].Position,
                Radius: speakerDistances.Single().Distance
            )).ToArray();

        if (spheres.Length < 3)
        {
            throw new ArgumentException($"Tried to calculate user position using {spheres.Length} < 3 data points.");
        }

        return PoorMansGradientDescent(_speakers.Values.Center(), vector => MeanSquaredError(vector, spheres));
    }

    private static double MeanSquaredError(Vector3 position, (Vector3 Center, double Radius)[] spheres)
    {
        return spheres.Select(sphere =>
        {
            double distance = (sphere.Center - position).Length();
            return Math.Pow(sphere.Radius - distance, 2.0);
        }).Sum() / spheres.Length;
    }

    private Vector3 PoorMansGradientDescent(Vector3 initialValue, Func<Vector3, double> errorFunc)
    {
        Vector3 result = initialValue;
        double errorVal = errorFunc(result);

        const double threshold = 0.3f;
        const uint maxIterations = 10;

        // Poor man's gradient descent
        for (uint i = 0; i < maxIterations; i++)
        {
            if (errorVal <= threshold) return result;

            (result, errorVal) = _directions
                .Select(direction => result + direction * (float)errorVal)
                .Select(position => (position, errorFunc(position)))
                .MinBy<(Vector3 Position, double ErrorVal), double>(tuple => tuple.ErrorVal);
        }

        _logger.LogWarning("User position {Position} has error value of {Error:0.00} > {Threshold:0.00}", result, errorVal, threshold);

        return result;
    }
}
