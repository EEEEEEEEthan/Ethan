using System.Text;
using Agent;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using PrettyPrompt;
ChatHistory conversation = [];
var httpClient = new HttpClient {Timeout = TimeSpan.FromMinutes(5),};
Console.OutputEncoding = Encoding.UTF8;
var skillIndex = SkillSummary.BuildIndex(SkillSummary.DefaultSkillRepositoryRoots());
Console.WriteLine($"已建立技能索引：{skillIndex.Count} 条。");
Console.WriteLine("聊天 AI。行内以 / 开头弹出斜杠补全；列表未收起时可按 Esc。");
if(string.IsNullOrWhiteSpace(UserChatSettings.ApiKey))
	Console.WriteLine(
		$"提示：尚未设置 API Key，可用 /apikey <密钥> 或环境变量 OPENAI_API_KEY；配置保存于 {UserChatSettings.SettingsFilepath}。");
var promptConfiguration = new PromptConfiguration(prompt: "> ");
await using var prompt = new Prompt(
	persistentHistoryFilepath: null,
	callbacks: new SlashPromptCallbacks(),
	configuration: promptConfiguration);
Kernel? activeKernel = null;
KernelConfigSnapshot? lastKernelConfig = null;
while(true)
{
	var result = await prompt.ReadLineAsync();
	if(!result.IsSuccess)
		break;
	var trimmed = result.Text.Trim();
	if(trimmed.Length == 0)
		continue;
	if(trimmed.StartsWith('/'))
	{
		if(!SlashPromptCallbacks.TryHandleLine(
			   trimmed,
			   skillIndex,
			   conversation,
			   out var connectionSettingsTouched))
			break;
		if(connectionSettingsTouched)
		{
			activeKernel = null;
			lastKernelConfig = null;
		}
		continue;
	}
	if(string.IsNullOrWhiteSpace(UserChatSettings.ApiKey))
	{
		Console.WriteLine("请先 /apikey <你的密钥>");
		continue;
	}
	if(lastKernelConfig is null
	   || !lastKernelConfig.MatchesCurrent(UserChatSettings.ApiKey, UserChatSettings.Model, UserChatSettings.BaseUrl)
	   || activeKernel is null)
	{
		Uri endpoint;
		var working = UserChatSettings.BaseUrl.TrimEnd('/');
		if(working.Length == 0)
			endpoint = new("https://api.openai.com/v1", UriKind.Absolute);
		else
		{
			if(working.EndsWith("/responses", StringComparison.OrdinalIgnoreCase)
			   && working.Contains("volces.com", StringComparison.OrdinalIgnoreCase)
			   && working.Contains("/api/v3", StringComparison.OrdinalIgnoreCase))
				working = working[..^"/responses".Length].TrimEnd('/');
			if(working.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase) || working.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
				endpoint = new(working[..^"/chat/completions".Length], UriKind.Absolute);
			else if(working.Contains("volces.com", StringComparison.OrdinalIgnoreCase)
			        && working.Contains("/api/v3", StringComparison.OrdinalIgnoreCase) || working.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
				endpoint = new(working, UriKind.Absolute);
			else if(Uri.TryCreate(working, UriKind.Absolute, out var openAiCheck)
			        && string.Equals(openAiCheck.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase)
			        && (string.IsNullOrEmpty(openAiCheck.AbsolutePath) || openAiCheck.AbsolutePath == "/"))
				endpoint = new("https://api.openai.com/v1", UriKind.Absolute);
			else if(Uri.TryCreate(working + "/v1", UriKind.Absolute, out var withV1))
				endpoint = withV1;
			else
				endpoint = new(working, UriKind.Absolute);
		}
		var builder = Kernel.CreateBuilder();
		builder.AddOpenAIChatCompletion(
			modelId: UserChatSettings.Model,
			endpoint: endpoint,
			apiKey: UserChatSettings.ApiKey,
			orgId: null,
			serviceId: null,
			httpClient: httpClient);
		activeKernel = builder.Build();
		activeKernel.ImportPluginFromObject(new SkillLearningPlugin(skillIndex), "skills");
		lastKernelConfig = new(UserChatSettings.ApiKey, UserChatSettings.Model, UserChatSettings.BaseUrl);
	}
	var currentKernel = activeKernel
		?? throw new InvalidOperationException("内部错误：Kernel 未初始化。");
	var turnStartCount = conversation.Count;
	conversation.AddUserMessage(trimmed);
	try
	{
		var chat = currentKernel.GetRequiredService<IChatCompletionService>();
		await runStreamingChatTurnAsync(
			currentKernel,
			chat,
			conversation,
			skillIndex);
	}
	catch(Exception exception)
	{
		while(conversation.Count > turnStartCount)
			conversation.RemoveAt(conversation.Count - 1);
		Console.WriteLine($"请求失败：{exception.Message}");
	}
}
return;
static void refreshSkillSystemMessage(ChatHistory chatHistory, IReadOnlyDictionary<string, SkillSummary> index)
{
	if(chatHistory.Count > 0 && chatHistory[0].Role == AuthorRole.System)
		chatHistory.RemoveAt(0);
	chatHistory.Insert(0, new(AuthorRole.System, SkillSummary.BuildAgentSystemPrompt(index)));
}
static async Task runStreamingChatTurnAsync(
	Kernel kernel,
	IChatCompletionService chatCompletion,
	ChatHistory conversation,
	Dictionary<string, SkillSummary> skillIndex)
{
	refreshSkillSystemMessage(conversation, skillIndex);
	var execution = new OpenAIPromptExecutionSettings {ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,};
	var textBuffer = new StringBuilder();
	var streamPendingCarriageReturn = false;
	var streaming = chatCompletion.GetStreamingChatMessageContentsAsync(
		chatHistory: conversation,
		executionSettings: execution,
		kernel: kernel);
	await foreach(var part in streaming.ConfigureAwait(false))
	{
		var textChunk = part.Content;
		if(string.IsNullOrEmpty(textChunk))
			continue;
		textBuffer.Append(textChunk);
		var formatted = formatTextForWindowsConsolePiece(textChunk, ref streamPendingCarriageReturn);
		if(formatted.Length != 0)
		{
			Console.Write(formatted);
			await Console.Out.FlushAsync().ConfigureAwait(false);
		}
	}
	var trailingNewline = formatTextForWindowsConsolePiece(string.Empty, ref streamPendingCarriageReturn);
	if(trailingNewline.Length != 0)
		Console.Write(trailingNewline);
	await Console.Out.FlushAsync().ConfigureAwait(false);
	Console.WriteLine();
}
static string formatTextForWindowsConsolePiece(string segment, ref bool pendingCarriageReturn)
{
	if(segment.Length == 0)
		return string.Empty;
	if(Environment.NewLine is not"\r\n" || (!pendingCarriageReturn && segment.AsSpan().IndexOfAny('\r', '\n') < 0))
		return segment;
	var builder = new StringBuilder(segment.Length + 4);
	if(pendingCarriageReturn)
	{
		pendingCarriageReturn = false;
		if(segment[0] == '\n')
		{
			builder.Append("\r\n");
			segment = segment[1..];
		}
		else
			builder.Append('\r');
	}
	if(segment.Length == 0)
		return builder.ToString();
	for(var index = 0; index < segment.Length; index++)
	{
		var character = segment[index];
		switch(character)
		{
			case'\r':
				if(index + 1 < segment.Length && segment[index + 1] == '\n')
				{
					builder.Append("\r\n");
					index++;
				}
				else if(index == segment.Length - 1)
					pendingCarriageReturn = true;
				else
					builder.Append('\r');
				break;
			case'\n':
				builder.Append("\r\n");
				break;
			default:
				builder.Append(character);
				break;
		}
	}
	return builder.ToString();
}
file sealed class KernelConfigSnapshot(
	string? apiKeySnapshot,
	string modelIdSnapshot,
	string userBaseUrlSnapshot)
{
	public bool MatchesCurrent(string? key, string currentModel, string currentBase)
	{
		return key == apiKeySnapshot
			&& currentModel == modelIdSnapshot
			&& string.Equals(
				currentBase.TrimEnd('/'),
				userBaseUrlSnapshot.TrimEnd('/'),
				StringComparison.Ordinal);
	}
}
