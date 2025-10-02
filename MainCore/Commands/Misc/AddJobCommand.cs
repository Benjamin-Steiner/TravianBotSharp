using System.Text.Json;

namespace MainCore.Commands.Misc
{
    [Handler]
    public static partial class AddJobCommand
    {
        public sealed record Command(VillageId VillageId, JobDto Job, bool ToTop = false) : IVillageCommand;

        private static async ValueTask HandleAsync(
            Command command,
            AppDbContext context,
            ILogger logger
            )
        {
            await Task.CompletedTask;
            var (villageId, job, top) = command;

            // De-duplicate identical jobs (same target Type/Location/Level regardless of Source).
            var candidates = context.Jobs
                .Where(x => x.VillageId == villageId.Value)
                .Where(x => x.Type == job.Type)
                .OrderBy(x => x.Position)
                .Select(x => new JobCandidate(x.Id, x.Position, x.Content))
                .AsEnumerable();

            var match = FindMatchingJob(job, candidates);

            if (match is JobCandidate existing)
            {
                // If asked to push to top, move the existing job to top.
                var overwrite = ShouldOverwriteContent(job, existing.Content);

                if (top)
                {
                    context.Jobs
                        .Where(x => x.VillageId == villageId.Value)
                        .Where(x => x.Id != existing.Id)
                        .ExecuteUpdate(x => x.SetProperty(y => y.Position, y => y.Position + 1));

                    context.Jobs
                        .Where(x => x.Id == existing.Id)
                        .ExecuteUpdate(x => x.SetProperty(y => y.Position, y => 0));

                    if (overwrite)
                    {
                        context.Jobs
                            .Where(x => x.Id == existing.Id)
                            .ExecuteUpdate(x => x.SetProperty(y => y.Content, y => job.Content));
                    }

                    logger.Information("Moved existing identical job to top (JobId: {JobId}, Source: {Source})", existing.Id, GetSource(job));
                }
                else
                {
                    if (overwrite)
                    {
                        context.Jobs
                            .Where(x => x.Id == existing.Id)
                            .ExecuteUpdate(x => x.SetProperty(y => y.Content, y => job.Content));
                        logger.Information("Updated existing job content and skipped duplicate (JobId: {JobId}, Source: {Source})", existing.Id, GetSource(job));
                    }
                    else
                    {
                        logger.Information("Skipped adding duplicate job (existing JobId: {JobId}, Source: {Source})", existing.Id, GetSource(job));
                    }
                }
                return;
            }

            if (top)
            {
                context.Jobs
                   .Where(x => x.VillageId == villageId.Value)
                   .ExecuteUpdate(x =>
                       x.SetProperty(x => x.Position, x => x.Position + 1));
                job.Position = 0;
                logger.Information("Added job to top: {Job} (Source: {Source})", job, GetSource(job));
            }
            else
            {
                var count = context.Jobs
                    .Where(x => x.VillageId == villageId.Value)
                    .Count();

                job.Position = count;
                logger.Information("Added job to bottom: {Job} (Source: {Source})", job, GetSource(job));
            }

            context.Add(job.ToEntity(villageId));
            context.SaveChanges();
        }

        private static JobCandidate? FindMatchingJob(JobDto incomingJob, IEnumerable<JobCandidate> candidates)
        {
            foreach (var candidate in candidates)
            {
                if (JobsMatch(incomingJob, candidate.Content))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static bool JobsMatch(JobDto incomingJob, string existingContent)
        {
            switch (incomingJob.Type)
            {
                case JobTypeEnums.NormalBuild:
                    {
                        var incomingPlan = JsonSerializer.Deserialize<NormalBuildPlan>(incomingJob.Content);
                        var existingPlan = JsonSerializer.Deserialize<NormalBuildPlan>(existingContent);
                        if (incomingPlan is null || existingPlan is null) return incomingJob.Content == existingContent;
                        return incomingPlan.Type == existingPlan.Type
                            && incomingPlan.Location == existingPlan.Location
                            && incomingPlan.Level == existingPlan.Level;
                    }
                case JobTypeEnums.ResourceBuild:
                    {
                        var incomingPlan = JsonSerializer.Deserialize<ResourceBuildPlan>(incomingJob.Content);
                        var existingPlan = JsonSerializer.Deserialize<ResourceBuildPlan>(existingContent);
                        if (incomingPlan is null || existingPlan is null) return incomingJob.Content == existingContent;
                        return incomingPlan.Plan == existingPlan.Plan
                            && incomingPlan.Level == existingPlan.Level;
                    }
                default:
                    return incomingJob.Content == existingContent;
            }
        }

        private static bool ShouldOverwriteContent(JobDto incomingJob, string existingContent)
        {
            if (incomingJob.Type != JobTypeEnums.NormalBuild) return false;

            var incomingPlan = JsonSerializer.Deserialize<NormalBuildPlan>(incomingJob.Content);
            var existingPlan = JsonSerializer.Deserialize<NormalBuildPlan>(existingContent);
            if (incomingPlan is null || existingPlan is null) return false;

            // Prefer to keep Source metadata if the new job supplies it.
            return !string.IsNullOrWhiteSpace(incomingPlan.Source) && string.IsNullOrWhiteSpace(existingPlan.Source);
        }

        private readonly record struct JobCandidate(int Id, int Position, string Content);

        private static string GetSource(JobDto job)
        {
            try
            {
                return job.Type switch
                {
                    JobTypeEnums.NormalBuild => JsonSerializer.Deserialize<NormalBuildPlan>(job.Content)?.Source ?? string.Empty,
                    JobTypeEnums.ResourceBuild => "resource-plan",
                    _ => string.Empty,
                };
            }
            catch
            {
                return string.Empty;
            }
        }

        public static JobDto ToJob(this NormalBuildPlan plan)
        {
            return new JobDto()
            {
                Position = 0,
                Type = JobTypeEnums.NormalBuild,
                Content = JsonSerializer.Serialize(plan),
            };
        }

        public static JobDto ToJob(this ResourceBuildPlan plan)
        {
            return new JobDto()
            {
                Position = 0,
                Type = JobTypeEnums.ResourceBuild,
                Content = JsonSerializer.Serialize(plan),
            };
        }
    }
}
