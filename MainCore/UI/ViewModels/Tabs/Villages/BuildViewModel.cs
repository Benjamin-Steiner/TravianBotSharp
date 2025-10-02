using Humanizer;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using MainCore.Commands.Misc;
using MainCore.Enums;
using MainCore.Models;
using MainCore.Defaults.BuildQueues;
using MainCore.Infrasturecture.Persistence;
using MainCore.Services.Planning;
using MainCore.Services.Automation;
using MainCore.Tasks;
using MainCore.Commands.UI.Villages.BuildViewModel;
using MainCore.UI.Models.Input;
using MainCore.UI.Models.Output;
using MainCore.UI.ViewModels.Abstract;
using MainCore.UI.ViewModels.UserControls;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;
using System.IO;
using ReactiveUI;
using MainCore.Entities;
using System.Threading;
using System.Reactive.Concurrency;
using System.Globalization;
using MainCore.DTO;

namespace MainCore.UI.ViewModels.Tabs.Villages
{
    [RegisterSingleton<BuildViewModel>]
    public partial class BuildViewModel : VillageTabViewModelBase
    {
        private readonly IDialogService _dialogService;
        private readonly ITaskManager _taskManager;
        private readonly IValidator<NormalBuildInput> _normalBuildInputValidator;
        private readonly IValidator<ResourceBuildInput> _resourceBuildInputValidator;
        private readonly ICustomServiceScopeFactory _serviceScopeFactory;
        private readonly IBuildAutomationService _buildAutomationService;
        private bool _automationRunning;
        private readonly SemaphoreSlim _estimateSemaphore = new(1, 1);
        private string _nextJobEstimate = "Select a village to view job timing.";

        public string NextJobEstimate
        {
            get => _nextJobEstimate;
            private set => this.RaiseAndSetIfChanged(ref _nextJobEstimate, value);
        }
        private const int DefaultRewardPlanBuffer = 2;

        public NormalBuildInput NormalBuildInput { get; } = new();
        public ResourceBuildInput ResourceBuildInput { get; } = new();

        public ListBoxItemViewModel Buildings { get; } = new();
        public ListBoxItemViewModel Queue { get; } = new();
        public ListBoxItemViewModel Jobs { get; } = new();

        public ObservableCollection<BuildQueueTemplate> QueueTemplates { get; } = new(BuildQueueTemplateProvider.Templates);

        private BuildQueueTemplate? _selectedTemplate;
        private BuildForecastStatus _nextJobStatus = BuildForecastStatus.Info;
        public BuildForecastStatus NextJobStatus
        {
            get => _nextJobStatus;
            private set => this.RaiseAndSetIfChanged(ref _nextJobStatus, value);
        }

        public ObservableCollection<string> NextJobDetails { get; } = new();

        public BuildQueueTemplate? SelectedTemplate
        {
            get => _selectedTemplate;
            set => this.RaiseAndSetIfChanged(ref _selectedTemplate, value);
        }

        public BuildViewModel(IDialogService dialogService, IValidator<NormalBuildInput> normalBuildInputValidator, IValidator<ResourceBuildInput> resourceBuildInputValidator, ICustomServiceScopeFactory serviceScopeFactory, ITaskManager taskManager, IBuildAutomationService buildAutomationService, BuildAutomationSubscriber buildAutomationSubscriber, IRxQueue rxQueue)
        {
            _dialogService = dialogService;
            _normalBuildInputValidator = normalBuildInputValidator;
            _resourceBuildInputValidator = resourceBuildInputValidator;
            _serviceScopeFactory = serviceScopeFactory;
            _taskManager = taskManager;
            _buildAutomationService = buildAutomationService;
            _ = buildAutomationSubscriber;

            if (QueueTemplates.Count > 0)
            {
                SelectedTemplate = QueueTemplates[0];
            }

            this.WhenAnyValue(vm => vm.Buildings.SelectedItem)
                .ObserveOn(RxApp.TaskpoolScheduler)
                .WhereNotNull()
                .InvokeCommand(LoadBuildNormalCommand);

            LoadBuildingCommand.Subscribe(Buildings.Load);
            LoadJobCommand.Subscribe(items =>
            {
                Jobs.Load(items);
                QueueNextJobEstimateRefresh();
            });
            LoadQueueCommand.Subscribe(Queue.Load);

            LoadBuildNormalCommand.Subscribe(buildings =>
            {
                switch (buildings.Count)
                {
                    case 0:
                        NormalBuildInput.Clear();
                        break;

                    default:
                        NormalBuildInput.Set(buildings, -1);
                        break;
                }
            });

            rxQueue.RegisterCommand<BuildingsModified>(BuildingsModifiedCommand);
            rxQueue.RegisterCommand<JobsModified>(JobsModifiedCommand);
        }

        [ReactiveCommand]
        public async Task BuildingsModified(BuildingsModified notification)
        {
            if (!IsActive) return;
            if (notification.VillageId != VillageId) return;
            await LoadQueueCommand.Execute(notification.VillageId);
            await LoadBuildingCommand.Execute(notification.VillageId);

            using var scope = _serviceScopeFactory.CreateScope(AccountId);
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var task = new CompleteImmediatelyTask.Task(AccountId, notification.VillageId);
            if (task.CanStart(context) && !_taskManager.IsExist<CompleteImmediatelyTask.Task>(AccountId, notification.VillageId))
            {
                _taskManager.Add(task);
            }
        }

