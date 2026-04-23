namespace Agent;
static class SkillHolder
{
	static Dictionary<string, SkillSummary>? backingIndex;
	public static Dictionary<string, SkillSummary> Index
		=> backingIndex
			?? throw new InvalidOperationException("内部错误：尚未调用 SkillHolder.Build。");
	public static void Build() { backingIndex = SkillSummary.BuildIndex(SkillSummary.DefaultSkillRepositoryRoots); }
}
