using System.Threading;
using System.Threading.Tasks;
using MainCore.Entities;
using MainCore.Models.Planning;

namespace MainCore.Services.Planning
{
    public interface IUpgradePlanner
    {
        Task<UpgradeRecommendation> GenerateAsync(AccountId accountId, VillageId villageId, CancellationToken cancellationToken = default);
    }
}
