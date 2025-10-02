using Humanizer;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Retry;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Serilog.Context;
using MainCore.Tasks.Base;
using Timer = System.Timers.Timer;

namespace MainCore.Services
{
    [RegisterSingleton<ITimerManager, TimerManager>]
    public sealed class TimerManager : ITimerManager
    {
        private readonly Dictionary<AccountId, Timer> _timers = [];

        private bool _isShutdown = false;

        private readonly ITaskManager _taskManager;
        private readonly IRxQueue _rxQueue;
        private readonly ICustomServiceScopeFactory _serviceScopeFactory;
        private readonly Microsoft.Extensions.Logging.ILogger<TimerManager> _logger;

        private static ResiliencePropertyKey<ContextData> contextDataKey = new(nameof(ContextData));
        private readonly ResiliencePipeline<Result> _pipeline;

        private readonly ConcurrentDictionary<AccountId, (string TaskKey, DateTime ExecuteAt)> _lastScheduledTask = new();
        private readonly ConcurrentDictionary<AccountId, (int MaxMinutes, int AutoUnpauseMinutes, int BaseRetry, int MaxRetry)> _lastSchedulerSettings = new();

        // Resilience defaults (overridden by account settings at runtime)
        private const int DefaultMaxTaskExecutionMinutes = 5; // safety timeout per task
        private const int DefaultBaseRetrySeconds = 30;       // base backoff window
        private const int DefaultMaxRetrySeconds = 600;       // cap backoff at 10 minutes
        private const int DefaultAutoUnpauseMinutes = 5;

        public TimerManager(ITaskManager taskManager, ICustomServiceScopeFactory serviceScopeFactory, IRxQueue rxQueue, Microsoft.Extensions.Logging.ILogger<TimerManager> logger)
        {
            _taskManager = taskManager;
            _serviceScopeFactory = serviceScopeFactory;
            _rxQueue = rxQueue;
            _logger = logger;

            Func<OnRetryArguments<Result>, ValueTask> OnRetry = async static args =>
            {
                await Task.CompletedTask;
                if (!args.Context.Properties.TryGetValue(contextDataKey, out var contextData)) return;

                var (taskName, browser) = contextData;
                var error = args.Outcome;
                if (error.Exception is not null)
                {
                    var exception = error.Exception;
                    browser.Logger.Error(exception, "{Message}", exception.Message);
                }
                if (error.Result is not null)
                {
                    var message = string.Join(Environment.NewLine, error.Result.Reasons.Select(e => e.Message));
                    if (!string.IsNullOrEmpty(message))
                    {
                        browser.Logger.Warning("Task {TaskName} failed", taskName, message);
                        browser.Logger.Warning("{Message}", message);
                    }
                }

                browser.Logger.Warning("{TaskName} will retry after {RetryDelay} (#{AttemptNumber} times)", taskName, args.RetryDelay.Humanize(3, minUnit: Humanizer.Localisation.TimeUnit.Second), args.AttemptNumber + 1);
            };

            var retryOptions = new RetryStrategyOptions<Result>()
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(30),
                UseJitter = true,
                BackoffType = DelayBackoffType.Exponential,
                ShouldHandle = new PredicateBuilder<Result>()
                   .Handle<Exception>()
                   .HandleResult(static x => x.HasError<Retry>()),
                OnRetry = OnRetry
            };

            _pipeline = new ResiliencePipelineBuilder<Result>()
                .AddRetry(retryOptions)
                .Build();
        }

