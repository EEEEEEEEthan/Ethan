using Microsoft.SemanticKernel;
namespace Agent;
static class KernelHolder
{
	public static Kernel Kernel
	{
		get
		{
			if(field is null)
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
					httpClient: HttpClient);
				field = builder.Build();
				field.ImportPluginFromObject(new SkillLearningPlugin(SkillHolder.Index), "skills");
				field.ImportPluginFromObject(new FileEditPlugin(), "files");
			}
			return field;
		}
		private set;
	}
	static HttpClient HttpClient => field ??= new() {Timeout = TimeSpan.FromMinutes(5),};
	public static void Rebuild() { Kernel = null!; }
}
