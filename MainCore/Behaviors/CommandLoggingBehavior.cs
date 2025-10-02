using System.Diagnostics;

namespace MainCore.Behaviors
{
    public sealed class CommandLoggingBehavior<TRequest, TResponse>
        : Behavior<TRequest, TResponse>
        where TRequest : ICommand
    {
        private readonly ILogger _logger;

        private static readonly string[] ExcludedCommandNames = new[]
        {
            "Update",
            "Delay",
            "NextExecute"
        };

        public CommandLoggingBehavior(ILogger logger)
        {
            _logger = logger;
        }

        public override async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken)
        {
            var rawName = request.GetType().FullName ?? typeof(TRequest).Name;
            var includeInfo = !ExcludedCommandNames.Any(rawName.Contains);

            var cleanedName = rawName
                .Replace("MainCore.", string.Empty)
                .Replace("+Command", string.Empty);

            var properties = request.GetType().GetProperties()
                .Where(prop => !prop.Name.Equals("AccountId"))
                .Where(prop => !prop.Name.Equals("VillageId"))
                .ToDictionary(prop => prop.Name, prop =>
                {
                    var value = prop.GetValue(request);
                    return value is long[] array ? string.Join(",", array) : value?.ToString() ?? string.Empty;
                });

            var stopwatch = Stopwatch.StartNew();

            if (properties.Count == 0)
            {
                _logger.Debug("Starting command {Command}", cleanedName);
                if (includeInfo)
                {
                    _logger.Information("Execute {Name}", cleanedName);
                }
            }
            else
            {
                _logger.Debug("Starting command {Command} {@Payload}", cleanedName, properties);
                if (includeInfo)
                {
                    _logger.Information("Execute {Name} {@Dict}", cleanedName, properties);
                }
            }

            try
            {
                var response = await Next(request, cancellationToken);
                stopwatch.Stop();

                if (response is Result result)
                {
                    var outcome = result.IsSuccess ? "Succeeded" : "Failed";
                    _logger.Debug("Completed command {Command} in {Elapsed}ms ({Outcome})", cleanedName, stopwatch.Elapsed.TotalMilliseconds, outcome);

                    if (result.IsFailed)
                    {
                        var messages = result.Reasons
                            .Select(reason => reason.Message)
                            .Where(message => !string.IsNullOrWhiteSpace(message))
                            .ToArray();

                        if (messages.Length > 0)
                        {
                            _logger.Debug("Command {Command} failure reasons: {Reasons}", cleanedName, string.Join("; ", messages));
                        }
                    }
                }
                else
                {
                    _logger.Debug("Completed command {Command} in {Elapsed}ms", cleanedName, stopwatch.Elapsed.TotalMilliseconds);
                }

                return response;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                _logger.Debug(exception, "Command {Command} threw after {Elapsed}ms", cleanedName, stopwatch.Elapsed.TotalMilliseconds);
                throw;
            }
        }
    }
}
