using MQTTnet;

namespace Syren.Server.Services;

public interface ISyrenMqttClientFactory
{
    IMqttClient CreateClient();
}

public sealed class SyrenMqttClientFactory : ISyrenMqttClientFactory
{
    public IMqttClient CreateClient() => new MqttClientFactory().CreateMqttClient();
}
