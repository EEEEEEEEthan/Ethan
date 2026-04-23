using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agent;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using PrettyPrompt;
using PrettyPrompt.Completion;
using PrettyPrompt.Consoles;
using PrettyPrompt.Documents;
using PrettyPrompt.Highlighting;
var userSettingsDirectory = Path.Combine(
	Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
	"Ethan",
	"Agent");
var userSettingsFilepath = Path.Combine(userSettingsDirectory, "settings.json");
ChatHistory conversation = new();
var httpClient = new HttpClient {Timeout = TimeSpan.FromMinutes(5),};
var environmentApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
var environmentModel = Environment.GetEnvironmentVariable("OPENAI_MODEL");
var environmentBaseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
string? apiKey = null;
var model = "gpt-4o-mini";
var baseUrl = "https://api.openai.com";
if(File.Exists(userSettingsFilepath))
	try
	{
		var jsonText = File.ReadAllText(userSettingsFilepath);
		var loaded = JsonSerializer.Deserialize<UserChatSettings>(jsonText, ChatJson.serializer);
		if(loaded is {})
		{
			apiKey = loaded.ApiKey;
			if(!string.IsNullOrWhiteSpace(loaded.Model))
				model = loaded.Model;
			if(!string.IsNullOrWhiteSpace(loaded.BaseUrl))
				baseUrl = loaded.BaseUrl.TrimEnd('/');
		}
	}
	catch(JsonException) { Console.WriteLine($"提示：配置文件格式无效，已忽略：{userSettingsFilepath}"); }
if(!string.IsNullOrEmpty(environmentApiKey))
	apiKey = environmentApiKey;
if(!string.IsNullOrEmpty(environmentModel))
	model = environmentModel;
if(!string.IsNullOrEmpty(environmentBaseUrl))
	baseUrl = environmentBaseUrl.TrimEnd('/');