        [ReactiveCommand]
        public async Task JobsModified(JobsModified notification)
        {
            if (!IsActive) return;
            if (notification.VillageId != VillageId) return;

            var automationChanged = await RunAutomationAsync().ConfigureAwait(false);

            await LoadJobCommand.Execute(notification.VillageId);
            await LoadBuildingCommand.Execute(notification.VillageId);

            _taskManager.AddOrUpdate(new UpgradeBuildingTask.Task(AccountId, notification.VillageId));

            if (automationChanged)
            {
                await LoadQueueCommand.Execute(notification.VillageId);
            }

            QueueNextJobEstimateRefresh();
            ScheduleVillageRefresh();
        }

        protected override async Task Load(VillageId villageId)
        {
            await LoadJobCommand.Execute(villageId);
            await LoadBuildingCommand.Execute(villageId);
            await LoadQueueCommand.Execute(villageId);
        }

        [ReactiveCommand]
        private async Task<List<ListBoxItem>> LoadBuilding(VillageId villageId)
        {
            using var scope = _serviceScopeFactory.CreateScope(AccountId);
            var getLayoutBuildingsQuery = scope.ServiceProvider.GetRequiredService<GetLayoutBuildingsCommand.Handler>();
            var buildings = await getLayoutBuildingsQuery.HandleAsync(new(villageId));
            static ListBoxItem ToListBoxItem(BuildingItem building)
            {
                const string arrow = " -> ";
                var sb = new StringBuilder();
                sb.Append(building.CurrentLevel);
                if (building.QueueLevel != 0)
                {
                    var content = $"{arrow}({building.QueueLevel})";
                    sb.Append(content);
                }
                if (building.JobLevel != 0 && building.JobLevel > building.CurrentLevel)
                {
                    var content = $"{arrow}[{building.JobLevel}]";
                    sb.Append(content);
                }

                var item = new ListBoxItem()
                {
                    Id = building.Id.Value,
                    Content = $"[{building.Location}] {building.Type.Humanize()} | lvl {sb}",
                    Color = building.Type.GetColor(),
                };
                return item;
            }
            var items = buildings
                .Select(ToListBoxItem)
                .ToList();
            return items;
        }

        [ReactiveCommand]
        private List<ListBoxItem> LoadQueue(VillageId villageId)
        {
            using var scope = _serviceScopeFactory.CreateScope(AccountId);
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var items = context.QueueBuildings
                 .Where(x => x.VillageId == villageId.Value)
                 .AsEnumerable()
                 .Select(x => new ListBoxItem()
                 {
                     Id = x.Id,
                     Content = $"{x.Type.Humanize()} to level {x.Level} complete at {x.CompleteTime}",
                 })
                 .ToList();

            var tribe = (TribeEnums)context.VillagesSetting
                .Where(x => x.VillageId == villageId.Value)
                .Where(x => x.Setting == VillageSettingEnums.Tribe)
                .Select(x => x.Value)
                .FirstOrDefault();

            var count = 2;
            if (tribe == TribeEnums.Romans) count = 3;
            items.AddRange(Enumerable.Range(0, Math.Max(count - items.Count, 0)).Select((x) => new ListBoxItem() { Id = x - 5 }));
            return items;
        }

        [ReactiveCommand]
        private List<ListBoxItem> LoadJob(VillageId villageId)
        {
            using var scope = _serviceScopeFactory.CreateScope(AccountId);
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var items = context.Jobs
                .Where(x => x.VillageId == villageId.Value)
                .OrderBy(x => x.Position)
                .ToDto()
                .AsEnumerable()
                .Select(x => new ListBoxItem()
                {
                    Id = x.Id.Value,
                    Content = x.ToString(),
                })
                .ToList();

            return items;
        }

        private static readonly List<BuildingEnums> MultipleBuildings =
        [
            BuildingEnums.Warehouse,
            BuildingEnums.Granary,
            BuildingEnums.Trapper,
            BuildingEnums.Cranny,
        ];

        private static readonly List<BuildingEnums> IgnoreBuildings =
        [
            BuildingEnums.Site,
            BuildingEnums.Blacksmith,
            BuildingEnums.CityWall,
            BuildingEnums.EarthWall,
            BuildingEnums.Palisade,
            BuildingEnums.WW,
            BuildingEnums.StoneWall,
            BuildingEnums.MakeshiftWall,
            BuildingEnums.Unknown,
        ];

        private static readonly List<BuildingEnums> AvailableBuildings = Enum.GetValues(typeof(BuildingEnums))
            .Cast<BuildingEnums>()
            .Where(x => !IgnoreBuildings.Contains(x))
            .ToList();