        public async Task Execute(AccountId accountId)
        {
            using var accountScope = LogContext.PushProperty("AccountId", accountId.Value.ToString());
            _logger.LogDebug("Timer tick evaluating queue");

            var taskQueue = _taskManager.GetTaskQueue(accountId);

            var status = taskQueue.Status;

            // Load scheduler settings for this account
            using var cfgScope = _serviceScopeFactory.CreateScope(accountId);
            var cfgProvider = cfgScope.ServiceProvider;
            var cfgContext = cfgProvider.GetRequiredService<AppDbContext>();
            var maxTaskMinutes = cfgContext.ByName(accountId, AccountSettingEnums.Scheduler_MaxTaskMinutes);
            if (maxTaskMinutes <= 0) maxTaskMinutes = DefaultMaxTaskExecutionMinutes;
            var autoUnpauseMinutes = cfgContext.ByName(accountId, AccountSettingEnums.Scheduler_AutoUnpauseMinutes);
            if (autoUnpauseMinutes <= 0) autoUnpauseMinutes = DefaultAutoUnpauseMinutes;
            var baseRetrySeconds = cfgContext.ByName(accountId, AccountSettingEnums.Scheduler_RetryBaseSeconds);
            if (baseRetrySeconds <= 0) baseRetrySeconds = DefaultBaseRetrySeconds;
            var maxRetrySeconds = cfgContext.ByName(accountId, AccountSettingEnums.Scheduler_RetryMaxSeconds);
            if (maxRetrySeconds <= 0) maxRetrySeconds = DefaultMaxRetrySeconds;
            var autoUnpauseAfter = TimeSpan.FromMinutes(autoUnpauseMinutes);

            var lastSettings = _lastSchedulerSettings.GetOrAdd(accountId, _ => (0, 0, 0, 0));
            if (lastSettings.MaxMinutes != maxTaskMinutes || lastSettings.AutoUnpauseMinutes != autoUnpauseMinutes || lastSettings.BaseRetry != baseRetrySeconds || lastSettings.MaxRetry != maxRetrySeconds)
            {
                _logger.LogInformation("Scheduler settings: MaxTaskMinutes={MaxTaskMinutes}, AutoUnpauseMinutes={AutoUnpauseMinutes}, RetryBaseSeconds={RetryBaseSeconds}, RetryMaxSeconds={RetryMaxSeconds}", maxTaskMinutes, autoUnpauseMinutes, baseRetrySeconds, maxRetrySeconds);
                _lastSchedulerSettings[accountId] = (maxTaskMinutes, autoUnpauseMinutes, baseRetrySeconds, maxRetrySeconds);
            }
            if (status != StatusEnums.Online)
            {
                // Auto-unpause watchdog: if paused too long and tasks are pending, resume
                if (status == StatusEnums.Paused && (DateTime.Now - taskQueue.StatusChangedAt) > autoUnpauseAfter)
                {
                    _logger.LogInformation("Auto-unpausing account after {Minutes} minute(s) paused", autoUnpauseMinutes);
                    _taskManager.SetStatus(accountId, StatusEnums.Online);
                }
                else
                {
                    _logger.LogInformation("Skipping execution because status is {Status}", status);
                    return;
                }
            }

            var tasks = taskQueue.Tasks;
            if (tasks.Count == 0)
            {
                _logger.LogInformation("No pending tasks");
                return;
            }

            var task = tasks[0];
            using var villageScope = task is VillageTask villageTask ? LogContext.PushProperty("VillageId", villageTask.VillageId.Value.ToString()) : null;

            if (task.ExecuteAt > DateTime.Now)
            {
                var snapshot = _lastScheduledTask.GetOrAdd(accountId, _ => (string.Empty, DateTime.MinValue));
                if (snapshot.TaskKey != task.Key || snapshot.ExecuteAt != task.ExecuteAt)
                {
                    _logger.LogInformation("Next task {TaskName} scheduled at {ExecuteAt:o}", task.Description, task.ExecuteAt);
                    _lastScheduledTask[accountId] = (task.Key, task.ExecuteAt);
                }
                return;
            }

            _logger.LogInformation("Executing task {TaskName} (Key {TaskKey})", task.Description, task.Key);
            _lastScheduledTask[accountId] = (task.Key, task.ExecuteAt);

            taskQueue.IsExecuting = true;
            var cts = new CancellationTokenSource();
            taskQueue.CancellationTokenSource = cts;

            task.Stage = StageEnums.Executing;
            task.LastStartedAt = DateTime.Now;
            task.DeadlineAt = task.LastStartedAt.Value.AddMinutes(maxTaskMinutes);
            // Apply safety timeout
            cts.CancelAfter(TimeSpan.FromMinutes(maxTaskMinutes));
            _rxQueue.Enqueue(new TasksModified(accountId));

            var cacheExecuteTime = task.ExecuteAt;

            using var scope = _serviceScopeFactory.CreateScope(accountId);

            var browser = scope.ServiceProvider.GetRequiredService<IChromeBrowser>();
            var logger = browser.Logger;

            var contextData = new ContextData(task.Description, browser);

            var context = ResilienceContextPool.Shared.Get(cts.Token);
            context.Properties.Set(contextDataKey, contextData);

            var poliResult = await _pipeline.ExecuteOutcomeAsync(
                async (ctx, state) => Outcome.FromResult(await scope.Execute(state, ctx.CancellationToken)),
                context,
                task);

            ResilienceContextPool.Shared.Return(context);

            task.Stage = StageEnums.Waiting;
            _rxQueue.Enqueue(new TasksModified(accountId));

            taskQueue.IsExecuting = false;

            cts.Dispose();
            taskQueue.CancellationTokenSource = null;

            if (poliResult.Exception is not null)
            {
                var ex = poliResult.Exception;

                if (ex is OperationCanceledException)
                {
                    logger.Information("Pause button is pressed");
                }
                else
                {
                    var filename = await browser.Screenshot();
                    logger.Information("Screenshot saved as {FileName}", filename);
                    if (ex is OperationCanceledException && task.DeadlineAt.HasValue && DateTime.Now >= task.DeadlineAt.Value)
                    {
                        logger.Warning("Task {TaskName} timed out after {Minutes} minute(s), will be rescheduled.", task.Description, maxTaskMinutes);
                    }
                    else
                    {
                        logger.Warning("There is something wrong. Bot is pausing. Last exception is");
                        logger.Error(ex, "{Message}", ex.Message);
                    }
                }

                _logger.LogInformation("Exception encountered; rescheduling task and continuing");
                // Exponential backoff for unexpected exceptions
                task.RetryAttempt = Math.Min(task.RetryAttempt + 1, 10);
                var backoff = Math.Min(maxRetrySeconds, baseRetrySeconds * (int)Math.Pow(2, task.RetryAttempt - 1));
                var jitter = Random.Shared.Next(0, baseRetrySeconds);
                task.ExecuteAt = DateTime.Now.AddSeconds(backoff + jitter);
                _logger.LogInformation("Rescheduled after exception with backoff {Backoff}s (+jitter), attempt {Attempt}", backoff, task.RetryAttempt);
                _taskManager.ReOrder(accountId);
            }

            if (poliResult.Result is not null)
            {
                var result = poliResult.Result;
                if (result.IsFailed)
                {
                    var message = string.Join(Environment.NewLine, result.Reasons.Select(e => e.Message));
                    if (!string.IsNullOrEmpty(message))
                    {
                        logger.Warning("Task {TaskName} failed", task.Description, message);
                        logger.Warning("{Message}", message);
                    }

                    if (result.HasError<Stop>() || result.HasError<Retry>())
                    {
                        var filename = await browser.Screenshot();
                        logger.Information(messageTemplate: "Screenshot saved as {FileName}", filename);
                        if (result.HasError<Stop>())
                        {
                            task.RetryAttempt = Math.Min(task.RetryAttempt + 1, 10);
                            var backoff = Math.Min(maxRetrySeconds, baseRetrySeconds * (int)Math.Pow(2, task.RetryAttempt - 1));
                            var jitter = Random.Shared.Next(0, baseRetrySeconds);
                            var delay = TimeSpan.FromSeconds(backoff + jitter);
                            task.ExecuteAt = DateTime.Now.Add(delay);
                            _taskManager.ReOrder(accountId);
                            _taskManager.SetStatus(accountId, StatusEnums.Online);
                            _logger.LogInformation("Task requested stop; rescheduled to {ExecuteAt:o} (backoff {Backoff}s, attempt {Attempt})", task.ExecuteAt, backoff, task.RetryAttempt);
                        }
                        else
                        {
                            // Retry: exponential backoff and continue without pausing the account
                            task.RetryAttempt = Math.Min(task.RetryAttempt + 1, 10);
                            var backoff = Math.Min(maxRetrySeconds, baseRetrySeconds * (int)Math.Pow(2, task.RetryAttempt - 1));
                            var jitter = Random.Shared.Next(0, baseRetrySeconds);
                            var delay = TimeSpan.FromSeconds(backoff + jitter);
                            task.ExecuteAt = DateTime.Now.Add(delay);
                            _taskManager.ReOrder(accountId);
                            _logger.LogInformation("Task requested retry; rescheduled to {ExecuteAt:o} (backoff {Backoff}s, attempt {Attempt})", task.ExecuteAt, backoff, task.RetryAttempt);
                        }
                    }
                    else if (result.HasError<Skip>())
                    {
                        if (task.ExecuteAt == cacheExecuteTime)
                        {
                            _taskManager.Remove(accountId, task);
                            _logger.LogInformation("Skipped task removed from queue");
                        }
                        else
                        {
                            _taskManager.ReOrder(accountId);
                            logger.Information("Schedule next run at {Time}", task.ExecuteAt.ToString("yyyy-MM-dd HH:mm:ss"));
                            _logger.LogInformation("Task rescheduled to {ExecuteAt:o}", task.ExecuteAt);
                        }
                    }
                    else if (result.HasError<Cancel>())
                    {
                        _logger.LogInformation("Task requested cancel; rescheduling");
                        task.RetryAttempt = Math.Min(task.RetryAttempt + 1, 10);
                        var backoff = Math.Min(maxRetrySeconds, baseRetrySeconds * (int)Math.Pow(2, task.RetryAttempt - 1));
                        var jitter = Random.Shared.Next(0, baseRetrySeconds);
                        task.ExecuteAt = DateTime.Now.AddSeconds(backoff + jitter);
                        _taskManager.ReOrder(accountId);
                    }
                }
                else
                {
                    if (task.ExecuteAt == cacheExecuteTime)
                    {
                        _taskManager.Remove(accountId, task);
                        _logger.LogInformation("Completed task removed from queue");
                    }
                    else
                    {
                        _taskManager.ReOrder(accountId);
                        logger.Information("Schedule next run at {Time}", task.ExecuteAt.ToString("yyyy-MM-dd HH:mm:ss"));
                        _logger.LogInformation("Task rescheduled to {ExecuteAt:o}", task.ExecuteAt);
                    }

                    // Reset retry metadata on success path
                    task.RetryAttempt = 0;
                    task.LastStartedAt = null;
                    task.DeadlineAt = null;
                }
            }

            var delayService = scope.ServiceProvider.GetRequiredService<IDelayService>();
            await delayService.DelayTask();
        }

        public void Shutdown()
        {
            _isShutdown = true;
            foreach (var timer in _timers.Values)
            {
                timer.Dispose();
            }
        }

        public void Start(AccountId accountId)
        {
            if (!_timers.ContainsKey(accountId))
            {
                var timer = new Timer(1000) { AutoReset = false };
                timer.Elapsed += async (sender, e) =>
                {
                    if (_isShutdown) return;
                    await Execute(accountId);
                    timer.Start();
                };

                _timers.Add(accountId, timer);
                timer.Start();
            }
        }

        public record ContextData(string TaskName, IChromeBrowser Browser);
    }
}

