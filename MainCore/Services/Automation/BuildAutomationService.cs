using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MainCore.Commands.Features.UpgradeBuilding;
using MainCore.Commands.Misc;
using MainCore.Entities;
using MainCore.Enums;
using MainCore.Models;
using MainCore.Services.Planning;
using MainCore.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MainCore.Services.Automation
{
    [RegisterSingleton<IBuildAutomationService, BuildAutomationService>]
    public sealed class BuildAutomationService : IBuildAutomationService
    {
        private const int DefaultRewardPlanBuffer = 2;

        private readonly ICustomServiceScopeFactory _scopeFactory;
        private readonly ILogger<BuildAutomationService> _logger;
        private readonly ConcurrentDictionary<(AccountId AccountId, VillageId VillageId), SemaphoreSlim> _locks = new();

        public BuildAutomationService(ICustomServiceScopeFactory scopeFactory, ILogger<BuildAutomationService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task<bool> EnsureQueueAsync(AccountId accountId, VillageId villageId, CancellationToken cancellationToken = default)
        {
            var gate = _locks.GetOrAdd((accountId, villageId), _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var scope = _scopeFactory.CreateScope(accountId);
                var provider = scope.ServiceProvider;
                var context = provider.GetRequiredService<AppDbContext>();
                var getLayoutBuildingsQuery = provider.GetRequiredService<GetLayoutBuildingsCommand.Handler>();
                var addJobCommand = provider.GetRequiredService<AddJobCommand.Handler>();
                var upgradePlanner = provider.GetRequiredService<IUpgradePlanner>();

                var changed = false;

                if (context.BooleanByName(villageId, VillageSettingEnums.AutoQueueStorage))
                {
                    changed |= await AutoBuildStorageAsync(context, getLayoutBuildingsQuery, addJobCommand, accountId, villageId, cancellationToken).ConfigureAwait(false);
                }

                if (context.BooleanByName(villageId, VillageSettingEnums.AutoQueueRewardPlan))
                {
                    changed |= await ApplyRewardPlanAsync(context, getLayoutBuildingsQuery, addJobCommand, upgradePlanner, accountId, villageId, cancellationToken).ConfigureAwait(false);
                }

                return changed;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to ensure build queue for account {AccountId} village {VillageId}", accountId.Value, villageId.Value);
                return false;
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<bool> EnsurePrerequisitesAsync(AccountId accountId, VillageId villageId, NormalBuildPlan plan, CancellationToken cancellationToken = default)
        {
            using var scope = _scopeFactory.CreateScope(accountId);
            var provider = scope.ServiceProvider;
            var context = provider.GetRequiredService<AppDbContext>();
            var getLayoutBuildingsQuery = provider.GetRequiredService<GetLayoutBuildingsCommand.Handler>();
            var addJobCommand = provider.GetRequiredService<AddJobCommand.Handler>();

            return await EnsurePrerequisitesInternalAsync(context, getLayoutBuildingsQuery, addJobCommand, villageId, plan, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> AutoBuildStorageAsync(
            AppDbContext context,
            GetLayoutBuildingsCommand.Handler getLayoutBuildingsQuery,
            AddJobCommand.Handler addJobCommand,
            AccountId accountId,
            VillageId villageId,
            CancellationToken cancellationToken)
        {
            var layout = await getLayoutBuildingsQuery.HandleAsync(new(villageId), cancellationToken).ConfigureAwait(false);
            var storage = context.Storages
                .AsNoTracking()
                .FirstOrDefault(x => x.VillageId == villageId.Value);
            if (storage is null) return false;

            var queueBuildings = context.QueueBuildings
                .AsNoTracking()
                .Where(x => x.VillageId == villageId.Value)
                .ToList();
            var buildings = context.Buildings
                .AsNoTracking()
                .Where(x => x.VillageId == villageId.Value)
                .ToList();

            var serverSpeedSetting = context.ByName(accountId, AccountSettingEnums.ServerSpeed);
            var serverSpeed = serverSpeedSetting <= 0 ? 1 : serverSpeedSetting;
            var production = CalculateProduction(buildings, storage, serverSpeed);
            var hoursUntilNextCompletion = GetHoursUntilNextCompletion(queueBuildings);

            var added = false;

            foreach (var type in new[] { BuildingEnums.Granary, BuildingEnums.Warehouse })
            {
                var building = layout
                    .Where(x => x.Type == type)
                    .OrderBy(x => x.Level)
                    .ThenBy(x => x.Location)
                    .FirstOrDefault();
                if (building is null) continue;
                if (building.QueueLevel > building.Level || building.JobLevel > building.Level) continue;
                if (!ShouldQueueStorageUpgrade(type, storage, production, hoursUntilNextCompletion)) continue;

                var baseLevel = building.Level;
                var nextLevel = Math.Min(type.GetMaxLevel(), baseLevel + 1);
                if (nextLevel <= baseLevel) continue;

                var plan = new NormalBuildPlan
                {
                    Type = type,
                    Location = building.Location,
                    Level = nextLevel,
                };

                var prerequisitesAdded = await EnsurePrerequisitesInternalAsync(context, getLayoutBuildingsQuery, addJobCommand, villageId, plan, cancellationToken).ConfigureAwait(false);
                if (prerequisitesAdded)
                {
                    added = true;
                }

                await addJobCommand.HandleAsync(new(villageId, plan.ToJob(), true), cancellationToken).ConfigureAwait(false);
                layout = await getLayoutBuildingsQuery.HandleAsync(new(villageId), cancellationToken).ConfigureAwait(false);
                added = true;
            }

            return added;
        }

        private static double? GetHoursUntilNextCompletion(IEnumerable<QueueBuilding> queueBuildings)
        {
            var now = DateTime.UtcNow;
            var nextCompletion = queueBuildings
                .Where(x => x.CompleteTime > now)
                .OrderBy(x => x.CompleteTime)
                .FirstOrDefault();

            if (nextCompletion is null) return null;

            var delta = nextCompletion.CompleteTime - now;
            return delta <= TimeSpan.Zero ? null : delta.TotalHours;
        }

        private static bool ShouldQueueStorageUpgrade(
            BuildingEnums type,
            Storage storage,
            ResourceProductionSnapshot production,
            double? hoursUntilNextCompletion)
        {
            var capacity = type == BuildingEnums.Warehouse ? storage.Warehouse : storage.Granary;
            if (capacity <= 0) return false;

            var threshold = capacity * 0.9d;

            if (type == BuildingEnums.Warehouse)
            {
                var current = new[] { storage.Wood, storage.Clay, storage.Iron };
                if (current.Max() >= threshold) return true;

                if (hoursUntilNextCompletion.HasValue && hoursUntilNextCompletion.Value > 0d)
                {
                    var hours = hoursUntilNextCompletion.Value;
                    var predicted = new[]
                    {
                        current[0] + production.WoodPerHour * hours,
                        current[1] + production.ClayPerHour * hours,
                        current[2] + production.IronPerHour * hours,
                    };

                    if (predicted.Max() >= capacity) return true;
                }

                return false;
            }

            var crop = storage.Crop;
            if (crop >= threshold) return true;

            if (hoursUntilNextCompletion.HasValue && hoursUntilNextCompletion.Value > 0d)
            {
                var perHour = Math.Max(production.NetCropPerHour, 0d);
                var predicted = crop + perHour * hoursUntilNextCompletion.Value;
                if (predicted >= capacity) return true;
            }

            return false;
        }

        private static ResourceProductionSnapshot CalculateProduction(
            IReadOnlyCollection<Building> buildings,
            Storage storage,
            int serverSpeed)
        {
            var woodFields = GetResourceFieldProduction(buildings, BuildingEnums.Woodcutter, serverSpeed);
            var wood = ApplyBonus(woodFields, GetBuildingLevel(buildings, BuildingEnums.Sawmill)) + (BaseProductionPerHour * serverSpeed);

            var clayFields = GetResourceFieldProduction(buildings, BuildingEnums.ClayPit, serverSpeed);
            var clay = ApplyBonus(clayFields, GetBuildingLevel(buildings, BuildingEnums.Brickyard)) + (BaseProductionPerHour * serverSpeed);

            var ironFields = GetResourceFieldProduction(buildings, BuildingEnums.IronMine, serverSpeed);
            var iron = ApplyBonus(ironFields, GetBuildingLevel(buildings, BuildingEnums.IronFoundry)) + (BaseProductionPerHour * serverSpeed);

            var cropFields = GetResourceFieldProduction(buildings, BuildingEnums.Cropland, serverSpeed);
            cropFields = ApplyBonus(cropFields, GetBuildingLevel(buildings, BuildingEnums.GrainMill));
            cropFields = ApplyBonus(cropFields, GetBuildingLevel(buildings, BuildingEnums.Bakery));

            var netCrop = storage.FreeCrop;
            return new ResourceProductionSnapshot(wood, clay, iron, netCrop);
        }

        private static double GetResourceFieldProduction(IEnumerable<Building> buildings, BuildingEnums type, int serverSpeed)
        {
            return buildings
                .Where(x => x.Type == type)
                .Select(x => ResourceProductionPerLevel[Math.Clamp(x.Level, 0, ResourceProductionPerLevel.Length - 1)] * serverSpeed)
                .Sum();
        }

        private static double ApplyBonus(double baseProduction, int level, double perLevelBonus = 0.05, int maxLevel = 5)
        {
            if (level <= 0) return baseProduction;
            var effectiveLevel = Math.Min(level, maxLevel);
            return baseProduction * (1 + perLevelBonus * effectiveLevel);
        }

        private static int GetBuildingLevel(IEnumerable<Building> buildings, BuildingEnums type)
        {
            return buildings.FirstOrDefault(x => x.Type == type)?.Level ?? 0;
        }

        private sealed record ResourceProductionSnapshot(double WoodPerHour, double ClayPerHour, double IronPerHour, double NetCropPerHour);

        private static readonly double[] ResourceProductionPerLevel = { 0, 2, 5, 9, 15, 22, 33, 50, 70, 100, 145, 200, 280, 375, 495, 635, 800, 1000, 1300, 1600, 2000, 2450, 3050, 3750 };

        private const double BaseProductionPerHour = 2d;
        private async Task<bool> ApplyRewardPlanAsync(
            AppDbContext context,
            GetLayoutBuildingsCommand.Handler getLayoutBuildingsQuery,
            AddJobCommand.Handler addJobCommand,
            IUpgradePlanner upgradePlanner,
            AccountId accountId,
            VillageId villageId,
            CancellationToken cancellationToken)
        {
            var recommendation = await upgradePlanner.GenerateAsync(accountId, villageId, cancellationToken).ConfigureAwait(false);
            if (!recommendation.HasPlans) return false;

            var buildings = await getLayoutBuildingsQuery.HandleAsync(new(villageId), cancellationToken).ConfigureAwait(false);

            var bufferSetting = context.ByName(villageId, VillageSettingEnums.AutoQueueRewardPlanMinQueue);
            var rewardPlanBuffer = bufferSetting < 0 ? DefaultRewardPlanBuffer : Math.Clamp(bufferSetting, 0, 5);

            var jobPlans = context.Jobs
                .Where(x => x.VillageId == villageId.Value)
                .Where(x => x.Type == JobTypeEnums.NormalBuild)
                .OrderBy(x => x.Position)
                .AsEnumerable()
                .Select(job =>
                {
                    var plan = JsonSerializer.Deserialize<NormalBuildPlan>(job.Content);
                    return plan is null ? null : new JobPlan(job.Id, plan);
                })
                .Where(entry => entry is not null)
                .Select(entry => entry!)
                .ToList();

            var changed = false;
            var staleRewardJobIds = new List<int>();

            foreach (var entry in jobPlans.Where(x => x.Plan.Source == NormalBuildPlan.RewardPlannerSource || x.Plan.Source == NormalBuildPlan.RewardPlannerPrerequisiteSource).ToList())
            {
                if (!NeedsUpgrade(entry.Plan, buildings))
                {
                    staleRewardJobIds.Add(entry.JobId);
                    jobPlans.Remove(entry);
                    changed = true;
                }
            }

            if (staleRewardJobIds.Count > 0)
            {
                context.Jobs
                    .Where(x => staleRewardJobIds.Contains(x.Id))
                    .ExecuteDelete();

                buildings = await getLayoutBuildingsQuery.HandleAsync(new(villageId), cancellationToken).ConfigureAwait(false);
            }

            var rewardPlansQueued = jobPlans.Count(x => x.Plan.Source == NormalBuildPlan.RewardPlannerSource);
            if (rewardPlanBuffer == 0 || rewardPlansQueued >= rewardPlanBuffer) return changed;

            var tribeValue = context.AccountsSetting
                .Where(x => x.AccountId == accountId.Value)
                .Where(x => x.Setting == AccountSettingEnums.Tribe)
                .Select(x => x.Value)
                .FirstOrDefault();
            var tribe = (TribeEnums)tribeValue;

            var candidatePlans = SelectRewardPlans(recommendation.Plans, tribe).ToList();

            async Task<bool> TryQueuePlanAsync(NormalBuildPlan candidate, bool force = false)
            {
                if (!force && rewardPlansQueued >= rewardPlanBuffer) return false;
                if (jobPlans.Any(x => PlansEqual(x.Plan, candidate))) return false;
                if (!NeedsUpgrade(candidate, buildings)) return false;

                var planToQueue = new NormalBuildPlan
                {
                    Type = candidate.Type,
                    Location = candidate.Location,
                    Level = candidate.Level,
                    Source = NormalBuildPlan.RewardPlannerSource,
                };

                var prerequisitesAdded = await EnsurePrerequisitesInternalAsync(context, getLayoutBuildingsQuery, addJobCommand, villageId, planToQueue, cancellationToken, NormalBuildPlan.RewardPlannerPrerequisiteSource).ConfigureAwait(false);
                await addJobCommand.HandleAsync(new(villageId, planToQueue.ToJob(), true), cancellationToken).ConfigureAwait(false);

                buildings = await getLayoutBuildingsQuery.HandleAsync(new(villageId), cancellationToken).ConfigureAwait(false);

                jobPlans.Add(new JobPlan(-1, planToQueue));
                rewardPlansQueued++;
                if (prerequisitesAdded)
                {
                    changed = true;
                }
                changed = true;
                return true;
            }

            if (tribe == TribeEnums.Romans)
            {
                var hasResourceReward = jobPlans.Any(x => x.Plan.Source == NormalBuildPlan.RewardPlannerSource && x.Plan.Type.IsResourceField());
                if (!hasResourceReward)
                {
                    var resourceCandidate = candidatePlans.FirstOrDefault(x => x.Type.IsResourceField());
                    if (resourceCandidate is not null && await TryQueuePlanAsync(resourceCandidate, force: true).ConfigureAwait(false))
                    {
                        candidatePlans.Remove(resourceCandidate);
                    }
                }

                var hasInfrastructureReward = jobPlans.Any(x => x.Plan.Source == NormalBuildPlan.RewardPlannerSource && !x.Plan.Type.IsResourceField());
                if (!hasInfrastructureReward)
                {
                    var infrastructureCandidate = candidatePlans.FirstOrDefault(x => !x.Type.IsResourceField());
                    if (infrastructureCandidate is not null && await TryQueuePlanAsync(infrastructureCandidate, force: true).ConfigureAwait(false))
                    {
                        candidatePlans.Remove(infrastructureCandidate);
                    }
                }
            }

            foreach (var candidate in candidatePlans)
            {
                if (await TryQueuePlanAsync(candidate).ConfigureAwait(false))
                {
                    if (rewardPlansQueued >= rewardPlanBuffer) break;
                }
            }

            return changed;
        }

        private static async Task<bool> EnsurePrerequisitesInternalAsync(
            AppDbContext context,
            GetLayoutBuildingsCommand.Handler getLayoutBuildingsQuery,
            AddJobCommand.Handler addJobCommand,
            VillageId villageId,
            NormalBuildPlan targetPlan,
            CancellationToken cancellationToken,
            string? source = null)
        {
            if (!context.BooleanByName(villageId, VillageSettingEnums.AutoBuildPrerequisites))
            {
                return false;
            }

            var prerequisites = targetPlan.Type.GetPrerequisiteBuildings();
            if (prerequisites.Count == 0) return false;

            var buildings = await getLayoutBuildingsQuery.HandleAsync(new(villageId), cancellationToken).ConfigureAwait(false);
            var added = false;

            foreach (var prerequisite in prerequisites)
            {
                var building = buildings.FirstOrDefault(x => x.Type == prerequisite.Type);
                if (building is null) continue;

                var currentLevel = Math.Max(building.Level, Math.Max(building.QueueLevel, building.JobLevel));
                if (currentLevel >= prerequisite.Level) continue;

                for (var level = currentLevel + 1; level <= prerequisite.Level; level++)
                {
                    var plan = new NormalBuildPlan
                    {
                        Type = prerequisite.Type,
                        Location = building.Location,
                        Level = level,
                        Source = source,
                    };

                    await addJobCommand.HandleAsync(new(villageId, plan.ToJob(), true), cancellationToken).ConfigureAwait(false);
                    added = true;
                }

                buildings = await getLayoutBuildingsQuery.HandleAsync(new(villageId), cancellationToken).ConfigureAwait(false);
            }

            return added;
        }

        private static IEnumerable<NormalBuildPlan> SelectRewardPlans(IReadOnlyList<NormalBuildPlan> plans, TribeEnums tribe)
        {
            if (plans.Count == 0) yield break;

            if (tribe == TribeEnums.Romans)
            {
                NormalBuildPlan? resourcePlan = null;
                NormalBuildPlan? infrastructurePlan = null;

                foreach (var plan in plans)
                {
                    if (resourcePlan is null && plan.Type.IsResourceField())
                    {
                        resourcePlan = plan;
                    }
                    else if (infrastructurePlan is null && !plan.Type.IsResourceField())
                    {
                        infrastructurePlan = plan;
                    }

                    if (resourcePlan is not null && infrastructurePlan is not null)
                    {
                        break;
                    }
                }

                if (resourcePlan is not null)
                {
                    yield return resourcePlan;
                }

                if (infrastructurePlan is not null)
                {
                    yield return infrastructurePlan;
                }

                foreach (var plan in plans)
                {
                    if (resourcePlan is not null && PlansEqual(plan, resourcePlan)) continue;
                    if (infrastructurePlan is not null && PlansEqual(plan, infrastructurePlan)) continue;

                    yield return plan;
                }

                yield break;
            }

            foreach (var plan in plans)
            {
                yield return plan;
            }
        }

        private static bool NeedsUpgrade(NormalBuildPlan plan, IEnumerable<BuildingItem> buildings)
        {
            var building = buildings.FirstOrDefault(x => x.Location == plan.Location);
            if (building is null)
            {
                return true;
            }

            return plan.Level > building.Level;
        }

        private static bool PlansEqual(NormalBuildPlan left, NormalBuildPlan right) =>
            left.Type == right.Type && left.Location == right.Location && left.Level == right.Level;

        private sealed record JobPlan(int JobId, NormalBuildPlan Plan);
    }
}




