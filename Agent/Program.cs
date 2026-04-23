using System.Text;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using PrettyPrompt;
namespace Agent;
file static class Program
{
	static async Task Main()
	{
		var httpClient = new HttpClient {Timeout = TimeSpan.FromMinutes(5),};
		Console.OutputEncoding = Encoding.UTF8;
		SkillHolder.Rebuild();
		Console.WriteLine($"已建立技能索引：{SkillHolder.Index.Count} 条。");
		HistoryHolder.New();
		KernelHolder.Rebuild(httpClient);
		Console.WriteLine("聊天 AI。行内以 / 开头弹出斜杠补全；列表未收起时可按 Esc。");
		if(string.IsNullOrWhiteSpace(UserChatSettings.ApiKey))
			Console.WriteLine(
				$"提示：尚未设置 API Key，可用 /apikey <密钥> 或环境变量 OPENAI_API_KEY；配置保存于 {UserChatSettings.SettingsFilepath}。");
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
				if(!SlashPromptCallbacks.TryHandleLine(trimmed, httpClient))
					break;
				continue;
			}
			if(string.IsNullOrWhiteSpace(UserChatSettings.ApiKey))
			{
				Console.WriteLine("请先 /apikey <你的密钥>");
				continue;
			}
			var currentKernel = KernelHolder.Kernel;
			var turnStartCount = HistoryHolder.History.Count;
			HistoryHolder.History.AddUserMessage(trimmed);
			try
			{
				var chat = currentKernel.GetRequiredService<IChatCompletionService>();
				await RunStreamingChatTurnAsync(currentKernel, chat);
			}
			catch(Exception exception)
			{
				while(HistoryHolder.History.Count > turnStartCount)
					HistoryHolder.History.RemoveAt(HistoryHolder.History.Count - 1);
				Console.WriteLine($"请求失败：{exception.Message}");
			}
		}
	}
	static void RefreshSkillSystemMessage(
		ChatHistory chatHistory,
		IReadOnlyDictionary<string, (string Id, string Description, string Path)> index)
	{
		if(chatHistory.Count > 0 && chatHistory[0].Role == AuthorRole.System)
			chatHistory.RemoveAt(0);
		chatHistory.Insert(0, new(AuthorRole.System, SkillHolder.BuildAgentSystemPrompt(index)));
	}
	static async Task RunStreamingChatTurnAsync(Kernel kernel, IChatCompletionService chatCompletion)
	{
		RefreshSkillSystemMessage(HistoryHolder.History, SkillHolder.Index);
		var execution = new OpenAIPromptExecutionSettings
			{ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,};
		var textBuffer = new StringBuilder();
		var streamPendingCarriageReturn = false;
		var streaming = chatCompletion.GetStreamingChatMessageContentsAsync(
			chatHistory: HistoryHolder.History,
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
		return;
		static string formatTextForWindowsConsolePiece(string segment, ref bool pendingCarriageReturn)
		{
			if(segment.Length == 0)
				return string.Empty;
			if(Environment.NewLine is not"\r\n"
			   || (!pendingCarriageReturn && segment.AsSpan().IndexOfAny('\r', '\n') < 0))
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
	}
}
