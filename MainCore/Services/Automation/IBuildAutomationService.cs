using System.Threading;
using System.Threading.Tasks;
using MainCore.Entities;
using MainCore.Models;

namespace MainCore.Services.Automation
{
    public interface IBuildAutomationService
    {
        Task<bool> EnsureQueueAsync(AccountId accountId, VillageId villageId, CancellationToken cancellationToken = default);

        Task<bool> EnsurePrerequisitesAsync(AccountId accountId, VillageId villageId, NormalBuildPlan plan, CancellationToken cancellationToken = default);
    }
}
