using Humanizer;

namespace MainCore.Models
{
    public class NormalBuildPlan
    {
        public const string RewardPlannerSource = "reward-planner";
        public const string RewardPlannerPrerequisiteSource = "reward-planner-prerequisite";

        public int Location { get; set; }
        public int Level { get; set; }
        public BuildingEnums Type { get; set; }
        public string? Source { get; set; }

        public override string ToString()
        {
            return $"{Type.Humanize()} at slot {Location} to level {Level}";
        }
    }
}
