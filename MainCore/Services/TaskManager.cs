using MainCore.Tasks.Base;
using Serilog.Context;
using Microsoft.Extensions.Logging;

namespace MainCore.Services
{
    [RegisterSingleton<ITaskManager, TaskManager>]
    public sealed class TaskManager : ITaskManager
    {
        private readonly Dictionary<AccountId, TaskQueue> _queues = new();
        private readonly IRxQueue _rxQueue;
        private readonly Microsoft.Extensions.Logging.ILogger<TaskManager> _logger;

        public TaskManager(IRxQueue rxQueue, Microsoft.Extensions.Logging.ILogger<TaskManager> logger)
        {
            _rxQueue = rxQueue;
            _logger = logger;
        }

        public BaseTask? GetCurrentTask(AccountId accountId)
        {
            var tasks = GetTaskList(accountId);
            return tasks.Find(x => x.Stage == StageEnums.Executing);
        }

        public async Task StopCurrentTask(AccountId accountId)
        {
            using var accountScope = PushAccountScope(accountId);
            _logger.LogDebug("StopCurrentTask requested");

            var cts = GetCancellationTokenSource(accountId);
            if (cts is not null)
            {
                _logger.LogDebug("Cancelling current task token");
                await cts.CancelAsync();
            }

            BaseTask? currentTask;
            do
            {
                currentTask = GetCurrentTask(accountId);
                if (currentTask is null) break;
                await Task.Delay(500);
            }
            while (currentTask.Stage != StageEnums.Waiting);

            _logger.LogDebug("Current task halted; setting status to Paused");
            SetStatus(accountId, StatusEnums.Paused);
        }

        public void AddOrUpdate<T>(T task, bool first = false) where T : AccountTask
        {
            using var accountScope = PushAccountScope(task.AccountId);
            using var villageScope = task is VillageTask villageTask ? PushVillageScope(villageTask.VillageId) : null;

            var existingTask = Get<T>(task.AccountId, task.Key);
            if (existingTask is null)
            {
                _logger.LogDebug("Queueing new task {TaskType} (Key {TaskKey}) for {ExecuteAt:o} (first: {First})", task.GetType().Name, task.Key, task.ExecuteAt, first);
                Add(task, first);
                return;
            }

            existingTask.ExecuteAt = task.ExecuteAt;
            _logger.LogDebug("Updating task {TaskType} (Key {TaskKey}) to {ExecuteAt:o} (first: {First})", existingTask.GetType().Name, existingTask.Key, existingTask.ExecuteAt, first);
            Update(existingTask, first);
        }

        public void Add<T>(T task, bool first = false) where T : AccountTask
        {
            AddTask(task, first);
        }

        private T? Get<T>(AccountId accountId, string key) where T : BaseTask
        {
            var task = GetTaskList(accountId)
                .OfType<T>()
                .FirstOrDefault(x => x.Key == key);
            return task;
        }

        public bool IsExist<T>(AccountId accountId) where T : BaseTask
        {
            var tasks = GetTaskList(accountId)
                .OfType<T>();
            return tasks.Any(x => x.Key == $"{accountId}");
        }

        public bool IsExist<T>(AccountId accountId, VillageId villageId) where T : BaseTask
        {
            var tasks = GetTaskList(accountId)
                .OfType<T>();
            return tasks.Any(x => x.Key == $"{accountId}-{villageId}");
        }

        private void AddTask(AccountTask task, bool first)
        {
            using var accountScope = PushAccountScope(task.AccountId);
            using var villageScope = task is VillageTask villageTask ? PushVillageScope(villageTask.VillageId) : null;

            var tasks = GetTaskList(task.AccountId);

            if (first)
            {
                var firstTask = tasks.FirstOrDefault();
                if (firstTask is not null && firstTask.ExecuteAt < task.ExecuteAt)
                {
                    _logger.LogDebug("Adjusting execution time for task {TaskType} from {Original:o}", task.GetType().Name, task.ExecuteAt);
                    task.ExecuteAt = firstTask.ExecuteAt.AddHours(-1);
                }
            }

            tasks.Add(task);
            _logger.LogDebug("Task {TaskType} (Key {TaskKey}) enqueued; queue size = {QueueSize}", task.GetType().Name, task.Key, tasks.Count);

            if (task is VillageTask villageTaskAdded)
            {
                _rxQueue.Enqueue(new VillageTaskAdded(villageTaskAdded));
            }

            ReOrder(task.AccountId, tasks);
        }

        private void Update(AccountTask task, bool first)
        {
            using var accountScope = PushAccountScope(task.AccountId);
            using var villageScope = task is VillageTask villageTask ? PushVillageScope(villageTask.VillageId) : null;

            var tasks = GetTaskList(task.AccountId);

            if (first)
            {
                var firstTask = tasks.FirstOrDefault();
                if (firstTask is not null && firstTask.ExecuteAt < task.ExecuteAt)
                {
                    _logger.LogDebug("Adjusting execution time for updated task {TaskType} from {Original:o}", task.GetType().Name, task.ExecuteAt);
                    task.ExecuteAt = firstTask.ExecuteAt.AddHours(-1);
                }
            }

            _logger.LogDebug("Task {TaskType} (Key {TaskKey}) updated; reordering queue", task.GetType().Name, task.Key);
            ReOrder(task.AccountId, tasks);
        }

