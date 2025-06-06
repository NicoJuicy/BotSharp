using BotSharp.Abstraction.Conversations.Enums;
using BotSharp.Abstraction.MLTasks;
using System.Diagnostics;

namespace BotSharp.Core.Conversations.Services;

public class TokenStatistics : ITokenStatistics
{
    private int _promptTokenCount = 0;
    private float _promptCost = 0f;
    private int _completionTokenCount = 0;
    private float _completionCost = 0f;
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    public int Total => _promptTokenCount + _completionTokenCount;
    public string _model;
    private Stopwatch _timer;

    public float Cost => _promptCost + _completionCost;
    public float AccumulatedCost
    {
        get 
        {
            var stat = _services.GetRequiredService<IConversationStateService>();
            return float.Parse(stat.GetState("llm_total_cost", "0"));
        }
    }

    public TokenStatistics(IServiceProvider services, ILogger<TokenStatistics> logger) 
    { 
        _services = services;
        _logger = logger;
    }

    public void AddToken(TokenStatsModel stats, RoleDialogModel message)
    {
        _model = stats.Model;
        _promptTokenCount += stats.TotalInputTokens;
        _completionTokenCount += stats.TotalOutputTokens;

        var settingsService = _services.GetRequiredService<ILlmProviderService>();
        var settings = settingsService.GetSetting(stats.Provider, _model);

        var deltaTextInputCost = stats.TextInputTokens / 1000f * (settings.Cost?.TextInputCost ?? 0f);
        var deltaCachedTextInputCost = stats.CachedTextInputTokens / 1000f * (settings.Cost?.CachedTextInputCost ?? 0f);
        var deltaAudioInputCost = stats.AudioInputTokens / 1000f * (settings.Cost?.AudioInputCost ?? 0f);
        var deltaCachedAudioInputCost = stats.CachedAudioInputTokens / 1000f * (settings.Cost?.CachedAudioInputCost ?? 0f);

        var deltaTextOutputCost = stats.TextOutputTokens / 1000f * (settings.Cost?.TextOutputCost ?? 0f);
        var deltaAudioOutputCost = stats.AudioOutputTokens / 1000f * (settings.Cost?.AudioOutputCost ?? 0f);

        var deltaPromptCost = deltaTextInputCost + deltaCachedTextInputCost + deltaAudioInputCost + deltaCachedAudioInputCost;
        var deltaCompletionCost = deltaTextOutputCost + deltaAudioOutputCost;

        var deltaTotal = deltaPromptCost + deltaCompletionCost;
        _promptCost += deltaPromptCost;
        _completionCost += deltaCompletionCost;

        // Accumulated Token
        var stat = _services.GetRequiredService<IConversationStateService>();
        var inputCount = int.Parse(stat.GetState("prompt_total", "0"));
        stat.SetState("prompt_total", stats.TotalInputTokens + inputCount, isNeedVersion: false, source: StateSource.Application);
        var outputCount = int.Parse(stat.GetState("completion_total", "0"));
        stat.SetState("completion_total", stats.TotalOutputTokens + outputCount, isNeedVersion: false, source: StateSource.Application);

        // Total cost
        var total_cost = float.Parse(stat.GetState("llm_total_cost", "0"));
        total_cost += deltaTotal;
        stat.SetState("llm_total_cost", total_cost, isNeedVersion: false, source: StateSource.Application);

        // Save stats
        var metric = StatsMetric.AgentLlmCost;
        var dim = "agent";
        var agentId = message.CurrentAgentId ?? string.Empty;
        var globalStats = _services.GetRequiredService<IBotSharpStatsService>();
        var delta = new BotSharpStatsDelta
        {
            AgentId = agentId,
            RecordTime = DateTime.UtcNow,
            IntervalType = StatsInterval.Day,
            LlmCostDelta = new()
            {
                PromptTokensDelta = stats.TotalInputTokens,
                CompletionTokensDelta = stats.TotalOutputTokens,
                PromptTotalCostDelta = deltaPromptCost,
                CompletionTotalCostDelta = deltaCompletionCost
            }
        };
        globalStats.UpdateStats($"global-{metric}-{dim}-{agentId}", delta);
    }

    public void PrintStatistics()
    {
        if (_timer == null)
        {
            _timer = Stopwatch.StartNew();
        }
        else
        {
            _timer.Start();
        }
        var stats = $"Token Usage: {_promptTokenCount} prompt + {_completionTokenCount} completion = {Total} total tokens ({_timer.ElapsedMilliseconds / 1000f:f2}s). One-Way cost: {Cost:C4}, accumulated cost: {AccumulatedCost:C4}. [{_model}]";
#if DEBUG
        Console.WriteLine(stats);
#else
        _logger.LogInformation(stats);
#endif
    }

    public void StartTimer()
    {
        if (_timer == null)
        {
            _timer = Stopwatch.StartNew();
        }
        else
        {
            _timer.Start();
        }
    }

    public void StopTimer()
    {
        if (_timer == null)
        {
            return;
        }
        _timer.Stop();
    }
}
