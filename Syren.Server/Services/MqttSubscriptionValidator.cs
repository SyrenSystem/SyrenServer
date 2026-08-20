using MQTTnet;

namespace Syren.Server.Services;

public static class MqttSubscriptionValidator
{
    public static bool AreAllGranted(IEnumerable<MqttClientSubscribeResultCode> resultCodes) =>
        resultCodes.All(resultCode => resultCode is
            MqttClientSubscribeResultCode.GrantedQoS0 or
            MqttClientSubscribeResultCode.GrantedQoS1 or
            MqttClientSubscribeResultCode.GrantedQoS2);
}
