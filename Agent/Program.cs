using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
var httpClient = new HttpClient {Timeout = TimeSpan.FromMinutes(5),};
List<PersistedChatMessage> messages = [];
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
Console.WriteLine("聊天 AI。行内以 / 开头弹出斜杠补全；列表未收起时可按 Esc。");
if(string.IsNullOrWhiteSpace(apiKey))
	Console.WriteLine(
		$"提示：尚未设置 API Key，可用 /apikey <密钥> 或环境变量 OPENAI_API_KEY；配置保存于 {userSettingsFilepath}。");
var promptConfiguration = new PromptConfiguration(prompt: "> ");
await using var prompt = new Prompt(
	persistentHistoryFilepath: null,
	callbacks: new SlashPromptCallbacks(),
	configuration: promptConfiguration);
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
			   messages))
			break;
		continue;
	}
	if(string.IsNullOrWhiteSpace(apiKey))
	{
		Console.WriteLine("请先 /apikey <你的密钥>");
		continue;
	}
	var turnStartIndex = messages.Count;
	messages.Add(new("user", trimmed));
	try
	{
		var conversationFinished = false;
		for(var guard = 0; guard < 64; guard++)
		{
			var turn = await sendChatCompletionTurnAsync(httpClient, baseUrl, apiKey!, model, messages);
			if(string.Equals(turn.FinishReason, "tool_calls", StringComparison.OrdinalIgnoreCase)
			   && turn.ToolCalls is {Count: > 0})
			{
				messages.Add(
					new(
						"assistant",
						string.IsNullOrWhiteSpace(turn.AssistantText)? null : turn.AssistantText,
						turn.ToolCalls));
				foreach(var call in turn.ToolCalls)
				{
					var header = $"[工具调用] {call.Function.Name} 参数：{call.Function.Arguments}";
					Console.WriteLine(header);
					Console.Out.Flush();
					var toolBody = executeLocalToolCall(call);
					Console.WriteLine("[工具输出]");
					Console.Out.Flush();
					writeStreamingConsole(toolBody);
					var toolMessageContent = $"{header}\n\n{toolBody}";
					messages.Add(new("tool", toolMessageContent, null, call.Id));
				}
				continue;
			}
			if(string.Equals(turn.FinishReason, "tool_calls", StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("响应为 tool_calls 但未包含任何 tool_call。");
			var finalText = turn.AssistantText ?? string.Empty;
			messages.Add(new("assistant", finalText));
			conversationFinished = true;
			break;
		}
		if(!conversationFinished)
			throw new InvalidOperationException("工具调用轮次超过上限，已中止。");
	}
	catch(Exception ex)
	{
		if(messages.Count > turnStartIndex)
			messages.RemoveRange(turnStartIndex, messages.Count - turnStartIndex);
		Console.WriteLine($"请求失败：{ex.Message}");
	}
}
static bool tryHandleSlash(
	string trimmed,
	string userSettingsFilepath,
	ref string? apiKey,
	ref string model,
	ref string baseUrl,
	List<PersistedChatMessage> messages)
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
				/apikey <密钥>     设置 API Key 并写入用户配置目录
				/model <名称>      设置模型 id 并保存，如 gpt-4o-mini
				/url <基础地址>    网关根地址并保存。OpenAI 默认根 https://api.openai.com（请求 …/v1/chat/completions）
				                   火山方舟填 https://ark.cn-beijing.volces.com/api/v3（勿用 /responses），请求 …/api/v3/chat/completions
				                   若已含完整路径 …/chat/completions 则原样使用
				环境变量 OPENAI_* 若已设置则优先于配置文件
				模型可调用工具 get_directory_tree（本机目录树，通配 * ?）
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
			saveUserChatSettings(userSettingsFilepath, apiKey, model, baseUrl);
			Console.WriteLine("已设置 API Key 并已保存。");
			return true;
		case"/model":
			if(argument.Length == 0)
			{
				Console.WriteLine("用法：/model <模型id>");
				return true;
			}
			model = argument;
			saveUserChatSettings(userSettingsFilepath, apiKey, model, baseUrl);
			Console.WriteLine($"已设置模型：{model}（已保存）");
			return true;
		case"/url":
			if(argument.Length == 0)
			{
				Console.WriteLine("用法：/url <根地址>（无尾 /；方舟用 …/api/v3，勿填 …/responses）");
				return true;
			}
			baseUrl = argument.TrimEnd('/');
			saveUserChatSettings(userSettingsFilepath, apiKey, model, baseUrl);
			Console.WriteLine($"已设置基础地址：{baseUrl}（已保存）");
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
static void saveUserChatSettings(string filepath, string? apiKey, string model, string baseUrl)
{
	var parentDirectory = Path.GetDirectoryName(filepath);
	if(!string.IsNullOrEmpty(parentDirectory))
		Directory.CreateDirectory(parentDirectory);
	var payload = new UserChatSettings(apiKey, baseUrl.TrimEnd('/'), model);
	var jsonText = JsonSerializer.Serialize(payload, ChatJson.serializer);
	File.WriteAllText(filepath, jsonText);
}
static async Task<ChatCompletionTurnResult> sendChatCompletionTurnAsync(
	HttpClient httpClient,
	string baseUrl,
	string apiKey,
	string model,
	IReadOnlyList<PersistedChatMessage> conversation)
{
	var trimmedUrl = baseUrl.TrimEnd('/');
	string endpoint;
	if(trimmedUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
	   || trimmedUrl.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
		endpoint = trimmedUrl;
	else if(trimmedUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
		endpoint = $"{trimmedUrl}/chat/completions";
	else if(trimmedUrl.Contains("volces.com", StringComparison.OrdinalIgnoreCase)
	        && trimmedUrl.Contains("/api/v3", StringComparison.OrdinalIgnoreCase))
	{
		if(trimmedUrl.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
			trimmedUrl = trimmedUrl[..^"/responses".Length];
		endpoint = $"{trimmedUrl}/chat/completions";
	}
	else
		endpoint = $"{trimmedUrl}/v1/chat/completions";
	var payload = new JsonObject
	{
		["model"] = model,
		["messages"] = buildOpenAiMessagesArray(conversation),
		["tools"] = JsonNode.Parse(OpenAiToolRegistration.DefinitionsJson)!,
		["tool_choice"] = "auto",
		["stream"] = true,
	};
	var json = payload.ToJsonString();
	using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
	request.Headers.Authorization = new("Bearer", apiKey);
	request.Content = new StringContent(json, Encoding.UTF8, "application/json");
	using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
	if(!response.IsSuccessStatusCode)
	{
		var errorBody = await response.Content.ReadAsStringAsync();
		throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {errorBody}");
	}
	await using var bodyStream = await response.Content.ReadAsStreamAsync();
	using var lineReader = new StreamReader(bodyStream, Encoding.UTF8);
	var contentBuilder = new StringBuilder();
	var toolCallAccumulators = new Dictionary<int, StreamingToolCallAccumulator>();
	string? finishReason = null;
	while(!lineReader.EndOfStream)
	{
		var line = await lineReader.ReadLineAsync();
		if(string.IsNullOrWhiteSpace(line))
			continue;
		if(!line.StartsWith("data:", StringComparison.Ordinal))
			continue;
		var data = line["data:".Length..].TrimStart();
		if(data == "[DONE]")
			break;
		JsonDocument chunk;
		try
		{
			chunk = JsonDocument.Parse(data);
		}
		catch(JsonException)
		{
			continue;
		}
		using(chunk)
		{
			var root = chunk.RootElement;
			if(root.TryGetProperty("error", out var errorElement))
			{
				var message = errorElement.TryGetProperty("message", out var messageElement)
					? messageElement.GetString()
					: errorElement.GetRawText();
				throw new InvalidOperationException($"流式响应错误：{message}");
			}
			if(!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
				continue;
			var first = choices[0];
			if(first.TryGetProperty("finish_reason", out var finishElement)
			   && finishElement.ValueKind is JsonValueKind.String)
			{
				var reason = finishElement.GetString();
				if(!string.IsNullOrEmpty(reason))
					finishReason = reason;
			}
			if(!first.TryGetProperty("delta", out var delta))
				continue;
			if(delta.TryGetProperty("content", out var deltaContent)
			   && deltaContent.ValueKind is JsonValueKind.String)
			{
				var piece = deltaContent.GetString();
				if(!string.IsNullOrEmpty(piece))
				{
					contentBuilder.Append(piece);
					Console.Write(piece);
					Console.Out.Flush();
				}
			}
			if(delta.TryGetProperty("tool_calls", out var deltaToolCalls)
			   && deltaToolCalls.ValueKind == JsonValueKind.Array)
			{
				foreach(var callDelta in deltaToolCalls.EnumerateArray())
					mergeStreamingToolCallDelta(toolCallAccumulators, callDelta);
			}
		}
	}
	Console.WriteLine();
	Console.Out.Flush();
	var assistantText = contentBuilder.Length > 0? contentBuilder.ToString() : null;
	IReadOnlyList<AssistantToolCall>? toolCalls = null;
	if(toolCallAccumulators.Count > 0)
	{
		var ordered = toolCallAccumulators
			.OrderBy(static indexAndAccumulator => indexAndAccumulator.Key)
			.Select(static indexAndAccumulator => indexAndAccumulator.Value)
			.ToList();
		var parsedCalls = new List<AssistantToolCall>();
		foreach(var accumulator in ordered)
		{
			if(string.IsNullOrEmpty(accumulator.Id))
				continue;
			var callType = string.IsNullOrEmpty(accumulator.Type)? "function" : accumulator.Type;
			var functionName = accumulator.Name ?? string.Empty;
			var arguments = accumulator.ArgumentsBuilder.Length > 0? accumulator.ArgumentsBuilder.ToString() : "{}";
			parsedCalls.Add(new(accumulator.Id!, callType, new AssistantToolFunction(functionName, arguments)));
		}
		if(parsedCalls.Count > 0)
			toolCalls = parsedCalls;
	}
	return new(finishReason ?? "stop", assistantText, toolCalls);
}
static void mergeStreamingToolCallDelta(
	Dictionary<int, StreamingToolCallAccumulator> accumulators,
	JsonElement callDelta)
{
	if(!callDelta.TryGetProperty("index", out var indexElement)
	   || indexElement.ValueKind is not JsonValueKind.Number)
		return;
	var index = indexElement.GetInt32();
	if(!accumulators.TryGetValue(index, out var accumulator))
	{
		accumulator = new();
		accumulators[index] = accumulator;
	}
	if(callDelta.TryGetProperty("id", out var idElement) && idElement.ValueKind is JsonValueKind.String)
	{
		var id = idElement.GetString();
		if(!string.IsNullOrEmpty(id))
			accumulator.Id = id;
	}
	if(callDelta.TryGetProperty("type", out var typeElement) && typeElement.ValueKind is JsonValueKind.String)
	{
		var type = typeElement.GetString();
		if(!string.IsNullOrEmpty(type))
			accumulator.Type = type;
	}
	if(!callDelta.TryGetProperty("function", out var functionDelta))
		return;
	if(functionDelta.TryGetProperty("name", out var nameElement) && nameElement.ValueKind is JsonValueKind.String)
	{
		var name = nameElement.GetString();
		if(!string.IsNullOrEmpty(name))
			accumulator.Name = name;
	}
	if(functionDelta.TryGetProperty("arguments", out var argumentsElement)
	   && argumentsElement.ValueKind is JsonValueKind.String)
	{
		var argumentsPiece = argumentsElement.GetString();
		if(!string.IsNullOrEmpty(argumentsPiece))
			accumulator.ArgumentsBuilder.Append(argumentsPiece);
	}
}
static JsonArray buildOpenAiMessagesArray(IReadOnlyList<PersistedChatMessage> conversation)
{
	var messagesArray = new JsonArray();
	foreach(var message in conversation)
	{
		var node = new JsonObject {["role"] = message.Role};
		switch(message.Role)
		{
			case "user":
				node["content"] = message.Content ?? string.Empty;
				break;
			case "assistant":
				if(message.ToolCalls is {Count: > 0})
				{
					if(!string.IsNullOrWhiteSpace(message.Content))
						node["content"] = message.Content;
					var toolCallsArray = new JsonArray();
					foreach(var call in message.ToolCalls)
					{
						toolCallsArray.Add(
							new JsonObject
							{
								["id"] = call.Id,
								["type"] = call.Type,
								["function"] = new JsonObject
								{
									["name"] = call.Function.Name,
									["arguments"] = call.Function.Arguments,
								},
							});
					}
					node["tool_calls"] = toolCallsArray;
				}
				else
					node["content"] = message.Content ?? string.Empty;
				break;
			case "tool":
				node["tool_call_id"] = message.ToolCallId ?? string.Empty;
				node["content"] = message.Content ?? string.Empty;
				break;
		}
		messagesArray.Add(node);
	}
	return messagesArray;
}
static void writeStreamingConsole(string text)
{
	const int chunkSize = 4096;
	for(var offset = 0; offset < text.Length; offset += chunkSize)
	{
		var length = Math.Min(chunkSize, text.Length - offset);
		Console.Out.Write(text.AsSpan(offset, length));
		Console.Out.Flush();
	}
}
static string executeLocalToolCall(AssistantToolCall call)
{
	if(call.Function.Name == DirectoryTreeTool.Name)
	{
		var arguments = JsonSerializer.Deserialize<GetDirectoryTreeArguments>(call.Function.Arguments, ChatJson.serializer);
		if(arguments is null)
			return "错误：get_directory_tree 参数无法解析。";
		return DirectoryTreeTool.Invoke(arguments.Root, arguments.MaxDepth, arguments.Filter);
	}
	return $"错误：未实现的工具 {call.Function.Name}";
}
file static class OpenAiToolRegistration
{
	internal static readonly string DefinitionsJson = createDefinitionsJson();
	static string createDefinitionsJson()
	{
		var parameters = JsonNode.Parse(DirectoryTreeTool.JsonSchemaParameters)!;
		var function = new JsonObject
		{
			["name"] = DirectoryTreeTool.Name,
			["description"] = "读取本机目录树。filter 为名称通配（* ?），省略或 * 表示全部；不匹配的项会裁掉，仅保留通向匹配项的祖先目录。",
			["parameters"] = parameters,
		};
		var tool = new JsonObject {["type"] = "function", ["function"] = function};
		return new JsonArray(tool).ToJsonString();
	}
}
file sealed record UserChatSettings(
	[property: JsonPropertyName("apiKey")]string? ApiKey,
	[property: JsonPropertyName("baseUrl")]
	string BaseUrl,
	[property: JsonPropertyName("model")]string Model);
file sealed record PersistedChatMessage(
	string Role,
	string? Content = null,
	IReadOnlyList<AssistantToolCall>? ToolCalls = null,
	string? ToolCallId = null);
file sealed record AssistantToolCall(string Id, string Type, AssistantToolFunction Function);
file sealed record AssistantToolFunction(string Name, string Arguments);
file sealed record ChatCompletionTurnResult(
	string FinishReason,
	string? AssistantText,
	IReadOnlyList<AssistantToolCall>? ToolCalls);
file sealed class StreamingToolCallAccumulator
{
	internal string? Id;
	internal string? Type;
	internal string? Name;
	internal readonly StringBuilder ArgumentsBuilder = new();
}
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
