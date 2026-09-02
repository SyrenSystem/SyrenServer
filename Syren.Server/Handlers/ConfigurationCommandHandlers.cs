using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using Syren.Server.Configuration;
using Syren.Server.Models;
using Syren.Server.Services;

namespace Syren.Server.Handlers;

public abstract class ConfigurationCommandHandler<TCommand> : IMqttMessageHandler
    where TCommand : ConfigurationCommand
{
    private readonly ILogger _logger;

    protected ConfigurationCommandHandler(
        ISystemConfigurationService configurationService,
        IOptions<MqttOptions> options,
        ILogger logger)
    {
        ConfigurationService = configurationService;
        Options = options.Value;
        _logger = logger;
    }

    public abstract string Topic { get; }

    protected ISystemConfigurationService ConfigurationService { get; }

    protected MqttOptions Options { get; }

    public async Task HandleMessageAsync(
        MqttApplicationMessage message,
        IMqttClientService client,
        CancellationToken cancellationToken = default)
    {
        try
        {
            TCommand? command = JsonSerializer.Deserialize<TCommand>(
                message.ConvertPayloadToString()
            );
            if (command == null)
            {
                throw new JsonException("Command payload is empty");
            }
            CommandResultMessage result = await ExecuteAsync(command, cancellationToken);
            string resultTopic = GetResultTopic(command.RequestId);
            await client.PublishAsync(resultTopic, result, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Dropping malformed configuration command from {Topic}", message.Topic);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to process configuration command from {Topic}", message.Topic);
        }
    }

    protected abstract Task<CommandResultMessage> ExecuteAsync(
        TCommand command,
        CancellationToken cancellationToken);

    private string GetResultTopic(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return Options.CommandResultTopic;
        }
        bool safe = requestId.All(character =>
            character != '/' && character != '+' && character != '#'
        );
        return safe && requestId.Length <= 128
            ? $"{Options.CommandResultTopic}/{requestId}"
            : Options.CommandResultTopic;
    }
}

public sealed class ConfigureSpeakerHandler : ConfigurationCommandHandler<ConfigureSpeakerCommand>
{
    public ConfigureSpeakerHandler(
        ISystemConfigurationService configurationService,
        IOptions<MqttOptions> options,
        ILogger<ConfigureSpeakerHandler> logger) : base(configurationService, options, logger)
    {
    }

    public override string Topic => Options.ConfigureSpeakerTopic;

    protected override Task<CommandResultMessage> ExecuteAsync(
        ConfigureSpeakerCommand command,
        CancellationToken cancellationToken) =>
        ConfigurationService.ConfigureSpeakerAsync(command, cancellationToken);
}

public sealed class DeleteSpeakerHandler : ConfigurationCommandHandler<DeleteSpeakerCommand>
{
    public DeleteSpeakerHandler(
        ISystemConfigurationService configurationService,
        IOptions<MqttOptions> options,
        ILogger<DeleteSpeakerHandler> logger) : base(configurationService, options, logger)
    {
    }

    public override string Topic => Options.DeleteSpeakerTopic;

    protected override Task<CommandResultMessage> ExecuteAsync(
        DeleteSpeakerCommand command,
        CancellationToken cancellationToken) =>
        ConfigurationService.DeleteSpeakerAsync(command, cancellationToken);
}

public sealed class UpsertGroupHandler : ConfigurationCommandHandler<UpsertGroupCommand>
{
    public UpsertGroupHandler(
        ISystemConfigurationService configurationService,
        IOptions<MqttOptions> options,
        ILogger<UpsertGroupHandler> logger) : base(configurationService, options, logger)
    {
    }

    public override string Topic => Options.UpsertGroupTopic;

    protected override Task<CommandResultMessage> ExecuteAsync(
        UpsertGroupCommand command,
        CancellationToken cancellationToken) =>
        ConfigurationService.UpsertGroupAsync(command, cancellationToken);
}

public sealed class DeleteGroupHandler : ConfigurationCommandHandler<DeleteGroupCommand>
{
    public DeleteGroupHandler(
        ISystemConfigurationService configurationService,
        IOptions<MqttOptions> options,
        ILogger<DeleteGroupHandler> logger) : base(configurationService, options, logger)
    {
    }

    public override string Topic => Options.DeleteGroupTopic;

    protected override Task<CommandResultMessage> ExecuteAsync(
        DeleteGroupCommand command,
        CancellationToken cancellationToken) =>
        ConfigurationService.DeleteGroupAsync(command, cancellationToken);
}

public sealed class SetSpeakerLevelHandler : ConfigurationCommandHandler<SetSpeakerLevelCommand>
{
    public SetSpeakerLevelHandler(
        ISystemConfigurationService configurationService,
        IOptions<MqttOptions> options,
        ILogger<SetSpeakerLevelHandler> logger) : base(configurationService, options, logger)
    {
    }

    public override string Topic => Options.SetSpeakerLevelTopic;

    protected override Task<CommandResultMessage> ExecuteAsync(
        SetSpeakerLevelCommand command,
        CancellationToken cancellationToken) =>
        ConfigurationService.SetSpeakerLevelAsync(command, cancellationToken);
}
