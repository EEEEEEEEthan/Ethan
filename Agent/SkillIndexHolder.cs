namespace Agent;
public sealed class SkillIndexHolder
{
	public Dictionary<string, SkillSummary> Index { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