        [ReactiveCommand]
        private async Task<List<BuildingEnums>> LoadBuildNormal(ListBoxItem item)
        {
            if (item is null) return [];

            using var scope = _serviceScopeFactory.CreateScope(AccountId);
            var getLayoutBuildingsQuery = scope.ServiceProvider.GetRequiredService<GetLayoutBuildingsCommand.Handler>();
            var buildingItems = await getLayoutBuildingsQuery.HandleAsync(new(VillageId));

            var type = buildingItems
                .Where(x => x.Id == new BuildingId(item.Id))
                .Select(x => x.Type)
                .FirstOrDefault();

            if (type != BuildingEnums.Site) return [type];

            var buildings = buildingItems
                .Select(x => x.Type)
                .Where(x => !MultipleBuildings.Contains(x))
                .Distinct()
                .ToList();

            return AvailableBuildings.Where(x => !buildings.Contains(x)).ToList();
        }

        [ReactiveCommand]
        private async Task BuildNormal()
        {
            if (!IsAccountPaused(AccountId))
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Warning", "Please pause account before modifing building queue"));
                return;
            }

            var result = await _normalBuildInputValidator.ValidateAsync(NormalBuildInput);
            if (!result.IsValid)
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Error", result.ToString()));
                return;
            }

            var location = Buildings.SelectedIndex + 1;

            using var scope = _serviceScopeFactory.CreateScope(AccountId);
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var plan = NormalBuildInput.ToPlan(location);

            if (await _buildAutomationService.EnsurePrerequisitesAsync(AccountId, VillageId, plan).ConfigureAwait(false))
            {
                await LoadJobCommand.Execute(VillageId);
            }

