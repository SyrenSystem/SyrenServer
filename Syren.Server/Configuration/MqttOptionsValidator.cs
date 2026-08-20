using Microsoft.Extensions.Options;

namespace Syren.Server.Configuration;

public sealed class MqttOptionsValidator : IValidateOptions<MqttOptions>
{
    public ValidateOptionsResult Validate(string? name, MqttOptions options)
    {
        string[] requiredValues =
        [
            options.Host,
            options.ClientId,
            options.UpdateDistanceTopic,
            options.SetSpeakerVolumeTopic,
            options.ConnectSpeakerTopic,
            options.DisconnectSpeakerTopic,
            options.GetSpeakerPositionTopic,
            options.GetUserPositionTopic,
            options.ServerStatusTopic,
        ];
        if (requiredValues.Any(string.IsNullOrWhiteSpace))
        {
            return ValidateOptionsResult.Fail("MQTT host, client ID, and topics are required");
        }
        if (options.Port is < 1 or > 65535)
        {
            return ValidateOptionsResult.Fail("MQTT port must be between 1 and 65535");
        }
        if (options.ReconnectDelaySeconds <= 0)
        {
            return ValidateOptionsResult.Fail("MQTT reconnect delay must be positive");
        }
        return ValidateOptionsResult.Success;
    }
}
