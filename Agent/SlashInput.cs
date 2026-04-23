using System.Text.Json;
using Microsoft.SemanticKernel.ChatCompletion;
using PrettyPrompt;
using PrettyPrompt.Completion;
using PrettyPrompt.Consoles;
using PrettyPrompt.Documents;
using PrettyPrompt.Highlighting;
namespace Agent;
public sealed class SlashPromptCallbacks: PromptCallbacks
{
	const string completeApikey = "/apikey ";
	const string completeModel = "/model ";
	const string completeUrl = "/url ";
	const string completeClear = "/clear";
	const string completeUpdateSkills = "/update-skills";
	const string completeHelp = "/help";
	const string completeQ = "/?";
	const string completeExit = "/exit";
	const string completeQuit = "/quit";
	const string captionApikey = "设置 API Key";
	const string captionModel = "设置模型 id";
	const string captionUrl = "OpenAI 兼容根地址";
	const string captionClear = "清空对话上下文";
	const string captionUpdateSkills = "重建技能索引";
	const string captionHelp = "指令说明";
	const string captionQ = "同 /help";
	const string captionExit = "退出";
	const string captionQuit = "退出";
	static readonly CompletionItem[] slashCompletionItems =
	[
		new SlashCompletionItem(completeApikey, captionApikey),
		new SlashCompletionItem(completeModel, captionModel),
		new SlashCompletionItem(completeUrl, captionUrl),
		new SlashCompletionItem(completeClear, captionClear),
		new SlashCompletionItem(completeUpdateSkills, captionUpdateSkills),
		new SlashCompletionItem(completeHelp, captionHelp),
		new SlashCompletionItem(completeQ, captionQ),
		new SlashCompletionItem(completeExit, captionExit),
		new SlashCompletionItem(completeQuit, captionQuit),
	];
	public static bool TryHandleLine(
		string trimmed,
		string userSettingsFilepath,
		ref string? apiKey,
		ref string model,
		ref string baseUrl,
		Dictionary<string, SkillSummary> skillIndex,
		ChatHistory conversation,
		out bool connectionSettingsTouched)
	{
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
				SaveUserSettings(userSettingsFilepath, apiKey, model, baseUrl);
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
				SaveUserSettings(userSettingsFilepath, apiKey, model, baseUrl);
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
				SaveUserSettings(userSettingsFilepath, apiKey, model, baseUrl);
				Console.WriteLine($"已设置基础地址：{baseUrl}（已保存）");
				return true;
			case"/clear":
				conversation.Clear();
				Console.WriteLine("已清空对话。");
				return true;
			case"/update-skills":
			{
				var rebuilt = SkillSummary.BuildIndex(SkillSummary.DefaultSkillRepositoryRoots());
				skillIndex.Clear();
				foreach(var pair in rebuilt)
					skillIndex[pair.Key] = pair.Value;
				Console.WriteLine($"已重建技能索引：{skillIndex.Count} 条。");
				return true;
			}
			case"/exit":
			case"/quit":
				return false;
			default:
				Console.WriteLine($"未知指令：{command}，输入 /help 查看列表。");
				return true;
		}
	}
	static void SaveUserSettings(
		string filepath,
		string? apiKeyValue,
		string modelId,
		string userBase)
	{
		var parentDirectory = Path.GetDirectoryName(filepath);
		if(!string.IsNullOrEmpty(parentDirectory))
			Directory.CreateDirectory(parentDirectory);
		var payload = new UserChatSettings(apiKeyValue, userBase.TrimEnd('/'), modelId);
		var jsonText = JsonSerializer.Serialize(payload, ChatJson.serializer);
		File.WriteAllText(filepath, jsonText);
	}
	static int GetTokenStartIndex(string text, int caret)
	{
		var limit = caret > 0? caret - 1 : -1;
		for(var index = limit; index >= 0; index--)
			if(text[index] is' ' or'\t')
				return index + 1;
		return 0;
	}
	protected override Task<TextSpan> GetSpanToReplaceByCompletionAsync(
		string text,
		int caret,
		CancellationToken cancellationToken)
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
