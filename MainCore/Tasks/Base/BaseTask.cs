namespace MainCore.Tasks.Base
{
    public abstract class BaseTask : ITask
    {
        public virtual string Key { get; } = "";
        public StageEnums Stage { get; set; } = StageEnums.Waiting;
        public DateTime ExecuteAt { get; set; } = DateTime.Now;
        // Resilience metadata: attempts, start/deadline
        public int RetryAttempt { get; set; } = 0;
        public DateTime? LastStartedAt { get; set; }
        public DateTime? DeadlineAt { get; set; }
        public virtual string Description { get; } = "";
        protected virtual string TaskName { get; } = "";

        public virtual bool CanStart(AppDbContext context) => true;
    }
}