        public void Remove(AccountId accountId, BaseTask task)
        {
            using var accountScope = PushAccountScope(accountId);
            using var villageScope = task is VillageTask villageTask ? PushVillageScope(villageTask.VillageId) : null;

            var tasks = GetTaskList(accountId);
            if (tasks.Remove(task))
            {
                _logger.LogDebug("Removed task {TaskType} (Key {TaskKey})", task.GetType().Name, task.Key);
                ReOrder(accountId, tasks);
            }
            else
            {
                _logger.LogDebug("Attempted to remove task {TaskType} (Key {TaskKey}) but it was not found", task.GetType().Name, task.Key);
            }
        }

        public void Remove<T>(AccountId accountId) where T : AccountTask
        {
            using var accountScope = PushAccountScope(accountId);

            var tasks = GetTaskList(accountId);
            var task = tasks.OfType<T>().FirstOrDefault(x => x.AccountId == accountId);
            if (task is null)
            {
                _logger.LogDebug("No task of type {TaskType} found to remove", typeof(T).Name);
                return;
            }

            using var villageScope = task is VillageTask villageTask ? PushVillageScope(villageTask.VillageId) : null;
            tasks.Remove(task);
            _logger.LogDebug("Removed task {TaskType} (Key {TaskKey})", task.GetType().Name, task.Key);
            ReOrder(accountId, tasks);
        }

        public void Remove<T>(AccountId accountId, VillageId villageId) where T : VillageTask
        {
            using var accountScope = PushAccountScope(accountId);
            using var villageScope = PushVillageScope(villageId);

            var tasks = GetTaskList(accountId);
            var task = tasks.OfType<T>().FirstOrDefault(x => x.AccountId == accountId && x.VillageId == villageId);
            if (task is null)
            {
                _logger.LogDebug("No village task of type {TaskType} found to remove", typeof(T).Name);
                return;
            }

            tasks.Remove(task);
            _logger.LogDebug("Removed village task {TaskType} (Key {TaskKey})", task.GetType().Name, task.Key);
            ReOrder(accountId, tasks);
        }

        public void ReOrder(AccountId accountId)
        {
            using var accountScope = PushAccountScope(accountId);
            var tasks = GetTaskList(accountId);
            ReOrder(accountId, tasks);
        }

        public void Clear(AccountId accountId)
        {
            using var accountScope = PushAccountScope(accountId);
            var tasks = GetTaskList(accountId);
            if (tasks.Count == 0)
            {
                _logger.LogDebug("Clear requested but queue already empty");
                return;
            }

            var removed = tasks.Count;
            tasks.Clear();
            _logger.LogDebug("Cleared {RemovedCount} tasks", removed);
            _rxQueue.Enqueue(new TasksModified(accountId));
        }

        private void ReOrder(AccountId accountId, List<BaseTask> tasks)
        {
            using var accountScope = PushAccountScope(accountId);
            _rxQueue.Enqueue(new TasksModified(accountId));
            if (tasks.Count <= 1)
            {
                _logger.LogDebug("Reorder skipped; queue size {QueueSize}", tasks.Count);
                return;
            }

            tasks.Sort((x, y) => DateTime.Compare(x.ExecuteAt, y.ExecuteAt));
            var next = tasks.First();
            using var villageScope = next is VillageTask villageTask ? PushVillageScope(villageTask.VillageId) : null;
            _logger.LogDebug("Reordered queue; next task {TaskType} (Key {TaskKey}) at {ExecuteAt:o}", next.GetType().Name, next.Key, next.ExecuteAt);
        }

        public List<BaseTask> GetTaskList(AccountId accountId)
        {
            var queue = GetTaskQueue(accountId);
            return queue.Tasks;
        }

        public StatusEnums GetStatus(AccountId accountId)
        {
            var queue = GetTaskQueue(accountId);
            return queue.Status;
        }

        public void SetStatus(AccountId accountId, StatusEnums status)
        {
            using var accountScope = PushAccountScope(accountId);
            var queue = GetTaskQueue(accountId);
            if (queue.Status != status)
            {
                _logger.LogDebug("Status change {OldStatus} -> {NewStatus}", queue.Status, status);
            }
            queue.Status = status;
            queue.StatusChangedAt = DateTime.Now;
            _rxQueue.Enqueue(new StatusModified(accountId, status));
        }

        private CancellationTokenSource? GetCancellationTokenSource(AccountId accountId)
        {
            var queue = GetTaskQueue(accountId);
            return queue.CancellationTokenSource;
        }

        public bool IsExecuting(AccountId accountId)
        {
            var queue = GetTaskQueue(accountId);
            return queue.IsExecuting;
        }

        public TaskQueue GetTaskQueue(AccountId accountId)
        {
            if (_queues.ContainsKey(accountId))
            {
                return _queues[accountId];
            }

            using var accountScope = PushAccountScope(accountId);
            _logger.LogDebug("Creating task queue for account");
            var queue = new TaskQueue();
            _queues.Add(accountId, queue);
            return queue;
        }

        private static IDisposable PushAccountScope(AccountId accountId) =>
            LogContext.PushProperty("AccountId", accountId.Value.ToString());

        private static IDisposable? PushVillageScope(VillageId villageId) =>
            LogContext.PushProperty("VillageId", villageId.Value.ToString());
    }

    public class TaskQueue
    {
        public bool IsExecuting { get; set; } = false;
        public StatusEnums Status { get; set; } = StatusEnums.Offline;
        public DateTime StatusChangedAt { get; set; } = DateTime.Now;
        public CancellationTokenSource? CancellationTokenSource { get; set; }
        public List<BaseTask> Tasks { get; } = [];
    }
}
