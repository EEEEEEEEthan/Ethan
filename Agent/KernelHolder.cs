using Microsoft.SemanticKernel;
namespace Agent;
static class KernelHolder
{
	static Kernel? backingKernel;
	public static Kernel Kernel
		=> backingKernel
			?? throw new InvalidOperationException("内部错误：尚未调用 KernelHolder.Build。");
	public static void Build(HttpClient httpClient)
	{
		var baseText = UserChatSettings.BaseUrl.Trim();
		var endpoint = baseText.Length == 0
			? new("https://api.openai.com/v1", UriKind.Absolute)
			: new Uri(baseText, UriKind.Absolute);
		var builder = Kernel.CreateBuilder();
		builder.AddOpenAIChatCompletion(
			modelId: UserChatSettings.Model,
			endpoint: endpoint,
			apiKey: UserChatSettings.ApiKey,
			orgId: null,
			serviceId: null,
			httpClient: httpClient);
		backingKernel = builder.Build();
		backingKernel.ImportPluginFromObject(new SkillLearningPlugin(SkillHolder.Index), "skills");
	}
}
