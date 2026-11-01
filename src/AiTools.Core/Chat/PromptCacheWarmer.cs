using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AiTools.Abstractions;

namespace AiTools.Core.Chat;

/// <summary>提示缓存预热模式。对应 Pi 的 <c>cacheWarming</c> 设置。</summary>
public enum PromptCacheWarmingMode
{
    /// <summary>不预热。</summary>
    Off,

    /// <summary>仅在一轮 agent 运行期间（工具循环耗时较长时）刷新缓存，一轮结束后停止。</summary>
    Streaming,

    /// <summary>一轮结束后继续刷新，直到达到 <see cref="PromptCacheWarmingOptions.MaxIdleAge"/>。</summary>
    Idle
}

/// <summary>一次预热决策的结论。</summary>
public enum CacheWarmingAction
{
    /// <summary>发送一次刷新请求。</summary>
    Warm,

    /// <summary>停止预热，直到下一次真实请求。</summary>
    Stop
}

/// <summary>提示缓存预热的配置。</summary>
/// <remarks>
/// 默认值与 Pi 对齐：仅在轮内预热（<see cref="PromptCacheWarmingMode.Streaming"/>），
/// 只有预期节省达到 $0.05 才发送刷新请求，刷新点取缓存寿命的 90%。
/// </remarks>
public sealed class PromptCacheWarmingOptions
{
    /// <summary>获取或设置预热模式。默认 <see cref="PromptCacheWarmingMode.Streaming"/>。</summary>
    public PromptCacheWarmingMode Mode { get; set; } = PromptCacheWarmingMode.Streaming;

    /// <summary>获取或设置发送刷新请求所需的最低预期节省（美元）。默认 0.05。</summary>
    public double MinimumExpectedSavings { get; set; } = 0.05;

    /// <summary>获取或设置空闲阶段"下一次真实请求会到来"的概率估计。默认 0.15。</summary>
    public double IdleContinuationProbability { get; set; } = 0.15;

    /// <summary>获取或设置刷新点占缓存寿命的比例。默认 0.9。</summary>
    public double RefreshFraction { get; set; } = 0.9;

