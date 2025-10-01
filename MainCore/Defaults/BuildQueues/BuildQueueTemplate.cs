using System.Collections.Generic;
using MainCore.DTO;

namespace MainCore.Defaults.BuildQueues
{
    public sealed record BuildQueueTemplate(string Name, IReadOnlyList<JobDto> Jobs);
}