            var normalBuildCommand = scope.ServiceProvider.GetRequiredService<NormalBuildCommand.Handler>();
            var buildResult = await normalBuildCommand.HandleAsync(new(VillageId, plan));
            if (buildResult.IsFailed)
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Error", buildResult.ToString()));
                return;
            }

            await JobsModifiedCommand.Execute(new JobsModified(VillageId));
        }

        [ReactiveCommand]
        private async Task UpgradeOneLevel()
        {
            if (!IsAccountPaused(AccountId))
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Warning", "Please pause account before modifing building queue"));
                return;
            }
            var location = Buildings.SelectedIndex + 1;

            using var scope = _serviceScopeFactory.CreateScope(AccountId);
            var upgradeCommand = scope.ServiceProvider.GetRequiredService<UpgradeCommand.Handler>();
            await upgradeCommand.HandleAsync(new(VillageId, location, false));
            await JobsModifiedCommand.Execute(new JobsModified(VillageId));
        }

        [ReactiveCommand]
        private async Task UpgradeMaxLevel()
        {
            if (!IsAccountPaused(AccountId))
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Warning", "Please pause account before modifing building queue"));
                return;
            }
            var location = Buildings.SelectedIndex + 1;

            using var scope = _serviceScopeFactory.CreateScope(AccountId);
            var upgradeCommand = scope.ServiceProvider.GetRequiredService<UpgradeCommand.Handler>();
            await upgradeCommand.HandleAsync(new(VillageId, location, true));
            await JobsModifiedCommand.Execute(new JobsModified(VillageId));
        }

        [ReactiveCommand]
        private async Task BuildResource()
        {
            if (!IsAccountPaused(AccountId))
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Warning", "Please pause account before modifing building queue"));
                return;
            }

            var result = await _resourceBuildInputValidator.ValidateAsync(ResourceBuildInput);
            if (!result.IsValid)
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Error", result.ToString()));
                return;
            }

            using var scope = _serviceScopeFactory.CreateScope(AccountId);
            var resourceBuildCommand = scope.ServiceProvider.GetRequiredService<ResourceBuildCommand.Handler>();
            await resourceBuildCommand.HandleAsync(new(VillageId, ResourceBuildInput.ToPlan()));
            await JobsModifiedCommand.Execute(new JobsModified(VillageId));
        }

        [ReactiveCommand]
        private async Task Up()
        {
            if (!IsAccountPaused(AccountId))
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Warning", "Please pause account before modifing building queue"));
                return;
            }

            if (Jobs.SelectedItem is null)
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Warning", "Please select before moving"));
                return;
            }

            using var scope = _serviceScopeFactory.CreateScope(AccountId);
            var swapCommand = scope.ServiceProvider.GetRequiredService<SwapCommand.Handler>();
            var newIndex = await swapCommand.HandleAsync(new(new JobId(Jobs[Jobs.SelectedIndex].Id), MoveEnums.Up));
            Jobs.SelectedIndex = newIndex;

            await JobsModifiedCommand.Execute(new JobsModified(VillageId));
        }

        [ReactiveCommand]
        private async Task Down()
        {
            if (!IsAccountPaused(AccountId))
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Warning", "Please pause account before modifing building queue"));
                return;
            }
            if (Jobs.SelectedItem is null)
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Warning", "Please select before moving"));
                return;
            }

            using var scope = _serviceScopeFactory.CreateScope(AccountId);
            var swapCommand = scope.ServiceProvider.GetRequiredService<SwapCommand.Handler>();
            var newIndex = await swapCommand.HandleAsync(new(new JobId(Jobs[Jobs.SelectedIndex].Id), MoveEnums.Down));
            Jobs.SelectedIndex = newIndex;
            await JobsModifiedCommand.Execute(new JobsModified(VillageId));
        }

        [ReactiveCommand]
        private async Task Top()
        {
            if (!IsAccountPaused(AccountId))
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Warning", "Please pause account before modifing building queue"));
                return;
            }
            if (Jobs.SelectedItem is null)
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Warning", "Please select before moving"));
                return;
            }

            using var scope = _serviceScopeFactory.CreateScope(AccountId);
            var moveCommand = scope.ServiceProvider.GetRequiredService<MoveCommand.Handler>();
            var newIndex = await moveCommand.HandleAsync(new(new JobId(Jobs[Jobs.SelectedIndex].Id), MoveEnums.Top));
            Jobs.SelectedIndex = newIndex;

            await JobsModifiedCommand.Execute(new JobsModified(VillageId));
        }

        [ReactiveCommand]
        private async Task Bottom()
        {
            if (!IsAccountPaused(AccountId))
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Warning", "Please pause account before modifing building queue"));
                return;
            }
            if (Jobs.SelectedItem is null)
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Warning", "Please select before moving"));
                return;
            }

            using var scope = _serviceScopeFactory.CreateScope(AccountId);
            var moveCommand = scope.ServiceProvider.GetRequiredService<MoveCommand.Handler>();
            var newIndex = await moveCommand.HandleAsync(new(new JobId(Jobs[Jobs.SelectedIndex].Id), MoveEnums.Bottom));
            Jobs.SelectedIndex = newIndex;
            await JobsModifiedCommand.Execute(new JobsModified(VillageId));
        }

        [ReactiveCommand]
        private async Task Delete()
        {
            if (!IsAccountPaused(AccountId))
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Warning", "Please pause account before modifing building queue"));
                return;
            }
            if (Jobs.SelectedItem is null) return;
            var jobId = Jobs.SelectedItem.Id;

            using var scope = _serviceScopeFactory.CreateScope(AccountId);
            var deleteJobByIdCommand = scope.ServiceProvider.GetRequiredService<DeleteJobByIdCommand.Handler>();
            await deleteJobByIdCommand.HandleAsync(new(new JobId(jobId)));
            await JobsModifiedCommand.Execute(new JobsModified(VillageId));
        }

        [ReactiveCommand]
        private async Task DeleteAll()
        {
            if (!IsAccountPaused(AccountId))
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Warning", "Please pause account before modifing building queue"));
                return;
            }

            using var scope = _serviceScopeFactory.CreateScope(AccountId);
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Jobs
                .Where(x => x.VillageId == VillageId.Value)
                .ExecuteDelete();
            await JobsModifiedCommand.Execute(new JobsModified(VillageId));
        }

        [ReactiveCommand]
        private async Task Import()
        {
            if (!IsAccountPaused(AccountId))
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Warning", "Please pause account before modifing building queue"));
                return;
            }

            var path = await _dialogService.OpenFileDialog.Handle(Unit.Default);
            if (string.IsNullOrEmpty(path)) return;

            List<JobDto>? jobs;
            try
            {
                var jsonString = await File.ReadAllTextAsync(path);
                jobs = JsonSerializer.Deserialize<List<JobDto>>(jsonString);
            }
            catch
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Warning", "Invalid file."));
                return;
            }

            if (jobs is null || jobs.Count == 0)
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Warning", "Selected file does not contain any jobs."));
                return;
            }

            await ApplyJobsAsync(jobs, promptShuffle: true);
        }

        [ReactiveCommand]
        private async Task LoadTemplate()
        {
            if (SelectedTemplate is null)
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Warning", "Select a template before applying."));
                return;
            }

            if (!IsAccountPaused(AccountId))
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Warning", "Please pause account before modifing building queue"));
                return;
            }

            var message = string.Format(CultureInfo.CurrentCulture, "Add {0} jobs from template \"{1}\"?", SelectedTemplate.Jobs.Count, SelectedTemplate.Name);
            var confirm = await _dialogService.ConfirmBox.Handle(new MessageBoxData("Confirmation", message));
            if (!confirm) return;

            var clonedJobs = CloneJobs(SelectedTemplate.Jobs);
            await ApplyJobsAsync(clonedJobs, promptShuffle: true);
        }

        [ReactiveCommand]
        private async Task Export()
        {
            if (!IsAccountPaused(AccountId))
            {
                await _dialogService.MessageBox.Handle(new MessageBoxData("Warning", "Please pause account before modifing building queue"));
                return;
            }

            var path = await _dialogService.SaveFileDialog.Handle(Unit.Default);
            if (string.IsNullOrEmpty(path)) return;

            using var scope = _serviceScopeFactory.CreateScope(AccountId);
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var jobs = context.Jobs
                .Where(x => x.VillageId == VillageId.Value)
                .OrderBy(x => x.Position)
                .ToDto()
                .ToList();
            jobs.ForEach(job => job.Id = JobId.Empty);
            var jsonString = JsonSerializer.Serialize(jobs);
            await File.WriteAllTextAsync(path, jsonString);

            await _dialogService.MessageBox.Handle(new MessageBoxData("Information", "Job list exported"));
        }

        private static List<JobDto> CloneJobs(IEnumerable<JobDto> templateJobs)
        {
            var clones = new List<JobDto>();
            var index = 0;
            foreach (var job in templateJobs)
            {
                clones.Add(new JobDto
                {
                    Position = index++,
                    Type = job.Type,
                    Content = job.Content,
                });
            }
            return clones;
        }

        private async Task ApplyJobsAsync(List<JobDto> jobs, bool promptShuffle)
        {
            var confirm = await _dialogService.ConfirmBox.Handle(new MessageBoxData("Warning", "TBS will remove resource field build job if its position doesn't match with current village."));
            if (!confirm) return;

            var shuffle = false;
            if (promptShuffle)
            {
                shuffle = await _dialogService.ConfirmBox.Handle(new MessageBoxData("Warning", "Do you want to random building location?"));
            }

            using var scope = _serviceScopeFactory.CreateScope(AccountId);
            var fixJobsCommand = scope.ServiceProvider.GetRequiredService<FixJobsCommand.Handler>();
            var fixedJobs = await fixJobsCommand.HandleAsync(new(VillageId, jobs, shuffle));
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var count = context.Jobs
                .Where(x => x.VillageId == VillageId.Value)
                .Count();

            var additionJobs = fixedJobs
                .Select((job, index) => new Job()
                {
                    Position = count + index,
                    VillageId = VillageId.Value,
                    Type = job.Type,
                    Content = job.Content,
                })
                .ToList();

            context.AddRange(additionJobs);
            context.SaveChanges();
            await JobsModifiedCommand.Execute(new JobsModified(VillageId));
        }


        private async Task<bool> RunAutomationAsync()
        {
            if (_automationRunning) return false;

            _automationRunning = true;
            try
            {
                var changed = await _buildAutomationService.EnsureQueueAsync(AccountId, VillageId).ConfigureAwait(false);
                return changed;
            }
            finally
            {
                _automationRunning = false;
            }
        }

        private void QueueNextJobEstimateRefresh()
        {
            Task.Run(UpdateNextJobEstimateAsync);
        }

        private async Task UpdateNextJobEstimateAsync()
        {
            if (AccountId == AccountId.Empty || VillageId == VillageId.Empty)
            {
                RxApp.MainThreadScheduler.Schedule(() =>
                {
                    ApplyNextJobForecast(new NextJobForecast(
                        "Select a village to view job timing.",
                        BuildForecastStatus.Info,
                        Array.Empty<string>()));
                });
                return;
            }

            await _estimateSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                using var scope = _serviceScopeFactory.CreateScope(AccountId);
                var forecast = await BuildNextJobEstimateAsync(scope).ConfigureAwait(false);
                RxApp.MainThreadScheduler.Schedule(() => ApplyNextJobForecast(forecast));
            }
            catch (Exception exception)
            {
                RxApp.MainThreadScheduler.Schedule(() => ApplyNextJobForecast(
                    new NextJobForecast(
                        $"Estimation unavailable ({exception.Message}).",
                        BuildForecastStatus.Error,
                        Array.Empty<string>())));
            }
            finally
            {
                _estimateSemaphore.Release();
            }
        }

        private async Task<NextJobForecast> BuildNextJobEstimateAsync(IServiceScope scope)
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var jobs = context.Jobs
                .AsNoTracking()
                .Where(x => x.VillageId == VillageId.Value)
                .OrderBy(x => x.Position)
                .ToDto()
                .ToList();

            if (jobs.Count == 0)
            {
                return new NextJobForecast("Job queue is empty.", BuildForecastStatus.Info, Array.Empty<string>());
            }

            var storage = context.Storages
                .AsNoTracking()
                .FirstOrDefault(x => x.VillageId == VillageId.Value);

            var buildings = context.Buildings
                .AsNoTracking()
                .Where(x => x.VillageId == VillageId.Value)
                .ToList();

            var queueBuildings = context.QueueBuildings
                .AsNoTracking()
                .Where(x => x.VillageId == VillageId.Value)
                .ToList();

            var serverSpeedSetting = context.ByName(AccountId, AccountSettingEnums.ServerSpeed);
            var serverSpeed = serverSpeedSetting <= 0 ? 1 : serverSpeedSetting;
            var production = CalculateProduction(buildings, storage, serverSpeed);
            var heroReserve = GetHeroResourceTotals(context);
            var useHeroResource = context.BooleanByName(VillageId, VillageSettingEnums.UseHeroResourceForBuilding);
            var plusActive = context.AccountsInfo
                .Where(x => x.AccountId == AccountId.Value)
                .Select(x => x.HasPlusAccount)
                .FirstOrDefault();
            var applyRomanQueueLogic = context.BooleanByName(VillageId, VillageSettingEnums.ApplyRomanQueueLogicWhenBuilding);

            NormalBuildPlan? selectedPlan = null;
            JobDto? unresolvedJob = null;
            var resolvedAnyPlan = false;
            var selectedJobIndex = -1;

            for (var i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                var candidatePlan = await ResolvePlanAsync(scope, job).ConfigureAwait(false);
                if (candidatePlan is null)
                {
                    unresolvedJob ??= job;
                    continue;
                }

                resolvedAnyPlan = true;

                if (!IsPlanPending(candidatePlan, buildings, queueBuildings))
                {
                    continue;
                }

                selectedPlan = candidatePlan;
                selectedJobIndex = i;
                break;
            }

            if (selectedPlan is null)
            {
                var details = BuildQueueSummaries(jobs, 0, includeCurrent: true);

                if (!resolvedAnyPlan && unresolvedJob is not null)
                {
                    if (details.Count == 0)
                    {
                        var summary = JobMapper.GetContent(unresolvedJob);
                        if (!string.IsNullOrWhiteSpace(summary))
                        {
                            details.Add($"Next (unresolved): {summary}");
                        }
                    }

                    return new NextJobForecast($"Next job ({unresolvedJob.Type}) could not be resolved.", BuildForecastStatus.Error, details);
                }

                return new NextJobForecast("All queued jobs are already in progress.", BuildForecastStatus.Info, details);
            }

            var forecast = ComposeEstimate(
                selectedPlan,
                storage,
                queueBuildings,
                production,
                heroReserve,
                useHeroResource,
                plusActive,
                applyRomanQueueLogic);

            var followingDetails = BuildQueueSummaries(jobs, selectedJobIndex, includeCurrent: false);
            return CombineDetails(forecast, followingDetails);
        }

        private async Task<NormalBuildPlan?> ResolvePlanAsync(IServiceScope scope, JobDto job)
        {
            switch (job.Type)
            {
                case JobTypeEnums.NormalBuild:
                    return JsonSerializer.Deserialize<NormalBuildPlan>(job.Content);
                case JobTypeEnums.ResourceBuild:
                    var resourcePlan = JsonSerializer.Deserialize<ResourceBuildPlan>(job.Content);
                    if (resourcePlan is null) return null;

                    var getLayoutBuildingsQuery = scope.ServiceProvider.GetRequiredService<GetLayoutBuildingsCommand.Handler>();
                    var layout = await getLayoutBuildingsQuery.HandleAsync(new(VillageId, true)).ConfigureAwait(false);
                    return ConvertResourcePlan(resourcePlan, layout);
                default:
                    return null;
            }
        }

        private static NormalBuildPlan? ConvertResourcePlan(ResourceBuildPlan plan, List<BuildingItem> layoutBuildings)
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

            var minLevel = resourceFields.Min(x => x.Level);
            var candidate = resourceFields
                .Where(x => x.Level == minLevel)
                .OrderBy(x => x.Location)
                .FirstOrDefault();

            if (candidate is null) return null;

            return new NormalBuildPlan
            {
                Type = candidate.Type,
                Level = candidate.Level + 1,
                Location = candidate.Location,
            };
        }

        private NextJobForecast ComposeEstimate(
            NormalBuildPlan plan,
            Storage? storage,
            List<QueueBuilding> queueBuildings,
            ResourceProductionSnapshot production,
            long[] heroReserves,
            bool useHeroResources,
            bool plusActive,
            bool applyRomanQueueLogic)
        {
            var now = DateTime.Now;
            var details = new List<string>
            {
                $"Next: {DescribePlan(plan)}"
            };

            var required = plan.Type.GetCost(plan.Level);

            var warehouseResources = new[]
            {
                storage?.Wood ?? 0,
                storage?.Clay ?? 0,
                storage?.Iron ?? 0,
                storage?.Crop ?? 0,
            };

            var remainingDeficit = new long[4];
            var heroUsed = new long[4];

            for (var i = 0; i < 4; i++)
            {
                var requiredAmount = required[i];
                var fromWarehouse = warehouseResources[i];
                var initialShortfall = Math.Max(0, requiredAmount - fromWarehouse);

                var availableHero = useHeroResources
                    ? Math.Min(heroReserves[i], initialShortfall)
                    : 0;

                if (availableHero > 0)
                {
                    heroUsed[i] = availableHero;
                }

                var combinedResources = fromWarehouse + availableHero;
                var deficit = Math.Max(0, requiredAmount - combinedResources);
                remainingDeficit[i] = deficit;
            }

            var freeCropRequired = required.Length > 4 ? required[4] : 0;
            var freeCropAvailable = storage?.FreeCrop ?? 0;
            var freeCropDeficit = Math.Max(0, freeCropRequired - freeCropAvailable);

            var queueReadyTime = ComputeQueueReadyTime(queueBuildings, plusActive, applyRomanQueueLogic, now);

            var hourlyProduction = new[]
            {
                production.WoodPerHour,
                production.ClayPerHour,
                production.IronPerHour,
                Math.Max(production.NetCropPerHour, 0d),
            };

            var hasResourceDeficit = remainingDeficit.Any(x => x > 0);
            var heroUsedAny = heroUsed.Any(x => x > 0);

            var resourceTimeUnknown = false;
            double maxResourceHours = 0d;
            for (var i = 0; i < 4; i++)
            {
                if (remainingDeficit[i] <= 0)
                {
                    continue;
                }

                var perHour = hourlyProduction[i];
                if (perHour <= 0)
                {
                    resourceTimeUnknown = true;
                    continue;
                }

                var hours = remainingDeficit[i] / perHour;
                if (hours > maxResourceHours)
                {
                    maxResourceHours = hours;
                }
            }

            DateTime? resourceReadyTime;
            if (!hasResourceDeficit)
            {
                resourceReadyTime = now;
            }
            else if (resourceTimeUnknown)
            {
                resourceReadyTime = null;
            }
            else
            {
                resourceReadyTime = now.AddHours(maxResourceHours);
            }

            if (freeCropDeficit > 0)
            {
                var deficitText = FormatResourceAmount(freeCropDeficit);
                details.Add($"Missing free crop: {deficitText}");
                return CreateForecast(
                    $"Next job {DescribePlan(plan)} blocked by free crop shortage.",
                    BuildForecastStatus.Blocked,
                    details);
            }

            if (resourceReadyTime is null && hasResourceDeficit)
            {
                var deficitText = DescribeResourceDeficit(remainingDeficit, heroUsed);
                if (!string.IsNullOrWhiteSpace(deficitText))
                {
                    details.Add($"Resources needed: {deficitText}");
                }

                if (heroUsedAny)
                {
                    details.Add("Hero crates will be consumed.");
                }

                if (!useHeroResources && !string.IsNullOrWhiteSpace(deficitText))
                {
                    details.Add("Hint: Enable hero crates or adjust production.");
                }

                return CreateForecast(
                    $"Next job {DescribePlan(plan)} waiting on resource income.",
                    BuildForecastStatus.Waiting,
                    details);
            }

            var queueReady = queueReadyTime > now ? queueReadyTime : now;
            var startTime = resourceReadyTime.HasValue && resourceReadyTime.Value > queueReady
                ? resourceReadyTime.Value
                : queueReady;

            var wait = startTime - now;
            var status = wait <= TimeSpan.FromSeconds(3)
                ? BuildForecastStatus.Ready
                : BuildForecastStatus.Waiting;

            if (queueReadyTime > now)
            {
                var queueWait = queueReadyTime - now;
                details.Add($"Queue frees at {FormatTime(queueReadyTime)} ({queueWait.Humanize(precision: 2)})");
            }

            if (resourceReadyTime.HasValue && resourceReadyTime.Value > now)
            {
                var resourceWait = resourceReadyTime.Value - now;
                if (resourceWait > TimeSpan.FromSeconds(3))
                {
                    details.Add($"Resources ready at {FormatTime(resourceReadyTime.Value)} ({resourceWait.Humanize(precision: 2)})");
                }
            }

            var outstandingResources = DescribeResourceDeficit(remainingDeficit, heroUsed);
            if (!string.IsNullOrWhiteSpace(outstandingResources))
            {
                details.Add($"Resources needed: {outstandingResources}");
            }

            if (heroUsedAny)
            {
                details.Add("Hero crates will be consumed.");
            }

            var message = status == BuildForecastStatus.Ready
                ? $"Next job {DescribePlan(plan)} is ready to start."
                : $"Next job {DescribePlan(plan)} expected at {FormatTime(startTime)} ({wait.Humanize(precision: 2)}).";

            return CreateForecast(message, status, details);
        }

        private static NextJobForecast CreateForecast(string message, BuildForecastStatus status, IEnumerable<string> details)
        {
            var cleaned = details
                .Where(detail => !string.IsNullOrWhiteSpace(detail))
                .Distinct()
                .ToList();

            return new NextJobForecast(message, status, cleaned);
        }

        private static NextJobForecast CombineDetails(NextJobForecast forecast, IEnumerable<string> additionalDetails)
        {
            var combined = forecast.Details
                .Concat(additionalDetails ?? Array.Empty<string>())
                .Where(detail => !string.IsNullOrWhiteSpace(detail))
                .Distinct()
                .ToList();

            return new NextJobForecast(forecast.Message, forecast.Status, combined);
        }

        private static List<string> BuildQueueSummaries(IReadOnlyList<JobDto> jobs, int referenceIndex, bool includeCurrent)
        {
            var details = new List<string>();
            if (jobs.Count == 0)
            {
                return details;
            }

            var labels = includeCurrent
                ? new[] { "Next", "Following" }
                : new[] { "Following", "Later" };

            var startIndex = includeCurrent
                ? Math.Max(referenceIndex, 0)
                : Math.Max(referenceIndex + 1, 0);

            var labelIndex = 0;
            for (var i = startIndex; i < jobs.Count && labelIndex < labels.Length; i++)
            {
                if (!includeCurrent && i == referenceIndex)
                {
                    continue;
                }

                var content = JobMapper.GetContent(jobs[i]);
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                details.Add($"{labels[labelIndex]}: {content}");
                labelIndex++;
            }

            return details;
        }

        private void ApplyNextJobForecast(NextJobForecast forecast)
        {
            NextJobEstimate = forecast.Message;
            NextJobStatus = forecast.Status;

            NextJobDetails.Clear();
            foreach (var detail in forecast.Details)
            {
                NextJobDetails.Add(detail);
            }
        }

        private sealed record NextJobForecast(string Message, BuildForecastStatus Status, IReadOnlyList<string> Details);

        private static bool IsPlanPending(
            NormalBuildPlan plan,
            IReadOnlyCollection<Building> buildings,
            IReadOnlyCollection<QueueBuilding> queueBuildings)
        {
            var queuedLevel = queueBuildings
                .Where(x => x.Location == plan.Location && x.Type == plan.Type)
                .Select(x => x.Level)
                .DefaultIfEmpty(0)
                .Max();

            if (queuedLevel >= plan.Level)
            {
                return false;
            }

            var building = buildings.FirstOrDefault(x => x.Location == plan.Location);
            if (building is null)
            {
                return true;
            }

            if (building.Type != plan.Type && building.Type != BuildingEnums.Site)
            {
                return false;
            }

            var effectiveLevel = Math.Max(building.Level, queuedLevel);
            return effectiveLevel < plan.Level;
        }

        private static string DescribePlan(NormalBuildPlan plan)
        {
            return $"{plan.Type.Humanize()} -> level {plan.Level} (slot {plan.Location})";
        }

        private long[] GetHeroResourceTotals(AppDbContext context)
        {
            var totals = new long[4];
            var heroItems = context.HeroItems
                .AsNoTracking()
                .Where(x => x.Type == HeroItemEnums.Wood || x.Type == HeroItemEnums.Clay || x.Type == HeroItemEnums.Iron || x.Type == HeroItemEnums.Crop)
                .ToList();

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

        private ResourceProductionSnapshot CalculateProduction(List<Building> buildings, Storage? storage, int serverSpeed)
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

            var netCrop = storage?.FreeCrop ?? 0;
            return new ResourceProductionSnapshot(wood, clay, iron, netCrop);
        }

        private static double ApplyBonus(double baseProduction, int level, double perLevelBonus = 0.05, int maxLevel = 5)
        {
            if (level <= 0) return baseProduction;
            var effectiveLevel = Math.Min(level, maxLevel);
            return baseProduction * (1 + perLevelBonus * effectiveLevel);
        }

        private static double GetResourceFieldProduction(IEnumerable<Building> buildings, BuildingEnums type, int serverSpeed)
        {
            return buildings
                .Where(x => x.Type == type)
                .Select(x => ResourceProductionPerLevel[Math.Clamp(x.Level, 0, ResourceProductionPerLevel.Length - 1)] * serverSpeed)
                .Sum();
        }

        private static int GetBuildingLevel(IEnumerable<Building> buildings, BuildingEnums type)
        {
            return buildings.FirstOrDefault(x => x.Type == type)?.Level ?? 0;
        }

        private static DateTime ComputeQueueReadyTime(List<QueueBuilding> queueBuildings, bool plusActive, bool applyRomanQueueLogic, DateTime now)
        {
            var slots = 1;
            if (plusActive) slots++;
            if (applyRomanQueueLogic) slots = Math.Max(slots, 2);

            var upcoming = queueBuildings
                .Where(x => x.CompleteTime > now)
                .OrderBy(x => x.CompleteTime)
                .ToList();

            if (upcoming.Count < slots)
            {
                return now;
            }

            return upcoming[0].CompleteTime;
        }

        private static string DescribeResourceDeficit(long[] remaining, long[] heroUsed)
        {
            var parts = new List<string>();
            for (var i = 0; i < remaining.Length; i++)
            {
                var deficit = remaining[i];
                var hero = heroUsed.Length > i ? heroUsed[i] : 0;
                if (deficit <= 0 && hero <= 0) continue;

                var name = ResourceNames[i];
                if (deficit > 0 && hero > 0)
                {
                    parts.Add($"{FormatResourceAmount(deficit + hero)} {name} (hero covers {FormatResourceAmount(hero)})");
                }
                else if (deficit > 0)
                {
                    parts.Add($"{FormatResourceAmount(deficit)} {name}");
                }
                else
                {
                    parts.Add($"{FormatResourceAmount(hero)} {name} via hero crates");
                }
            }

            return parts.Count == 0 ? string.Empty : string.Join(", ", parts);
        }

        private static string FormatResourceAmount(long value)
        {
            if (value >= 1_000_000_000)
            {
                return $"{value / 1_000_000_000d:0.#}b";
            }

            if (value >= 1_000_000)
            {
                return $"{value / 1_000_000d:0.#}m";
            }

            if (value >= 1_000)
            {
                return $"{value / 1_000d:0.#}k";
            }

            return value.ToString("N0", CultureInfo.CurrentCulture);
        }

        private static string FormatTime(DateTime value)
        {
            return value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
        }

        private static readonly string[] ResourceNames = { "wood", "clay", "iron", "crop" };
        private static readonly double[] ResourceProductionPerLevel = { 0, 2, 5, 9, 15, 22, 33, 50, 70, 100, 145, 200, 280, 375, 495, 635, 800, 1000, 1300, 1600, 2000, 2450, 3050, 3750 };
        private const double BaseProductionPerHour = 2d;

        private sealed record ResourceProductionSnapshot(double WoodPerHour, double ClayPerHour, double IronPerHour, double NetCropPerHour);
        private void ScheduleVillageRefresh()
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                if (_taskManager.IsExist<UpdateBuildingTask.Task>(AccountId, VillageId)) return;
                _taskManager.AddOrUpdate(new UpdateBuildingTask.Task(AccountId, VillageId));
            });
        }

        private bool IsAccountPaused(AccountId accountId)
        {
            var status = _taskManager.GetStatus(accountId);
            return status != StatusEnums.Online;
        }
    }

    public enum BuildForecastStatus
    {
        Info,
        Ready,
        Waiting,
        Blocked,
        Error
    }

}

