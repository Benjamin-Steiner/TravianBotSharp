using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MainCore.Entities;
using MainCore.Infrasturecture.Persistence;
using MainCore.Notifications;
using MainCore.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MainCore.Services.Automation
{
    [RegisterSingleton<BuildAutomationSubscriber>]
    public sealed class BuildAutomationSubscriber : IDisposable
    {
        private readonly IBuildAutomationService _automationService;
        private readonly ICustomServiceScopeFactory _scopeFactory;
        private readonly ConcurrentDictionary<VillageId, SemaphoreSlim> _locks = new();
        private readonly IDisposable _jobsSubscription;
        private readonly IDisposable _buildingsSubscription;

        public BuildAutomationSubscriber(IBuildAutomationService automationService, ICustomServiceScopeFactory scopeFactory, IRxQueue rxQueue)
        {
            _automationService = automationService;
            _scopeFactory = scopeFactory;

            _jobsSubscription = rxQueue.GetObservable<JobsModified>()
                .Subscribe(notification => _ = HandleNotificationAsync(notification.VillageId));

            _buildingsSubscription = rxQueue.GetObservable<BuildingsModified>()
                .Subscribe(notification => _ = HandleNotificationAsync(notification.VillageId));
        }

        private async Task HandleNotificationAsync(VillageId villageId)
        {
            if (villageId == VillageId.Empty)
            {
                return;
            }

            var gate = _locks.GetOrAdd(villageId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var accountId = await ResolveAccountIdAsync(villageId).ConfigureAwait(false);
                if (accountId == AccountId.Empty)
                {
                    return;
                }

                await _automationService.EnsureQueueAsync(accountId, villageId).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<AccountId> ResolveAccountIdAsync(VillageId villageId)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var accountValue = await context.Villages
                .Where(x => x.Id == villageId.Value)
                .Select(x => x.AccountId)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            return accountValue == 0 ? AccountId.Empty : new AccountId(accountValue);
        }

        public void Dispose()
        {
            _jobsSubscription.Dispose();
            _buildingsSubscription.Dispose();
        }
    }
}
