using System.Text;
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
            var builder = new StringBuilder();

            builder.AppendLine("(function() {");
            builder.AppendLine($"    var questId = \"{encodedQuestId}\";");
            builder.AppendLine("    var selectors = [");
            builder.AppendLine("        \"[data-questid='\" + questId + \"'] button:not([disabled])\",");
            builder.AppendLine("        \"[data-quest-id='\" + questId + \"'] button:not([disabled])\",");
            builder.AppendLine("        \"button[data-questid='\" + questId + \"']:not([disabled])\",");
            builder.AppendLine("        \"button[data-quest-id='\" + questId + \"']:not([disabled])\"");
            builder.AppendLine("    ];");
            builder.AppendLine("    for (var i = 0; i < selectors.length; i++) {");
            builder.AppendLine("        var element = document.querySelector(selectors[i]);");
            builder.AppendLine("        if (element) {");
            builder.AppendLine("            element.click();");
            builder.AppendLine("            return;");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            builder.AppendLine("    if (window.Travian && Travian.Game && Travian.Game.Quest) {");
            builder.AppendLine("        var questApi = Travian.Game.Quest;");
            builder.AppendLine("        if (typeof questApi.takeOverQuest === 'function') {");
            builder.AppendLine("            questApi.takeOverQuest(questId);");
            builder.AppendLine("        } else if (typeof questApi.startQuest === 'function') {");
            builder.AppendLine("            questApi.startQuest(questId);");
            builder.AppendLine("        } else if (typeof questApi.loadQuest === 'function') {");
            builder.AppendLine("            questApi.loadQuest(questId);");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            builder.AppendLine("})();");

            return builder.ToString();
        }
    }
}

