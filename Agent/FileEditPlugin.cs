using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.SemanticKernel;
namespace Agent;
public sealed class FileEditPlugin
{
	[KernelFunction, SuppressMessage("ReSharper", "InconsistentNaming"), Description("替换文本"),]
	// ReSharper disable once UnusedMember.Global
	public static string apply_patch(
		[Description("目标文件路径，可为绝对路径或相对当前工作目录")]
		string file_path,
		[Description("要被替换的原文片段，须在文件中出现且仅出现一次")]
		string old_text,
		[Description("替换后的内容")]string? new_text = null)
	{
		if(string.IsNullOrWhiteSpace(file_path))
			return Message("错误：file_path 不能为空。", ConsoleColor.DarkRed);
		if(old_text.Length == 0)
			return Message("错误：old_text 不能为空字符串。", ConsoleColor.DarkRed);
		var replacement = new_text ?? string.Empty;
		string fullPath;
		try { fullPath = Path.GetFullPath(file_path.Trim()); }
		catch(Exception exception) { return Message($"错误：路径无效：{exception.Message}", ConsoleColor.DarkRed); }
		if(!File.Exists(fullPath))
			return Message($"错误：文件不存在：{fullPath}", ConsoleColor.DarkRed);
		string content;
		try { content = File.ReadAllText(fullPath, Encoding.UTF8); }
		catch(IOException exception) { return Message($"错误：无法读取文件：{exception.Message}", ConsoleColor.DarkRed); }
		catch(UnauthorizedAccessException exception) { return Message($"错误：无权读取文件：{exception.Message}", ConsoleColor.DarkRed); }
		var fileOriginallyHadCrLf = content.Contains("\r\n", StringComparison.Ordinal);
		var workContent = ToLfForPatchMatching(content);
		var workOld = ToLfForPatchMatching(old_text);
		var workReplacement = ToLfForPatchMatching(replacement);
		const StringComparison comparison = StringComparison.Ordinal;
		var occurrences = CountNonOverlappingOccurrences(workContent, workOld, comparison);
		switch(occurrences)
		{
			case 0:
				return Message("错误：文件中未找到与 old_text 完全一致的片段。", ConsoleColor.DarkRed);
			case> 1:
				return Message($"错误：old_text 在文件中出现 {occurrences} 次，必须为恰好 1 次。", ConsoleColor.DarkRed);
		}
		var index = workContent.IndexOf(workOld, comparison);
		var updatedLf = string.Concat(workContent.AsSpan(0, index), workReplacement, workContent.AsSpan(index + workOld.Length));
		var updated = ToOriginalNewlinesIfCrLfFile(updatedLf, fileOriginallyHadCrLf);
		EnsureConsoleOnNewLineBeforeToolLog();
		try { File.WriteAllText(fullPath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)); }
		catch(IOException exception) { return Message($"错误：无法写入文件：{exception.Message}", ConsoleColor.DarkRed); }
		catch(UnauthorizedAccessException exception) { return Message($"错误：无权写入文件：{exception.Message}", ConsoleColor.DarkRed); }
		return Message("成功", ConsoleColor.DarkGreen);
		string Message(string messageText, ConsoleColor foreground)
		{
			EnsureConsoleOnNewLineBeforeToolLog();
			ConsoleColored.WriteLine(foreground, $"[{nameof(apply_patch)}]{messageText}");
			return messageText;
		}
	}
	static string ToLfForPatchMatching(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
	static string ToOriginalNewlinesIfCrLfFile(string lfNormalized, bool fileOriginallyHadCrLf) =>
		fileOriginallyHadCrLf ? lfNormalized.Replace("\n", "\r\n", StringComparison.Ordinal) : lfNormalized;
	static void EnsureConsoleOnNewLineBeforeToolLog()
	{
		try
		{
			if(Console.IsOutputRedirected)
				return;
			if(Console.CursorLeft != 0)
				Console.WriteLine();
		}
		catch(IOException)
		{
			/* 无控制台 */
		}
		catch(InvalidOperationException)
		{
			/* 无控制台 */
		}
	}
	static int CountNonOverlappingOccurrences(string content, string needle, StringComparison comparison)
	{
		var count = 0;
		var index = 0;
		while(true)
		{
			var found = content.IndexOf(needle, index, comparison);
			if(found < 0)
				break;
			count++;
			if(count > 1)
				break;
			index = found + needle.Length;
		}
		return count;
	}
}
