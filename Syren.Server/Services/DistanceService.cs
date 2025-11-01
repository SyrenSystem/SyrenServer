using System.Numerics;
using Microsoft.Extensions.Logging;
using Syren.Server.Interfaces;
using Syren.Server.Models;

namespace Syren.Server.Services;

public class DistanceService : IDistanceService
{
    private readonly Dictionary<uint, Speaker> _speakers = [];
    private readonly ILogger<DistanceService> _logger;

    public DistanceService(ILogger<DistanceService> logger)
    {
        _logger = logger;
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

    private static float MeanSquaredError(Vector3 position, (Vector3 Center, float Radius)[] spheres)
    {
        return spheres.Select(sphere =>
        {
            float distance = (sphere.Center - position).Length();
            return (float)Math.Pow(sphere.Radius - distance, 2.0);
        }).Sum() / spheres.Length;
    }

    private Vector3 PoorMansGradientDescent(Vector3 initialValue, Func<Vector3, float> errorFunc)
    {
        Vector3 result = initialValue;
        float errorVal = errorFunc(result);

        const double threshold = 0.3f;
        const uint maxIterations = 10;

        // Poor man's gradient descent
        for (uint i = 0; i < maxIterations; i++)
        {
            if (errorVal <= threshold) return result;

            (result, errorVal) = _directions
                .Select(direction => result + direction * errorVal)
                .Select(position => (position, errorFunc(position)))
                .MinBy<(Vector3 Position, float ErrorVal), float>(tuple => tuple.ErrorVal);
        }

        _logger.LogWarning("User position {Position} has error value of {Error:0.00} > {Threshold:0.00}", result, errorVal, threshold);

        return result;
    }
}
