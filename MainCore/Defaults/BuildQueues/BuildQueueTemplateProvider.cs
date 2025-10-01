using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using MainCore.DTO;

namespace MainCore.Defaults.BuildQueues
{
    public static class BuildQueueTemplateProvider
    {
        private const string ResourcePrefix = "MainCore.Defaults.BuildQueues.";
        private static readonly Lazy<IReadOnlyList<BuildQueueTemplate>> _templates = new(LoadTemplates);

        public static IReadOnlyList<BuildQueueTemplate> Templates => _templates.Value;

        private static IReadOnlyList<BuildQueueTemplate> LoadTemplates()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resources = assembly
                .GetManifestResourceNames()
                .Where(name => name.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name)
                .ToList();

            var templates = new List<BuildQueueTemplate>();
            foreach (var resourceName in resources)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null) continue;
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();

                List<JobDto>? jobs;
                try
                {
                    jobs = JsonSerializer.Deserialize<List<JobDto>>(json);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (jobs is null || jobs.Count == 0) continue;

                var templateName = ToDisplayName(resourceName);
                templates.Add(new BuildQueueTemplate(templateName, jobs));
            }

            return templates;
        }

        private static string ToDisplayName(string resourceName)
        {
            var fileName = resourceName.Substring(ResourcePrefix.Length);
            fileName = Path.GetFileNameWithoutExtension(fileName);
            var textInfo = CultureInfo.CurrentCulture.TextInfo;
            return textInfo.ToTitleCase(fileName.Replace('-', ' ').Replace('_', ' '));
        }
    }
}
