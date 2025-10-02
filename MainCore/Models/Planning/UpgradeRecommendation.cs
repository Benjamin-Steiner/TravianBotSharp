using System;
using System.Collections.Generic;
using System.Linq;

namespace MainCore.Models.Planning
{
    public sealed class UpgradeRecommendation
    {
        public UpgradeRecommendation(IEnumerable<NormalBuildPlan> plans, IEnumerable<string> insights)
        {
            Plans = (plans ?? Array.Empty<NormalBuildPlan>()).ToList();
            Insights = (insights ?? Array.Empty<string>()).ToList();
        }

        public IReadOnlyList<NormalBuildPlan> Plans { get; }

        public IReadOnlyList<string> Insights { get; }

        public bool HasPlans => Plans.Count > 0;

        public string Summary => Plans.Count == 0
            ? "No plans"
            : string.Join(", ", Plans.Select(plan => plan.ToString()));
    }
}

