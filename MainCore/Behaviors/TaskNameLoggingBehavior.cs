using System.Diagnostics;

namespace MainCore.Behaviors
{
    public sealed class TaskNameLoggingBehavior<TRequest, TResponse>
        : Behavior<TRequest, TResponse>
        where TRequest : ITask
    {
        private readonly ILogger _logger;

        public TaskNameLoggingBehavior(ILogger logger)
        {
            _logger = logger;
        }

        public override async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken)
        {
            var description = request.Description;
            var stopwatch = Stopwatch.StartNew();

            _logger.Debug("Starting task {TaskName} (ExecuteAt: {ExecuteAt:o})", description, request.ExecuteAt);
            _logger.Information("Task {TaskName} is started", description);

            try
            {
                var response = await Next(request, cancellationToken);
                stopwatch.Stop();

                _logger.Debug("Finished task {TaskName} in {Elapsed}ms", description, stopwatch.Elapsed.TotalMilliseconds);
                _logger.Information("Task {TaskName} is finished", description);

                return response;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                _logger.Debug(exception, "Task {TaskName} threw after {Elapsed}ms", description, stopwatch.Elapsed.TotalMilliseconds);
                throw;
            }
        }
    }
}
