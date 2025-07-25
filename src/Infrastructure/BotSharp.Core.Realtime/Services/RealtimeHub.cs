using BotSharp.Abstraction.Functions.Models;
using BotSharp.Abstraction.Hooks;
using BotSharp.Abstraction.Models;
using BotSharp.Abstraction.Options;
using BotSharp.Abstraction.Routing.Enums;
using BotSharp.Core.Infrastructures;

namespace BotSharp.Core.Realtime.Services;

public class RealtimeHub : IRealtimeHub
{
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;

    private RealtimeHubConnection _conn;
    public RealtimeHubConnection HubConn => _conn;

    private IRealTimeCompletion _completer;
    public IRealTimeCompletion Completer => _completer;

    public RealtimeHub(IServiceProvider services, ILogger<RealtimeHub> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task ConnectToModel(Func<string, Task>? responseToUser = null, Func<string, Task>? init = null, List<MessageState>? initStates = null)
    {
        var convService = _services.GetRequiredService<IConversationService>();
        convService.SetConversationId(_conn.ConversationId, initStates ?? []);
        var conversation = await convService.GetConversation(_conn.ConversationId);

        var routing = _services.GetRequiredService<IRoutingService>();
        var agentService = _services.GetRequiredService<IAgentService>();
        var agent = await agentService.GetAgent(_conn.CurrentAgentId);

        var storage = _services.GetRequiredService<IConversationStorage>();
        var dialogs = convService.GetDialogHistory();
        routing.Context.SetDialogs(dialogs);
        routing.Context.SetMessageId(_conn.ConversationId, Guid.Empty.ToString());

        var states = _services.GetRequiredService<IConversationStateService>();
        var settings = _services.GetRequiredService<RealtimeModelSettings>();

        _completer = _services.GetServices<IRealTimeCompletion>().First(x => x.Provider == settings.Provider);

        await _completer.Connect(
            conn: _conn, 
            onModelReady: async () => 
            {
                // Not TriggerModelInference, waiting for user utter.
                var instruction = await _completer.UpdateSession(_conn, isInit: true);
                var data = _conn.OnModelReady();
                await HookEmitter.Emit<IRealtimeHook>(_services, async hook => await hook.OnModelReady(agent, _completer), 
                    agent.Id);
                await (init?.Invoke(data) ?? Task.CompletedTask);
            },
            onModelAudioDeltaReceived: async (audioDeltaData, itemId) =>
            {
                var data = _conn.OnModelMessageReceived(audioDeltaData);
                await (responseToUser?.Invoke(data) ?? Task.CompletedTask);

                // If this is the first delta of a new response, set the start timestamp
                if (!_conn.ResponseStartTimestamp.HasValue)
                {
                    _conn.ResponseStartTimestamp = _conn.LatestMediaTimestamp;
                    _logger.LogDebug($"Setting start timestamp for new response: {_conn.ResponseStartTimestamp}ms");
                }
                // Record last assistant item ID for interruption handling
                if (!string.IsNullOrEmpty(itemId))
                {
                    _conn.LastAssistantItemId = itemId;
                }

                // Send mark messages to Media Streams so we know if and when AI response playback is finished
                // await SendMark(userWebSocket, _conn);
            }, 
            onModelAudioResponseDone: async () =>
            {
                var data = _conn.OnModelAudioResponseDone();
                await (responseToUser?.Invoke(data) ?? Task.CompletedTask);
            },
            onModelAudioTranscriptDone: async transcript =>
            {

            },
            onModelResponseDone: async messages =>
            {
                foreach (var message in messages)
                {
                    // Invoke function
                    if (message.MessageType == MessageTypeName.FunctionCall &&
                        !string.IsNullOrEmpty(message.FunctionName))
                    {
                        if (message.FunctionName == "route_to_agent")
                        {
                            var instruction = JsonSerializer.Deserialize<FunctionCallFromLlm>(message.FunctionArgs, BotSharpOptions.defaultJsonOptions);
                            await HookEmitter.Emit<IRoutingHook>(_services, async hook => await hook.OnRoutingInstructionReceived(instruction, message),
                                agent.Id);
                        }

                        await routing.InvokeFunction(message.FunctionName, message, from: InvokeSource.Llm);
                    }
                    else
                    {
                        // append output audio transcript to conversation
                        dialogs.Add(message);
                        storage.Append(_conn.ConversationId, message);

                        var convHooks = _services.GetHooksOrderByPriority<IConversationHook>(_conn.CurrentAgentId);
                        foreach (var hook in convHooks)
                        {
                            hook.SetAgent(agent)
                                .SetConversation(conversation);

                            await hook.OnResponseGenerated(message);
                        }
                    }
                }

                var isReconnect = false;
                var realtimeHooks = _services.GetHooks<IRealtimeHook>(_conn.CurrentAgentId);
                foreach (var hook in realtimeHooks)
                {
                    isReconnect = await hook.ShouldReconnect(_conn);
                    if (isReconnect) break;
                }

                if (isReconnect)
                {
                    await _completer.Reconnect(_conn);
                }
            },
            onConversationItemCreated: async response =>
            {
                
            },
            onInputAudioTranscriptionDone: async message =>
            {
                // append input audio transcript to conversation
                dialogs.Add(message);
                storage.Append(_conn.ConversationId, message);
                routing.Context.SetMessageId(_conn.ConversationId, message.MessageId);

                var hooks = _services.GetHooksOrderByPriority<IConversationHook>(_conn.CurrentAgentId);
                foreach (var hook in hooks)
                {
                    hook.SetAgent(agent)
                        .SetConversation(conversation);

                    await hook.OnMessageReceived(message);
                }
            },
            onInterruptionDetected: async () =>
            {
                if (settings.InterruptResponse)
                {
                    // Reset states
                    _conn.ResetResponseState();

                    var data = _conn.OnModelUserInterrupted();
                    await (responseToUser?.Invoke(data) ?? Task.CompletedTask);
                }

                var res = _conn.OnUserSpeechDetected();
                await (responseToUser?.Invoke(res) ?? Task.CompletedTask);
            });
    }

    public RealtimeHubConnection SetHubConnection(string conversationId)
    {
        _conn = new RealtimeHubConnection
        {
            ConversationId = conversationId
        };

        return _conn;
    }
}
