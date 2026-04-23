using Microsoft.SemanticKernel.ChatCompletion;
namespace Agent;
static class HistoryHolder
{
	static ChatHistory? backingHistory;
	public static ChatHistory History
		=> backingHistory
			?? throw new InvalidOperationException("内部错误：尚未调用 HistoryHolder.New。");
	public static void New()
	{
		backingHistory = [];
		backingHistory.Insert(
			0,
			new(AuthorRole.System, SkillSummary.BuildAgentSystemPrompt(SkillHolder.Index)));
	}
}
