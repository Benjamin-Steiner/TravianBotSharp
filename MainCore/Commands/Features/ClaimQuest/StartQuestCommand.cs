using System.Text.Encodings.Web;

namespace MainCore.Commands.Features.ClaimQuest
{
    [Handler]
    public static partial class StartQuestCommand
    {
        public sealed record Command : ICommand;

        private static async ValueTask<Result> HandleAsync(
            Command command,
            IChromeBrowser browser,
            IDelayService delayService,
            SwitchTabCommand.Handler switchTabCommand,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var quest = QuestParser.GetEasiestQuest(browser.Html);
            if (quest is null) return Result.Ok();

            var tabIndex = quest.Scope == QuestParser.QuestScope.Account ? 0 : 1;
            var result = await switchTabCommand.HandleAsync(new(tabIndex), cancellationToken);
            if (result.IsFailed) return result;

            result = await browser.ExecuteJsScript(BuildStartQuestScript(quest.QuestId));
            if (result.IsFailed) return result;

            await delayService.DelayClick(cancellationToken);
            logger.Information("Started quest {QuestName} ({QuestId})", quest.Name, quest.QuestId);
            return Result.Ok();
        }

        private static string BuildStartQuestScript(string questId)
        {
            var encodedQuestId = JavaScriptEncoder.Default.Encode(questId);
            return $@"(function() {{
    var questId = \"{encodedQuestId}\";
    var selectors = [
        '[data-questid=\"' + questId + '\"] button:not([disabled])',
        '[data-quest-id=\"' + questId + '\"] button:not([disabled])',
        'button[data-questid=\"' + questId + '\"]:not([disabled])',
        'button[data-quest-id=\"' + questId + '\"]:not([disabled])'
    ];
    for (var i = 0; i < selectors.length; i++) {
        var element = document.querySelector(selectors[i]);
        if (element) {
            element.click();
            return;
        }
    }
    if (window.Travian && Travian.Game && Travian.Game.Quest) {
        var questApi = Travian.Game.Quest;
        if (typeof questApi.takeOverQuest === 'function') {
            questApi.takeOverQuest(questId);
        } else if (typeof questApi.startQuest === 'function') {
            questApi.startQuest(questId);
        } else if (typeof questApi.loadQuest === 'function') {
            questApi.loadQuest(questId);
        }
    }
})();";
        }
    }
}