Console.OutputEncoding = Encoding.UTF8;
var skillHolder = new SkillIndexHolder
{
	Index = SkillSummary.BuildIndex(SkillSummary.DefaultSkillRepositoryRoots()),
};
Console.WriteLine($"已建立技能索引：{skillHolder.Index.Count} 条。");
Console.WriteLine("聊天 AI。行内以 / 开头弹出斜杠补全；列表未收起时可按 Esc。");
if(string.IsNullOrWhiteSpace(apiKey))
	Console.WriteLine(
		$"提示：尚未设置 API Key，可用 /apikey <密钥> 或环境变量 OPENAI_API_KEY；配置保存于 {userSettingsFilepath}。");
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
		if(!tryHandleSlash(
			   trimmed,
			   userSettingsFilepath,
			   ref apiKey,
			   ref model,
			   ref baseUrl,
			   skillHolder,
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
	if(string.IsNullOrWhiteSpace(apiKey))
	{
		Console.WriteLine("请先 /apikey <你的密钥>");
		continue;
	}
	if(lastKernelConfig is null || !lastKernelConfig.MatchesCurrent(apiKey, model, baseUrl) || activeKernel is null)
	{
		Uri endpoint;
		var working = baseUrl.TrimEnd('/');
		if(working.Length == 0)
			endpoint = new("https://api.openai.com/v1", UriKind.Absolute);
		else
		{
			if(working.EndsWith("/responses", StringComparison.OrdinalIgnoreCase)
			   && working.Contains("volces.com", StringComparison.OrdinalIgnoreCase)
			   && working.Contains("/api/v3", StringComparison.OrdinalIgnoreCase))
				working = working[..^"/responses".Length].TrimEnd('/');
			if(working.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
				endpoint = new(working[..^"/chat/completions".Length], UriKind.Absolute);
			else if(working.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
				endpoint = new(working[..^"/chat/completions".Length], UriKind.Absolute);
			else if(working.Contains("volces.com", StringComparison.OrdinalIgnoreCase)
			        && working.Contains("/api/v3", StringComparison.OrdinalIgnoreCase))
				endpoint = new(working, UriKind.Absolute);
			else if(working.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
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
			modelId: model,
			endpoint: endpoint,
			apiKey: apiKey,
			orgId: null,
			serviceId: null,
			httpClient: httpClient);
		activeKernel = builder.Build();
		activeKernel.ImportPluginFromObject(new SkillLearningPlugin(skillHolder), "skills");
		lastKernelConfig = new(apiKey, model, baseUrl);
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
			skillHolder);
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
static async Task<string?> runStreamingChatTurnAsync(
	Kernel kernel,
	IChatCompletionService chatCompletion,
	ChatHistory conversation,
	SkillIndexHolder skillHolder)
{
	refreshSkillSystemMessage(conversation, skillHolder.Index);
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
	return textBuffer.Length > 0? textBuffer.ToString() : null;
}
static bool tryHandleSlash(
	string trimmed,
	string userSettingsFilepath,
	ref string? apiKey,
	ref string model,
	ref string baseUrl,
	SkillIndexHolder skillHolder,
	ChatHistory conversation,
	out bool connectionSettingsTouched)
{
	static void save(string filepath, string? apiKeyValue, string modelId, string userBase)
	{
		var parentDirectory = Path.GetDirectoryName(filepath);
		if(!string.IsNullOrEmpty(parentDirectory))
			Directory.CreateDirectory(parentDirectory);
		var payload = new UserChatSettings(apiKeyValue, userBase.TrimEnd('/'), modelId);
		var jsonText = JsonSerializer.Serialize(payload, ChatJson.serializer);
		File.WriteAllText(filepath, jsonText);
	}
	connectionSettingsTouched = false;
	var spaceIndex = trimmed.IndexOf(' ');
	var command = spaceIndex < 0? trimmed : trimmed[..spaceIndex];
	var argument = spaceIndex < 0? string.Empty : trimmed[(spaceIndex + 1)..].Trim();
	switch(command.ToLowerInvariant())
	{
		case"/help":
		case"/?":
			Console.WriteLine(
				"""
				/apikey <密钥>     设置 API Key 并写入用户配置目录
				/model <名称>      设置模型 id 并保存，如 gpt-4o-mini
				/url <基础地址>    网关根地址并保存。OpenAI 默认根 https://api.openai.com（请求 …/v1/chat/completions）
				                   火山方舟填 https://ark.cn-beijing.volces.com/api/v3（勿用 /responses），请求 …/api/v3/chat/completions
				                   若已含完整路径 …/chat/completions 则原样使用
				环境变量 OPENAI_* 若已设置则优先于配置文件
				/clear             清空本轮对话上下文
				/update-skills     重新扫描 .cursor/skills 与 skills-cursor 并建立技能索引
				/exit 或 /quit     退出
				""");
			return true;
		case"/apikey":
			if(argument.Length == 0)
			{
				Console.WriteLine("用法：/apikey <密钥>");
				return true;
			}
			apiKey = argument;
			connectionSettingsTouched = true;
			save(userSettingsFilepath, apiKey, model, baseUrl);
			Console.WriteLine("已设置 API Key 并已保存。");
			return true;
		case"/model":
			if(argument.Length == 0)
			{
				Console.WriteLine("用法：/model <模型id>");
				return true;
			}
			model = argument;
			connectionSettingsTouched = true;
			save(userSettingsFilepath, apiKey, model, baseUrl);
			Console.WriteLine($"已设置模型：{model}（已保存）");
			return true;
		case"/url":
			if(argument.Length == 0)
			{
				Console.WriteLine("用法：/url <根地址>（无尾 /；方舟用 …/api/v3，勿填 …/responses）");
				return true;
			}
			baseUrl = argument.TrimEnd('/');
			connectionSettingsTouched = true;
			save(userSettingsFilepath, apiKey, model, baseUrl);
			Console.WriteLine($"已设置基础地址：{baseUrl}（已保存）");
			return true;
		case"/clear":
			conversation.Clear();
			Console.WriteLine("已清空对话。");
			return true;
		case"/update-skills":
			skillHolder.Index = SkillSummary.BuildIndex(SkillSummary.DefaultSkillRepositoryRoots());
			Console.WriteLine($"已重建技能索引：{skillHolder.Index.Count} 条。");
			return true;
		case"/exit":
		case"/quit":
			return false;
		default:
			Console.WriteLine($"未知指令：{command}，输入 /help 查看列表。");
			return true;
	}
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
file sealed record UserChatSettings(
	[property: JsonPropertyName("apiKey")]string? ApiKey,
	[property: JsonPropertyName("baseUrl")]
	string BaseUrl,
	[property: JsonPropertyName("model")]string Model);
file static class ChatJson
{
	internal static readonly JsonSerializerOptions serializer = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};
}
file sealed class SlashPromptCallbacks: PromptCallbacks
{
	static readonly CompletionItem[] slashCompletionItems =
	[
		new SlashCompletionItem("/apikey ", "设置 API Key"),
		new SlashCompletionItem("/model ", "设置模型 id"),
		new SlashCompletionItem("/url ", "OpenAI 兼容根地址"),
		new SlashCompletionItem("/clear", "清空对话上下文"),
		new SlashCompletionItem("/update-skills", "重建技能索引"),
		new SlashCompletionItem("/help", "指令说明"),
		new SlashCompletionItem("/?", "同 /help"),
		new SlashCompletionItem("/exit", "退出"),
		new SlashCompletionItem("/quit", "退出"),
	];
	static int GetTokenStartIndex(string text, int caret)
	{
		var limit = caret > 0? caret - 1 : -1;
		for(var index = limit; index >= 0; index--)
			if(text[index] is' ' or'\t')
				return index + 1;
		return 0;
	}
	protected override Task<TextSpan> GetSpanToReplaceByCompletionAsync(string text, int caret, CancellationToken cancellationToken)
	{
		var tokenStart = GetTokenStartIndex(text, caret);
		if(tokenStart >= text.Length || text[tokenStart] != '/')
		{
			if(caret > 0)
				return Task.FromResult(TextSpan.FromBounds(caret, caret));
			return Task.FromResult(TextSpan.FromBounds(0, 0));
		}
		return Task.FromResult(TextSpan.FromBounds(tokenStart, caret));
	}
	protected override Task<IReadOnlyList<CompletionItem>> GetCompletionItemsAsync(
		string text,
		int caret,
		TextSpan spanToBeReplaced,
		CancellationToken cancellationToken)
	{
		if(spanToBeReplaced.IsEmpty)
			return Task.FromResult<IReadOnlyList<CompletionItem>>(Array.Empty<CompletionItem>());
		var token = text.AsSpan(spanToBeReplaced);
		if(token.Length == 0 || token[0] != '/')
			return Task.FromResult<IReadOnlyList<CompletionItem>>(Array.Empty<CompletionItem>());
		return Task.FromResult<IReadOnlyList<CompletionItem>>(slashCompletionItems);
	}
	protected override Task<bool> ShouldOpenCompletionWindowAsync(
		string text,
		int caret,
		KeyPress keyPress,
		CancellationToken cancellationToken)
	{
		if(caret <= 0)
			return Task.FromResult(false);
		var tokenStart = GetTokenStartIndex(text, caret);
		return Task.FromResult(tokenStart < text.Length && text[tokenStart] == '/');
	}
}
file sealed class SlashCompletionItem(string replacement, string caption): CompletionItem(
	replacementText: replacement,
	displayText: new($"{replacement.TrimEnd()} — {caption}"),
	filterText: replacement.TrimEnd(),
	// ReSharper disable once LambdaExpressionCanBeMadeStatic
	getExtendedDescription: _ => Task.FromResult(new FormattedString(caption)))
{
	readonly string commandText = replacement.TrimEnd();
	public override int GetCompletionItemPriority(string text, int caret, TextSpan spanToBeReplaced)
	{
		if(spanToBeReplaced.IsEmpty)
			return int.MinValue;
		var pattern = text.AsSpan(spanToBeReplaced);
		if(pattern.Length == 0 || pattern[0] != '/' || !commandText.AsSpan().StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
			return int.MinValue;
		if(pattern.Length == commandText.Length)
			return 1000;
		return 500 + pattern.Length;
	}
}
