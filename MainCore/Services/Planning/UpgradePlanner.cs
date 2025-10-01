using System.Globalization;
using MainCore.Entities;
using MainCore.Infrasturecture.Persistence;
using MainCore.Enums;
using MainCore.Models;
using MainCore.Models.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MainCore.Services.Planning
{
    [RegisterScoped<IUpgradePlanner, UpgradePlanner>]
    public sealed class UpgradePlanner : IUpgradePlanner
    {
        private const int MaxPlans = 3;

        private readonly AppDbContext _context;
        private readonly ILogger<UpgradePlanner> _logger;

        public UpgradePlanner(AppDbContext context, ILogger<UpgradePlanner> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<UpgradeRecommendation> GenerateAsync(AccountId accountId, VillageId villageId, CancellationToken cancellationToken = default)
        {
            var buildings = await _context.Buildings
                .AsNoTracking()
                .Where(x => x.VillageId == villageId.Value)
                .ToListAsync(cancellationToken);

            var queueBuildings = await _context.QueueBuildings
                .AsNoTracking()
                .Where(x => x.VillageId == villageId.Value)
                .ToListAsync(cancellationToken);

            var storage = await _context.Storages
                .AsNoTracking()
                .Where(x => x.VillageId == villageId.Value)
                .FirstOrDefaultAsync(cancellationToken);

            var plans = new List<NormalBuildPlan>();
            var insights = new List<string>();

            AddFreeCropPlan(buildings, queueBuildings, storage, plans, insights);
            AddResourceFieldPlan(buildings, queueBuildings, plans, insights);
            AddCapacityPlans(buildings, queueBuildings, storage, plans, insights);

            if (plans.Count == 0)
            {
                TryRecommendMainBuilding(buildings, queueBuildings, plans, insights);
            }

            var recommendation = new UpgradeRecommendation(plans, insights);

            if (recommendation.HasPlans)
            {
                _logger.LogInformation(
                    "Upgrade planner prepared {PlanCount} plan(s) for account {AccountId} village {VillageId}: {Summary}",
                    recommendation.Plans.Count,
                    accountId.Value,
                    villageId.Value,
                    recommendation.Summary);
            }
            else
            {
                _logger.LogDebug(
                    "Upgrade planner found no actionable plan for account {AccountId} village {VillageId}.",
                    accountId.Value,
                    villageId.Value);
            }

            return recommendation;
        }

        private static void AddResourceFieldPlan(
            List<Building> buildings,
            List<QueueBuilding> queueBuildings,
            List<NormalBuildPlan> plans,
            List<string> insights)
        {
            if (plans.Count >= MaxPlans) return;

            var resourceFields = buildings
                .Where(b => b.Type.IsResourceField())
                .Where(b => !b.IsUnderConstruction)
                .ToList();

            if (resourceFields.Count == 0) return;

            var minLevel = resourceFields.Min(b => b.Level);
            var maxLevel = resourceFields.Max(b => b.Level);

            var candidate = resourceFields
                .Where(b => b.Level == minLevel)
                .Where(b => !IsQueued(queueBuildings, b.Type, b.Location, b.Level + 1))
                .OrderBy(b => b.Location)
                .FirstOrDefault();

            if (candidate is null) return;

            var nextLevel = Math.Min(candidate.Level + 1, candidate.Type.GetMaxLevel());
            if (nextLevel <= candidate.Level) return;

            var plan = new NormalBuildPlan
            {
                Type = candidate.Type,
                Level = nextLevel,
                Location = candidate.Location,
            };

            var reason = maxLevel - minLevel > 1
                ? $"Balancing resource fields by raising {plan.Type} at slot {plan.Location} to level {plan.Level}."
                : $"Incremental resource scaling via {plan.Type} slot {plan.Location} to level {plan.Level}.";

            TryAddPlan(plans, insights, queueBuildings, plan, reason);
        }

        private static void AddFreeCropPlan(
            List<Building> buildings,
            List<QueueBuilding> queueBuildings,
            Storage? storage,
            List<NormalBuildPlan> plans,
            List<string> insights)
        {
            if (plans.Count >= MaxPlans) return;
            if (storage is null) return;
            if (storage.FreeCrop > 5) return;

            var candidate = buildings
                .Where(b => b.Type == BuildingEnums.Cropland)
                .Where(b => !b.IsUnderConstruction)
                .Where(b => !IsQueued(queueBuildings, b.Type, b.Location, b.Level + 1))
                .OrderBy(b => b.Level)
                .ThenBy(b => b.Location)
                .FirstOrDefault();

            if (candidate is null) return;
            var nextLevel = Math.Min(candidate.Level + 1, candidate.Type.GetMaxLevel());
            if (nextLevel <= candidate.Level) return;

            var plan = new NormalBuildPlan
            {
                Type = candidate.Type,
                Level = nextLevel,
                Location = candidate.Location,
            };

            var reason = $"Free crop at {storage.FreeCrop} - prioritising cropland slot {plan.Location} to level {plan.Level}.";
            TryAddPlan(plans, insights, queueBuildings, plan, reason);
        }

        private static void AddCapacityPlans(
            List<Building> buildings,
            List<QueueBuilding> queueBuildings,
            Storage? storage,
            List<NormalBuildPlan> plans,
            List<string> insights)
        {
            if (storage is null) return;

            if (storage.Warehouse > 0)
            {
                var warehousePressure = new[] { storage.Wood, storage.Clay, storage.Iron }.Max() / (double)storage.Warehouse;
                if (warehousePressure >= 0.9)
                {
                    var plan = CreateInfrastructurePlan(buildings, queueBuildings, BuildingEnums.Warehouse);
                    if (plan is not null)
                    {
                        var formattedPressure = warehousePressure.ToString("P0", CultureInfo.InvariantCulture);
                        TryAddPlan(plans, insights, queueBuildings, plan, $"Warehouse utilisation at {formattedPressure}; uplifting slot {plan.Location} to level {plan.Level}.");
                    }
                }
            }

            if (storage.Granary > 0)
            {
                var granaryPressure = storage.Crop / (double)storage.Granary;
                if (granaryPressure >= 0.9)
                {
                    var plan = CreateInfrastructurePlan(buildings, queueBuildings, BuildingEnums.Granary);
                    if (plan is not null)
                    {
                        var formattedPressure = granaryPressure.ToString("P0", CultureInfo.InvariantCulture);
                        TryAddPlan(plans, insights, queueBuildings, plan, $"Granary utilisation at {formattedPressure}; upgrading slot {plan.Location} to level {plan.Level}.");
                    }
                }
            }
        }

        private static void TryRecommendMainBuilding(
            List<Building> buildings,
            List<QueueBuilding> queueBuildings,
            List<NormalBuildPlan> plans,
            List<string> insights)
        {
            if (plans.Count >= MaxPlans) return;

            var candidate = buildings
                .Where(b => b.Type == BuildingEnums.MainBuilding)
                .Where(b => !b.IsUnderConstruction)
                .OrderByDescending(b => b.Level)
                .FirstOrDefault();

            if (candidate is null) return;

            var nextLevel = Math.Min(candidate.Level + 1, candidate.Type.GetMaxLevel());
            if (nextLevel <= candidate.Level) return;
            if (IsQueued(queueBuildings, candidate.Type, candidate.Location, nextLevel)) return;

            var plan = new NormalBuildPlan
            {
                Type = candidate.Type,
                Level = nextLevel,
                Location = candidate.Location,
            };

            TryAddPlan(plans, insights, queueBuildings, plan, $"No pressing needs detected - improving main building slot {plan.Location} to level {plan.Level} for faster construction.");
        }

        private static NormalBuildPlan? CreateInfrastructurePlan(
            List<Building> buildings,
            List<QueueBuilding> queueBuildings,
            BuildingEnums type)
        {
            var candidate = buildings
                .Where(b => b.Type == type)
                .Where(b => !b.IsUnderConstruction)
                .OrderBy(b => b.Level)
                .FirstOrDefault();

            if (candidate is null) return null;

            var nextLevel = Math.Min(candidate.Level + 1, type.GetMaxLevel());
            if (nextLevel <= candidate.Level) return null;
            if (IsQueued(queueBuildings, type, candidate.Location, nextLevel)) return null;

            return new NormalBuildPlan
            {
                Type = type,
                Level = nextLevel,
                Location = candidate.Location,
            };
        }

        private static bool TryAddPlan(
            List<NormalBuildPlan> plans,
            List<string> insights,
            List<QueueBuilding> queueBuildings,
            NormalBuildPlan plan,
            string insight)
        {
            if (plans.Count >= MaxPlans) return false;
            if (IsQueued(queueBuildings, plan.Type, plan.Location, plan.Level)) return false;
            if (plans.Any(p => p.Type == plan.Type && p.Location == plan.Location && p.Level == plan.Level)) return false;

            plan.Source ??= NormalBuildPlan.RewardPlannerSource;

            plans.Add(plan);
            insights.Add(insight);
            return true;
        }

        private static bool IsQueued(
            IEnumerable<QueueBuilding> queueBuildings,
            BuildingEnums type,
            int location,
            int level)
        {
            return queueBuildings.Any(q => q.Location == location && q.Type == type && q.Level >= level);
        }
    }
}

