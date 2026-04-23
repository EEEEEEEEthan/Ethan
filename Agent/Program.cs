using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrettyPrompt;
using PrettyPrompt.Completion;
using PrettyPrompt.Consoles;
using PrettyPrompt.Documents;
using PrettyPrompt.Highlighting;
const string defaultBaseUrl = "https://api.openai.com";
var httpClient = new HttpClient {Timeout = TimeSpan.FromMinutes(5),};
var messages = new List<ChatMessage>();
var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";
var baseUrl = (Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? defaultBaseUrl).TrimEnd('/');
Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("聊天 AI。行内以 / 开头弹出斜杠补全；列表未收起时可按 Esc。");
if(string.IsNullOrWhiteSpace(apiKey))
	Console.WriteLine("提示：尚未设置 API Key，可用 /apikey <密钥> 或环境变量 OPENAI_API_KEY。");
var promptConfiguration = new PromptConfiguration(prompt: "> ");
await using var prompt = new Prompt(
	persistentHistoryFilepath: null,
	callbacks: new SlashPromptCallbacks(),
	configuration: promptConfiguration);
while(true)
{
	var result = await prompt.ReadLineAsync();
	if(result is null || !result.IsSuccess)
		break;
	var trimmed = result.Text.Trim();
	if(trimmed.Length == 0)
		continue;
	if(trimmed.StartsWith("/", StringComparison.Ordinal))
	{
		if(!TryHandleSlash(
			   trimmed,
			   ref apiKey,
			   ref model,
			   ref baseUrl,
			   messages,
			   httpClient))
			break;
		continue;
	}
	if(string.IsNullOrWhiteSpace(apiKey))
	{
		Console.WriteLine("请先 /apikey <你的密钥>");
		continue;
	}
	messages.Add(new("user", trimmed));
	try
	{
		var reply = await CompleteChatAsync(
			httpClient,
			baseUrl,
			apiKey,
			model,
			messages);
		messages.Add(new("assistant", reply));
		Console.WriteLine(reply);
	}
	catch(Exception ex)
	{
		messages.RemoveAt(messages.Count - 1);
		Console.WriteLine($"请求失败：{ex.Message}");
	}
}
static bool TryHandleSlash(
	string trimmed,
	ref string? apiKey,
	ref string model,
	ref string baseUrl,
	List<ChatMessage> messages,
	HttpClient httpClient)
{
	var spaceIndex = trimmed.IndexOf(' ');
	var command = spaceIndex < 0? trimmed : trimmed[..spaceIndex];
	var argument = spaceIndex < 0? string.Empty : trimmed[(spaceIndex + 1)..].Trim();
	switch(command.ToLowerInvariant())
	{
		case"/help":
		case"/?":
			Console.WriteLine(
				"""
				/apikey <密钥>     设置 API Key（仅内存，不落盘）
				/model <名称>      设置模型 id，如 gpt-4o-mini
				/url <基础地址>    网关根地址。OpenAI 默认根 https://api.openai.com（请求 …/v1/chat/completions）
				                   火山方舟填 https://ark.cn-beijing.volces.com/api/v3（勿用 /responses），请求 …/api/v3/chat/completions
				                   若已含完整路径 …/chat/completions 则原样使用
				/clear             清空本轮对话上下文
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
			Console.WriteLine("已设置 API Key。");
			return true;
		case"/model":
			if(argument.Length == 0)
			{
				Console.WriteLine("用法：/model <模型id>");
				return true;
			}
			model = argument;
			Console.WriteLine($"已设置模型：{model}");
			return true;
		case"/url":
			if(argument.Length == 0)
			{
				Console.WriteLine("用法：/url <根地址>（无尾 /；方舟用 …/api/v3，勿填 …/responses）");
				return true;
			}
			baseUrl = argument.TrimEnd('/');
			Console.WriteLine($"已设置基础地址：{baseUrl}");
			return true;
		case"/clear":
			messages.Clear();
			Console.WriteLine("已清空对话。");
			return true;
		case"/exit":
		case"/quit":
			return false;
		default:
			Console.WriteLine($"未知指令：{command}，输入 /help 查看列表。");
			return true;
	}
}
static string ResolveChatCompletionsEndpoint(string baseUrl)
{
	var trimmed = baseUrl.TrimEnd('/');
	if(trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
	   || trimmed.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
		return trimmed;
	if(trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
		return$"{trimmed}/chat/completions";
	if(trimmed.Contains("volces.com", StringComparison.OrdinalIgnoreCase)
	   && trimmed.Contains("/api/v3", StringComparison.OrdinalIgnoreCase))
	{
		if(trimmed.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
			trimmed = trimmed[..^"/responses".Length];
		return$"{trimmed}/chat/completions";
	}
	return$"{trimmed}/v1/chat/completions";
}
static async Task<string> CompleteChatAsync(
	HttpClient httpClient,
	string baseUrl,
	string apiKey,
	string model,
	IReadOnlyList<ChatMessage> messages)
{
	var payload = new ChatCompletionRequest(model, messages.Select(static message => new ChatMessageDto(message.Role, message.Content)).ToList());
	var json = JsonSerializer.Serialize(payload, JsonOptions.Serializer);
	var endpoint = ResolveChatCompletionsEndpoint(baseUrl);
	using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
	request.Headers.Authorization = new("Bearer", apiKey);
	request.Content = new StringContent(json, Encoding.UTF8, "application/json");
	using var response = await httpClient.SendAsync(request);
	var body = await response.Content.ReadAsStringAsync();
	if(!response.IsSuccessStatusCode)
		throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
	using var document = JsonDocument.Parse(body);
	var root = document.RootElement;
	if(!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
		throw new InvalidOperationException($"响应无 choices：{body}");
	var first = choices[0];
	if(!first.TryGetProperty("message", out var messageElement))
		throw new InvalidOperationException($"响应缺少 message：{body}");
	var content = messageElement.GetProperty("content").GetString() ?? string.Empty;
	return content;
}
sealed record ChatMessage(string Role, string Content);
sealed record ChatMessageDto(
	[property: JsonPropertyName("role")]string Role,
	[property: JsonPropertyName("content")]
	string Content);
sealed record ChatCompletionRequest(
	[property: JsonPropertyName("model")]string Model,
	[property: JsonPropertyName("messages")]
	IReadOnlyList<ChatMessageDto> Messages);
file static class JsonOptions
{
	public static readonly JsonSerializerOptions Serializer = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};
}
file sealed class SlashPromptCallbacks: PromptCallbacks
{
	static readonly (string Replacement, string Caption)[] SlashCommands =
	[
		("/apikey ", "设置 API Key"),
		("/model ", "设置模型 id"),
		("/url ", "OpenAI 兼容根地址"),
		("/clear", "清空对话上下文"),
		("/help", "指令说明"),
		("/?", "同 /help"),
		("/exit", "退出"),
		("/quit", "退出"),
	];
	static bool IsSlashCommandToken(string text, int caret)
	{
		var tokenStart = GetTokenStartIndex(text, caret);
		return tokenStart < text.Length && text[tokenStart] == '/';
	}
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
		var items = SlashCommands
			.Select(static entry => new SlashCompletionItem(entry.Replacement, entry.Caption))
			.ToArray();
		return Task.FromResult<IReadOnlyList<CompletionItem>>(items);
	}
	protected override Task<bool> ShouldOpenCompletionWindowAsync(
		string text,
		int caret,
		KeyPress keyPress,
		CancellationToken cancellationToken)
	{
		if(caret > 0 && IsSlashCommandToken(text, caret))
			return Task.FromResult(true);
		return Task.FromResult(false);
	}
}
file sealed class SlashCompletionItem: CompletionItem
{
	readonly string commandText;
	public SlashCompletionItem(string replacement, string caption)
		: base(
			replacementText: replacement,
			displayText: new($"{replacement.TrimEnd()} — {caption}"),
			filterText: replacement.TrimEnd(),
			getExtendedDescription: _ => Task.FromResult(new FormattedString(caption)))
	{
		commandText = replacement.TrimEnd();
	}
	public override int GetCompletionItemPriority(string text, int caret, TextSpan spanToBeReplaced)
	{
		if(spanToBeReplaced.IsEmpty)
			return int.MinValue;
		var pattern = text.AsSpan(spanToBeReplaced);
		if(pattern.Length == 0 || pattern[0] != '/')
			return int.MinValue;
		if(!commandText.AsSpan().StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
			return int.MinValue;
		if(pattern.Length == commandText.Length)
			return 1000;
		return 500 + pattern.Length;
	}
}
