using System.Text.Json;
using MainCore.Entities;
using MainCore.Enums;
using MainCore.Infrasturecture.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MainCore.Commands.Features.UpgradeBuilding
{
    [Handler]
    public static partial class GetBuildPlanCommand
    {
        public sealed record Command(AccountId AccountId, VillageId VillageId) : IAccountVillageCommand;

        private static async ValueTask<Result<NormalBuildPlan>> HandleAsync(
            Command command,
            GetJobCommand.Handler getJobQuery,
            ToDorfCommand.Handler toDorfCommand,
            UpdateBuildingCommand.Handler updateBuildingCommand,
            GetLayoutBuildingsCommand.Handler getLayoutBuildingsQuery,
            DeleteJobByIdCommand.Handler deleteJobByIdCommand,
            AddJobCommand.Handler addJobCommand,
            ValidateJobCompleteCommand.Handler validateJobCompleteCommand,
            AppDbContext context,
            ILogger logger,
            IRxQueue rxQueue,
            CancellationToken cancellationToken
        )
        {
            var (accountId, villageId) = command;

            while (true)
            {
                if (cancellationToken.IsCancellationRequested) return Cancel.Error;

                var result = await toDorfCommand.HandleAsync(new(2), cancellationToken);
                if (result.IsFailed) return result;

                result = await updateBuildingCommand.HandleAsync(new(villageId), cancellationToken);
                if (result.IsFailed) return result;

                var (_, isFailed, job, errors) = await getJobQuery.HandleAsync(new(accountId, villageId), cancellationToken);
                if (isFailed) return Result.Fail(errors);

                if (job.Type == JobTypeEnums.ResourceBuild)
                {
                    logger.Information("{Content}", job);

                    var layoutBuildings = await getLayoutBuildingsQuery.HandleAsync(new(villageId, true));
                    var resourceBuildPlan = JsonSerializer.Deserialize<ResourceBuildPlan>(job.Content)!;
                    var storage = await context.Storages
                        .AsNoTracking()
                        .FirstOrDefaultAsync(x => x.VillageId == villageId.Value, cancellationToken);

                    var heroReserve = await GetHeroResourceTotalsAsync(context, accountId, cancellationToken);

                    var normalBuildPlan = GetNormalBuildPlan(resourceBuildPlan, layoutBuildings, storage, heroReserve);
                    if (normalBuildPlan is null)
                    {
                        await deleteJobByIdCommand.HandleAsync(new(job.Id), cancellationToken);
                    }
                    else
                    {
                        await addJobCommand.HandleAsync(new(villageId, normalBuildPlan.ToJob(), true));
                    }
                    rxQueue.Enqueue(new JobsModified(villageId));
                    continue;
                }

                var plan = JsonSerializer.Deserialize<NormalBuildPlan>(job.Content)!;

                var dorf = plan.Location < 19 ? 1 : 2;
                result = await toDorfCommand.HandleAsync(new(dorf), cancellationToken);
                if (result.IsFailed) return result;

                result = await updateBuildingCommand.HandleAsync(new(villageId), cancellationToken);
                if (result.IsFailed) return result;

                if (await validateJobCompleteCommand.HandleAsync(new ValidateJobCompleteCommand.Command(villageId, job), cancellationToken))
                {
                    await deleteJobByIdCommand.HandleAsync(new(job.Id), cancellationToken);
                    rxQueue.Enqueue(new JobsModified(villageId));
                    continue;
                }

                return plan;
            }
        }

        private static NormalBuildPlan? GetNormalBuildPlan(
            ResourceBuildPlan plan,
            List<BuildingItem> layoutBuildings,
            Storage? storage,
            long[] heroReserve
        )
        {
            List<BuildingItem> resourceFields;

            if (plan.Plan == ResourcePlanEnums.ExcludeCrop)
            {
                resourceFields = layoutBuildings
                    .Where(x => x.Type == BuildingEnums.Woodcutter || x.Type == BuildingEnums.ClayPit || x.Type == BuildingEnums.IronMine)
                    .Where(x => x.Level < plan.Level)
                    .ToList();
            }
            else if (plan.Plan == ResourcePlanEnums.OnlyCrop)
            {
                resourceFields = layoutBuildings
                    .Where(x => x.Type == BuildingEnums.Cropland)
                    .Where(x => x.Level < plan.Level)
                    .ToList();
            }
            else
            {
                resourceFields = layoutBuildings
                    .Where(x => x.Type.IsResourceField())
                    .Where(x => x.Level < plan.Level)
                    .ToList();
            }

            if (resourceFields.Count == 0) return null;

            if (plan.Plan == ResourcePlanEnums.AllResources)
            {
                var groupedFields = resourceFields
                    .GroupBy(x => x.Type)
                    .ToDictionary(x => x.Key, x => x.OrderBy(f => f.Level).ThenBy(f => f.Location).ToList());

                foreach (var resourceType in GetResourcePriority(storage, heroReserve))
                {
                    if (!groupedFields.TryGetValue(resourceType, out var candidates) || candidates.Count == 0) continue;

                    var selected = candidates.First();
                    return new NormalBuildPlan()
                    {
                        Type = selected.Type,
                        Level = selected.Level + 1,
                        Location = selected.Location,
                    };
                }
            }

            var minLevel = resourceFields
                .Select(x => x.Level)
                .Min();

            var chosenOne = resourceFields
                .Where(x => x.Level == minLevel)
                .OrderBy(x => x.Location)
                .FirstOrDefault();

            if (chosenOne is null) return null;

            var normalBuildPlan = new NormalBuildPlan()
            {
                Type = chosenOne.Type,
                Level = chosenOne.Level + 1,
                Location = chosenOne.Location,
            };
            return normalBuildPlan;
        }

        private static async Task<long[]> GetHeroResourceTotalsAsync(AppDbContext context, AccountId accountId, CancellationToken cancellationToken)
        {
            var totals = new long[4];
            var heroItems = await context.HeroItems
                .AsNoTracking()
                .Where(x => x.AccountId == accountId.Value)
                .Where(x => x.Type == HeroItemEnums.Wood || x.Type == HeroItemEnums.Clay || x.Type == HeroItemEnums.Iron || x.Type == HeroItemEnums.Crop)
                .ToListAsync(cancellationToken);

            foreach (var item in heroItems)
            {
                switch (item.Type)
                {
                    case HeroItemEnums.Wood:
                        totals[0] += item.Amount;
                        break;
                    case HeroItemEnums.Clay:
                        totals[1] += item.Amount;
                        break;
                    case HeroItemEnums.Iron:
                        totals[2] += item.Amount;
                        break;
                    case HeroItemEnums.Crop:
                        totals[3] += item.Amount;
                        break;
                }
            }

            return totals;
        }

        private static IEnumerable<BuildingEnums> GetResourcePriority(Storage? storage, long[] heroReserve)
        {
            var priorities = new (BuildingEnums Type, long Total)[]
            {
                (BuildingEnums.Woodcutter, (storage?.Wood ?? 0) + GetHeroReserve(heroReserve, 0)),
                (BuildingEnums.ClayPit, (storage?.Clay ?? 0) + GetHeroReserve(heroReserve, 1)),
                (BuildingEnums.IronMine, (storage?.Iron ?? 0) + GetHeroReserve(heroReserve, 2)),
                (BuildingEnums.Cropland, (storage?.Crop ?? 0) + GetHeroReserve(heroReserve, 3)),
            };

            return priorities
                .OrderBy(x => x.Total)
                .ThenBy(x => GetTypeOrder(x.Type))
                .Select(x => x.Type);
        }

        private static long GetHeroReserve(long[] heroReserve, int index)
        {
            if (heroReserve.Length > index)
            {
                return heroReserve[index];
            }

            return 0;
        }

        private static int GetTypeOrder(BuildingEnums type) => type switch
        {
            BuildingEnums.Woodcutter => 0,
            BuildingEnums.ClayPit => 1,
            BuildingEnums.IronMine => 2,
            BuildingEnums.Cropland => 3,
            _ => 4,
        };

        private static bool IsJobComplete(JobDto job, List<BuildingDto> buildings, List<QueueBuilding> queueBuildings)
        {
            if (job.Type == JobTypeEnums.ResourceBuild) return false;

            var plan = JsonSerializer.Deserialize<NormalBuildPlan>(job.Content)!;

            var queueBuilding = queueBuildings
                .Where(x => x.Location == plan.Location)
                .OrderByDescending(x => x.Level)
                .Select(x => x.Level)
                .FirstOrDefault();

            if (queueBuilding >= plan.Level) return true;

            var villageBuilding = buildings
                .Where(x => x.Location == plan.Location)
                .Select(x => x.Level)
                .FirstOrDefault();
            if (villageBuilding >= plan.Level) return true;

            return false;
        }
    }
}