    /// <summary>获取或设置刷新点距过期至少保留的余量。默认 10 秒。</summary>
    public TimeSpan MinimumMargin { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>获取或设置轮内预热的最长持续时间。默认 1 小时。</summary>
    public TimeSpan MaxStreamingAge { get; set; } = TimeSpan.FromHours(1);

    /// <summary>获取或设置空闲预热的最长持续时间。默认 30 分钟。</summary>
    public TimeSpan MaxIdleAge { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>获取或设置单个请求最多刷新多少次。默认 24。</summary>
    public int MaxRefreshes { get; set; } = 24;
}

/// <summary>一次预热决策的输入与结论，宿主可展示或在扩展中改写。</summary>
public sealed class CacheWarmingDecision
{
    /// <summary>获取阶段：<c>streaming</c>（轮内）或 <c>idle</c>（轮间）。</summary>
    public string Phase { get; init; } = "streaming";

    /// <summary>获取模型名。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>获取最近一次真实请求的提示 token 数；未知时为 0。</summary>
    public int PromptTokens { get; init; }

    /// <summary>获取本次刷新的价格估算（缓存读 + 1 个输出 token）。</summary>
    public double WarmCost { get; init; }

    /// <summary>获取缓存丢失时下一次真实请求多付的价格。</summary>
    public double MissCost { get; init; }

    /// <summary>获取"下一次真实请求会在过期前到来"的概率估计。</summary>
    public double ContinuationProbability { get; init; }

    /// <summary>获取预期节省：<c>概率 × 丢失代价 − 刷新代价</c>。</summary>
    public double ExpectedSavings { get; init; }

    /// <summary>获取经济性是否可估算：提示规模与单价都已知时为 true。</summary>
    public bool EconomicsAvailable { get; init; }

    /// <summary>获取提示是否低于该模型的最小可缓存长度（此时刷新不会创建缓存条目）。</summary>
    public bool BelowCacheableMinimum { get; init; }

    /// <summary>获取该模型的最小可缓存长度（token）；0 表示无已知下限。</summary>
    public int MinimumCacheableTokens { get; init; }

    /// <summary>获取或设置结论。扩展可改写为 <see cref="CacheWarmingAction.Stop"/> 或反向开启。</summary>
    public CacheWarmingAction Action { get; set; }
}

/// <summary>预热器的当前状态，供状态栏展示（对应 Pi 的 <c>/session</c> 一行）。</summary>
public sealed class CacheWarmingStatus
{
    /// <summary>获取状态：<c>inactive</c>、<c>scheduled</c> 或 <c>refreshing</c>。</summary>
    public string State { get; init; } = "inactive";

    /// <summary>获取没有安排预热时的原因。</summary>
    public string? Reason { get; init; }

    /// <summary>获取下一次刷新的时间。</summary>
    public DateTimeOffset? NextWarmAt { get; init; }

    /// <summary>获取最近的（或终止预热的）决策。</summary>
    public CacheWarmingDecision? Decision { get; init; }

    /// <summary>获取决策是否被扩展改写过。</summary>
    public bool ExtensionOverride { get; init; }

    /// <summary>生成一行可读状态。</summary>
    public string Describe()
    {
        switch (State)
        {
            case "scheduled":
                return NextWarmAt.HasValue
                    ? string.Format(CultureInfo.InvariantCulture, "预热已排程（{0:HH:mm:ss}）", NextWarmAt.Value.ToLocalTime())
                    : "预热已排程";
            case "refreshing":
                return "正在刷新缓存";
            default:
                return string.IsNullOrEmpty(Reason) ? "未预热" : "未预热：" + Reason;
        }
    }
}

/// <summary>一次成功刷新的结果（只用于观察与展示，不进入模型上下文）。</summary>
public sealed class CacheWarmingResult
{
    /// <summary>获取模型名。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>获取阶段：<c>streaming</c> / <c>idle</c>。</summary>
    public string Phase { get; init; } = "streaming";

    /// <summary>获取刷新的提示 token 数。</summary>
    public int PromptTokens { get; init; }

    /// <summary>获取本次刷新的预估价格。</summary>
    public double WarmCost { get; init; }

    /// <summary>获取本次刷新的耗时。</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>获取服务商返回的用量（可计入会话总量，但不进入上下文）。</summary>
    public Usage? Usage { get; init; }

    /// <summary>获取这是该请求的第几次刷新。</summary>
    public int RefreshCount { get; init; }
}

/// <summary>要保活其提示缓存的请求，与真实请求逐字段一致（除输出上限）。</summary>
public sealed class PromptCacheWarmRequest
{
    /// <summary>获取用于发送刷新请求的客户端。</summary>
    public IChatClient Client { get; init; } = null!;

    /// <summary>获取真实请求的原样副本。</summary>
    public ChatRequest Request { get; init; } = null!;

    /// <summary>获取该模型上的缓存策略。</summary>
    public PromptCachePolicy Policy { get; init; } = PromptCachePolicy.None;

    /// <summary>获取最近一次真实请求的提示 token 数；未知时为 0。</summary>
    public int PromptTokens { get; init; }
}

/// <summary>
/// 通过重放请求把提示缓存保活。每次刷新都以同样的消息前缀、把输出上限压到 1 个 token 发出，
/// 因此会写入与真实请求相同的缓存条目，而开销接近一次缓存读。
/// </summary>
/// <remarks>
/// <para>
/// 刷新是尽力而为：任何失败都不会影响正在进行的对话，也不会重试。
/// 是否刷新由经济性决定——只有当 <c>概率 × 缓存丢失代价 − 刷新代价</c> 达到阈值时才会发送。
/// </para>
/// <para>
/// 刷新用量计入会话总量但不进入模型上下文（与 Pi 的 <c>cache_warm</c> 记录一致）。
/// </para>
/// </remarks>
public sealed class PromptCacheWarmer : IDisposable
{
    private readonly PromptCacheWarmingOptions _options;
    private readonly Func<CacheWarmingDecision, Task<CacheWarmingAction>>? _decide;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new object();
    private TrackedRequest? _tracked;
    private bool _runStarted;
    private bool _disposed;

    /// <summary>创建预热器。</summary>
    /// <param name="options">预热配置；null 时使用默认值。</param>
    /// <param name="decide">可选的决策改写回调（扩展接管）；异常时回退到内置结论。</param>
    /// <param name="delay">可选的等待实现，仅用于测试；null 时使用 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>。</param>
    public PromptCacheWarmer(
        PromptCacheWarmingOptions? options = null,
        Func<CacheWarmingDecision, Task<CacheWarmingAction>>? decide = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _options = options ?? new PromptCacheWarmingOptions();
        _decide = decide;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }

    /// <summary>每次成功刷新后触发。处理器异常被忽略。</summary>
    public event Action<CacheWarmingResult>? Warmed;

    /// <summary>获取预热配置。</summary>
    public PromptCacheWarmingOptions Options => _options;

    /// <summary>获取当前状态。</summary>
    public CacheWarmingStatus Status
    {
        get
        {
            lock (_gate)
            {
                if (_tracked == null)
                {
                    return new CacheWarmingStatus
                    {
                        State = "inactive",
                        Reason = _disposed ? "已释放" : _lastStopReason,
                        Decision = _lastDecision,
                        ExtensionOverride = _lastOverride
                    };
                }

                return _tracked.ToStatus();
            }
        }
    }

    private string? _lastStopReason;
    private CacheWarmingDecision? _lastDecision;
    private bool _lastOverride;

    /// <summary>标记一轮 agent 运行开始。轮内预热只在运行期间生效。</summary>
    public void OnRunStarted()
    {
        lock (_gate)
        {
            _runStarted = true;
        }
    }

    /// <summary>标记一轮 agent 运行结束。Streaming 模式据此停止预热，Idle 模式切换到空闲阶段。</summary>
    public void OnRunSettled()
    {
        TrackedRequest? tracked;
        lock (_gate)
        {
            _runStarted = false;
            tracked = _tracked;
        }

        if (tracked == null)
        {
            return;
        }

        if (_options.Mode != PromptCacheWarmingMode.Idle)
        {
            Stop(tracked, _options.Mode == PromptCacheWarmingMode.Off ? "预热已关闭" : "本轮已结束");
            return;
        }

        // 空闲阶段的时间窗与刷新点都不同，需要重新排程。
        Restart(tracked, "idle");
    }

    /// <summary>
    /// 记录"最近一次真实请求"，并（重新）安排刷新。
    /// 调用方应在每个模型请求结束后调用；不可预热的请求会停止已有预热（前缀已改变）。
    /// </summary>
    /// <param name="request">真实请求的原样副本。</param>
    /// <param name="isCurrent">可选的"仍然有效"判定；返回 false 时停止预热。</param>
    public void Track(PromptCacheWarmRequest request, Func<bool>? isCurrent = null)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.Client == null || request.Request == null)
        {
            throw new ArgumentException("A client and request are required.", nameof(request));
        }

        if (_options.Mode == PromptCacheWarmingMode.Off || !request.Policy.IsWarmable)
        {
            Cancel();
            return;
        }

        TrackedRequest tracked;
        TrackedRequest? previous;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            previous = _tracked;
            tracked = new TrackedRequest(request, isCurrent, _runStarted ? "streaming" : "idle");
            _tracked = tracked;
            _lastStopReason = null;
            _lastDecision = null;
            _lastOverride = false;
        }

