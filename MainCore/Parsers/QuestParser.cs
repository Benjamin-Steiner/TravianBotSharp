using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace MainCore.Parsers
{
    public static class QuestParser
    {
        private const string TasksRenderMarker = "Travian.React.Tasks.render(";
        private const string TasksDataMarker = "tasksData:";

        public static HtmlNode GetQuestMaster(HtmlDocument doc)
        {
            var questmasterButton = doc.GetElementbyId("questmasterButton");
            return questmasterButton;
        }

        public static bool IsQuestClaimable(HtmlDocument doc)
        {
            var questmasterButton = GetQuestMaster(doc);
            if (questmasterButton is null) return false;
            var newQuestSpeechBubble = questmasterButton
                .Descendants("div")
                .Any(x => x.HasClass("newQuestSpeechBubble"));
            return newQuestSpeechBubble;
        }

        public static HtmlNode? GetQuestCollectButton(HtmlDocument doc)
        {
            var taskOverviewTable = doc.DocumentNode
                .Descendants("div")
                .FirstOrDefault(x => x.HasClass("taskOverview"));

            if (taskOverviewTable is null) return null;

            var collectButton = taskOverviewTable
                .Descendants("button")
                .FirstOrDefault(x => x.HasClass("collect") && !x.HasClass("disabled"));
            return collectButton;
        }

        public static bool IsQuestPage(HtmlDocument doc)
        {
            var table = doc.DocumentNode
                .Descendants("div")
                .Any(x => x.HasClass("tasks") && x.HasClass("tasksVillage"));
            return table;
        }

        public static QuestInfo? GetEasiestQuest(HtmlDocument doc)
        {
            var payload = GetTasksRenderPayload(doc);
            if (payload is null) return null;

            var tasksJson = ExtractTasksDataJson(payload);
            if (tasksJson is null) return null;

            using var jsonDocument = JsonDocument.Parse(tasksJson);
            var root = jsonDocument.RootElement;

            var candidates = new List<QuestInfo>();
            if (root.TryGetProperty("generalTasks", out var generalTasks))
            {
                candidates.AddRange(ParseQuestGroup(generalTasks, QuestScope.Account));
            }

            if (root.TryGetProperty("activeVillageTasks", out var villageTasks))
            {
                candidates.AddRange(ParseQuestGroup(villageTasks, QuestScope.Village));
            }

            if (candidates.Count == 0) return null;

            return candidates
                .OrderBy(x => x.ReadyToCollect ? 0 : 1)
                .ThenBy(x => x.RequiredValue ?? int.MaxValue)
                .ThenBy(x => x.Level)
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        private static string? GetTasksRenderPayload(HtmlDocument doc)
        {
            foreach (var script in doc.DocumentNode.Descendants("script"))
            {
                var text = script.InnerText;
                if (string.IsNullOrEmpty(text)) continue;

                var markerIndex = text.IndexOf(TasksRenderMarker, StringComparison.Ordinal);
                if (markerIndex < 0) continue;

                markerIndex += TasksRenderMarker.Length;
                var endIndex = text.IndexOf(" );", markerIndex, StringComparison.Ordinal);
                if (endIndex < 0)
                {
                    endIndex = text.IndexOf(");", markerIndex, StringComparison.Ordinal);
                    if (endIndex < 0) continue;
                }

                return text.Substring(markerIndex, endIndex - markerIndex);
            }

            return null;
        }

        private static string? ExtractTasksDataJson(string payload)
        {
            var startIndex = payload.IndexOf(TasksDataMarker, StringComparison.Ordinal);
            if (startIndex < 0) return null;

            startIndex += TasksDataMarker.Length;
            var depth = 0;
            var jsonStart = -1;

            for (var i = startIndex; i < payload.Length; i++)
            {
                var ch = payload[i];
                if (jsonStart < 0)
                {
                    if (char.IsWhiteSpace(ch)) continue;
                    if (ch != '{') return null;
                    jsonStart = i;
                }

                if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0 && jsonStart >= 0)
                    {
                        return payload.Substring(jsonStart, i - jsonStart + 1);
                    }
                }
            }

            return null;
        }

        private static IEnumerable<QuestInfo> ParseQuestGroup(JsonElement element, QuestScope scope)
        {
            foreach (var task in element.EnumerateArray())
            {
                if (!task.TryGetProperty("levels", out var levels)) continue;

                foreach (var level in levels.EnumerateArray())
                {
                    if (IsTrue(level, "wasTakenOver")) continue;
                    if (IsTrue(level, "wasCollected")) continue;

                    if (!level.TryGetProperty("questId", out var questIdElement)) break;
                    var questId = questIdElement.GetString();
                    if (string.IsNullOrWhiteSpace(questId)) break;

                    var requiredValue = level.TryGetProperty("levelValue", out var levelValueElement) && levelValueElement.ValueKind == JsonValueKind.Number
                        ? levelValueElement.GetInt32()
                        : (int?)null;

                    var name = task.TryGetProperty("name", out var nameElement)
                        ? nameElement.GetString() ?? string.Empty
                        : string.Empty;

                    var levelNumber = level.TryGetProperty("level", out var levelElement) && levelElement.ValueKind == JsonValueKind.Number
                        ? levelElement.GetInt32()
                        : 0;

                    var ready = IsTrue(level, "readyToBeCollected");

                    yield return new QuestInfo(name, questId, levelNumber, requiredValue, ready, scope);
                    break;
                }
            }
        }

        private static bool IsTrue(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property)) return false;
            return property.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => property.TryGetInt32(out var number) && number != 0,
                JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) && parsed,
                _ => false,
            };
        }

        public sealed record QuestInfo(string Name, string QuestId, int Level, int? RequiredValue, bool ReadyToCollect, QuestScope Scope);

        public enum QuestScope
        {
            Account,
            Village,
        }
    }
}