        previous?.CancelLoop();
        StartLoop(tracked);
    }

    /// <summary>停止预热并清空状态。</summary>
    public void Cancel()
    {
        TrackedRequest? tracked;
        lock (_gate)
        {
            tracked = _tracked;
            _tracked = null;
        }

        tracked?.CancelLoop();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        TrackedRequest? tracked;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            tracked = _tracked;
            _tracked = null;
        }

        tracked?.CancelLoop();
    }

    private void Restart(TrackedRequest tracked, string phase)
    {
        lock (_gate)
        {
            if (_tracked != tracked || _disposed)
            {
                return;
            }

            tracked.Phase = phase;
        }

        tracked.CancelLoop();
        StartLoop(tracked);
    }

    private void StartLoop(TrackedRequest tracked)
    {
        // 每一轮等待都使用独立的取消源，避免阶段切换后复用已取消的令牌。
        Loop loop;
        lock (_gate)
        {
            if (_tracked != tracked || _disposed || tracked.Finished)
            {
                return;
            }

            loop = new Loop();
            tracked.CurrentLoop = loop;
        }

        _ = Task.Run(() => RunLoopAsync(tracked, loop));
    }

    private async Task RunLoopAsync(TrackedRequest tracked, Loop loop)
    {
        var token = loop.Token;
        try
        {
            while (true)
            {
                var reason = GetStopReason(tracked, loop);
                if (reason != null)
                {
                    Stop(tracked, reason);
                    return;
                }

                var delay = ComputeDelay(tracked.Request.Policy.Lifetime);
                if (delay == null)
                {
                    Stop(tracked, "缓存寿命过短");
                    return;
                }

                lock (_gate)
                {
                    if (tracked.CurrentLoop != loop)
                    {
                        return;
                    }

                    tracked.NextWarmAt = DateTimeOffset.UtcNow + delay.Value;
                }

                try
                {
                    await _delay(delay.Value, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                token.ThrowIfCancellationRequested();

                reason = GetStopReason(tracked, loop);
                if (reason != null)
                {
                    Stop(tracked, reason);
                    return;
                }

                var decision = Evaluate(tracked);
                var action = await ApplyDecisionAsync(tracked, decision).ConfigureAwait(false);
                if (action == CacheWarmingAction.Stop)
                {
                    Stop(tracked, decision.BelowCacheableMinimum
                        ? $"提示低于最小可缓存长度（{decision.MinimumCacheableTokens} tokens）"
                        : decision.EconomicsAvailable ? "预期收益低于阈值" : "缓存经济性不可估算");
                    return;
                }

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                Usage? usage = null;
                int refreshCount;
                lock (_gate)
                {
                    if (tracked.CurrentLoop != loop)
                    {
                        return;
                    }

                    tracked.Refreshing = true;
                    tracked.RefreshCount++;
                    refreshCount = tracked.RefreshCount;
                }

                try
                {
                    var response = await tracked.Request.Client
                        .CompleteAsync(BuildWarmRequest(tracked.Request.Request, tracked.Request.Policy), token)
                        .ConfigureAwait(false);
                    usage = response.Usage;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // 刷新失败不影响对话；停止本次预热，避免持续失败重试。
                    Stop(tracked, "刷新请求失败");
                    return;
                }
                finally
                {
                    stopwatch.Stop();
                    lock (_gate)
                    {
                        tracked.Refreshing = false;
                    }
                }

                try
                {
                    Warmed?.Invoke(new CacheWarmingResult
                    {
                        Model = tracked.Request.Request.Model,
                        Phase = tracked.Phase,
                        PromptTokens = tracked.Request.PromptTokens,
                        WarmCost = decision.WarmCost,
                        Duration = stopwatch.Elapsed,
                        Usage = usage,
                        RefreshCount = refreshCount
                    });
                }
                catch (Exception)
                {
                    // 观察者异常不影响预热循环。
                }

                lock (_gate)
                {
                    if (tracked.CurrentLoop != loop)
                    {
                        return;
                    }

                    if (tracked.RefreshCount >= Math.Max(1, _options.MaxRefreshes))
                    {
                        StopLocked(tracked, "已达到刷新次数上限");
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止路径。
        }
    }

    private async Task<CacheWarmingAction> ApplyDecisionAsync(TrackedRequest tracked, CacheWarmingDecision decision)
    {
        lock (_gate)
        {
            tracked.Decision = decision;
        }

        if (_decide == null)
        {
            return decision.Action;
        }

        CacheWarmingAction action;
        try
        {
            action = await _decide(decision).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 扩展失败时回退到内置结论。
            return decision.Action;
        }

        if (action != decision.Action)
        {
            lock (_gate)
            {
                tracked.ExtensionOverride = true;
            }
        }

        return action;
    }

    private string? GetStopReason(TrackedRequest tracked, Loop loop)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return "已释放";
            }

            if (_tracked != tracked || tracked.CurrentLoop != loop)
            {
                return "已被替换";
            }

            if (tracked.Finished)
            {
                return tracked.StopReason ?? "已停止";
            }

            if (_options.Mode == PromptCacheWarmingMode.Off)
            {
                return "预热已关闭";
            }
        }

        // 外部判定（例如会话或模型已切换）：缓存键已变，继续刷新只会写无用条目。
        if (!tracked.IsCurrent())
        {
            return "请求已失效";
        }

        if (tracked.Phase == "streaming")
        {
            lock (_gate)
            {
                if (_options.Mode == PromptCacheWarmingMode.Streaming && !_runStarted)
                {
                    return "本轮已结束";
                }
            }
        }

        var maxAge = tracked.Phase == "streaming" ? _options.MaxStreamingAge : _options.MaxIdleAge;
        if (maxAge > TimeSpan.Zero && DateTimeOffset.UtcNow - tracked.StartedAt > maxAge)
        {
            return "已超过预热时限";
        }

        return null;
    }

    private TimeSpan? ComputeDelay(TimeSpan? lifetime)
    {
        if (!lifetime.HasValue || lifetime.Value <= TimeSpan.Zero)
        {
            return null;
        }

        var fraction = _options.RefreshFraction;
        if (double.IsNaN(fraction) || double.IsInfinity(fraction) || fraction <= 0 || fraction > 1)
        {
            fraction = 0.9;
        }

        var margin = _options.MinimumMargin < TimeSpan.Zero ? TimeSpan.Zero : _options.MinimumMargin;
        var target = TimeSpan.FromTicks((long)(lifetime.Value.Ticks * fraction));
        var latest = lifetime.Value - margin;
        if (latest <= TimeSpan.Zero)
        {
            return null;
        }

        return target < latest ? target : latest;
    }

    private CacheWarmingDecision Evaluate(TrackedRequest tracked)
    {
        var pricing = tracked.Request.Policy.Pricing;
        var promptTokens = Math.Max(0, tracked.Request.PromptTokens);
        var streaming = tracked.Phase == "streaming";
        var probability = streaming ? 1.0 : Clamp(_options.IdleContinuationProbability, 0, 1);

        // 低于最小可缓存长度时服务端根本不会创建条目，刷新纯属浪费。
        var minimum = Math.Max(0, tracked.Request.Policy.MinimumCacheableTokens);
        var belowMinimum = minimum > 0 && promptTokens < minimum;
        var economicsAvailable = pricing != null && pricing.IsUsable && promptTokens > 0 && !belowMinimum;

        var warmCost = 0.0;
        var missCost = 0.0;
        if (economicsAvailable)
        {
            var tokens = promptTokens / 1_000_000.0;
            warmCost = tokens * pricing!.CachedInputPerMillion + (1.0 / 1_000_000.0) * pricing.OutputPerMillion;
            missCost = tokens * Math.Max(0, pricing.InputPerMillion - pricing.CachedInputPerMillion);
        }

        var savings = probability * missCost - warmCost;
        var action = economicsAvailable && savings >= _options.MinimumExpectedSavings
            ? CacheWarmingAction.Warm
            : CacheWarmingAction.Stop;

        return new CacheWarmingDecision
        {
            Phase = tracked.Phase,
            Model = tracked.Request.Request.Model,
            PromptTokens = promptTokens,
            WarmCost = warmCost,
            MissCost = missCost,
            ContinuationProbability = probability,
            ExpectedSavings = savings,
            EconomicsAvailable = economicsAvailable,
            BelowCacheableMinimum = belowMinimum,
            MinimumCacheableTokens = minimum,
            Action = action
        };
    }

    private static double Clamp(double value, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return min;
        }

        return value < min ? min : value > max ? max : value;
    }

    private void Stop(TrackedRequest tracked, string reason)
    {
        Loop? loop;
        lock (_gate)
        {
            // 已被新请求取代的旧循环不得污染当前状态：它的停止原因对用户没有意义。
            if (_tracked == tracked)
            {
                StopLocked(tracked, reason);
                _tracked = null;
            }
            else
            {
                tracked.Finished = true;
            }

            loop = tracked.TakeLoop();
        }

        loop?.Cancel();
    }

    private void StopLocked(TrackedRequest tracked, string reason)
    {
        tracked.Finished = true;
        tracked.StopReason = reason;
        _lastStopReason = reason;
        _lastDecision = tracked.Decision;
        _lastOverride = tracked.ExtensionOverride;
    }

    /// <summary>
    /// 构造重放请求：除输出上限外与真实请求完全一致。
    /// 输出上限取策略给出的值（Anthropic 为 0，零输出计费；其他服务商为 1）。
    /// </summary>
    /// <remarks>
    /// <c>ToolChoice</c> 与 <c>ResponseFormat</c> 会被清空：强制工具选择与 JSON Schema 约束
    /// 在"只写缓存不产出内容"的重放里没有意义，而 <c>max_tokens: 0</c> 与它们组合会被
    /// Anthropic 直接拒绝。参考 <c>PromptCachePolicy.WarmOutputTokens</c>。
    /// </remarks>
    private static ChatRequest BuildWarmRequest(ChatRequest request, PromptCachePolicy policy) => new()
    {
        Model = request.Model,
        SystemPrompt = request.SystemPrompt,
        Messages = request.Messages,
        Tools = request.Tools,
        ToolChoice = null,
        Temperature = request.Temperature,
        TopP = request.TopP,
        TopK = request.TopK,
        MaxOutputTokens = Math.Max(0, policy.WarmOutputTokens),
        StopSequences = request.StopSequences,
        ResponseFormat = null,
        AdditionalParameters = request.AdditionalParameters
    };

    /// <summary>一次等待循环的取消源。阶段切换时整体替换。</summary>
    private sealed class Loop
    {
        private readonly CancellationTokenSource _cts = new();

        public CancellationToken Token => _cts.Token;

        public void Cancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已释放：无需再取消。
            }
        }
    }

    private sealed class TrackedRequest
    {
        private readonly Func<bool>? _isCurrent;

        public TrackedRequest(PromptCacheWarmRequest request, Func<bool>? isCurrent, string phase)
        {
            Request = request;
            _isCurrent = isCurrent;
            Phase = phase;
            StartedAt = DateTimeOffset.UtcNow;
        }

        public PromptCacheWarmRequest Request { get; }

        public string Phase { get; set; }

        public DateTimeOffset StartedAt { get; }

        public DateTimeOffset? NextWarmAt { get; set; }

        public CacheWarmingDecision? Decision { get; set; }

        public bool ExtensionOverride { get; set; }

        public bool Refreshing { get; set; }

        public bool Finished { get; set; }

        public string? StopReason { get; set; }

        public int RefreshCount { get; set; }

        public Loop? CurrentLoop { get; set; }

        /// <summary>外部判定是否仍然有效（例如会话未被切换）。</summary>
        public bool IsCurrent()
        {
            if (Finished)
            {
                return false;
            }

            try
            {
                return _isCurrent == null || _isCurrent();
            }
            catch (Exception)
            {
                return false;
            }
        }

        public Loop? TakeLoop()
        {
            var loop = CurrentLoop;
            CurrentLoop = null;
            return loop;
        }

        public void CancelLoop() => TakeLoop()?.Cancel();

        public CacheWarmingStatus ToStatus()
        {
            if (Finished)
            {
                return new CacheWarmingStatus
                {
                    State = "inactive",
                    Reason = StopReason,
                    Decision = Decision,
                    ExtensionOverride = ExtensionOverride
                };
            }

            return new CacheWarmingStatus
            {
                State = Refreshing ? "refreshing" : "scheduled",
                NextWarmAt = Refreshing ? null : NextWarmAt,
                Decision = Decision,
                ExtensionOverride = ExtensionOverride
            };
        }
    }
}
