using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.SemanticKernel;
namespace Agent;
public sealed class SkillLearningPlugin(Dictionary<string, (string Id, string Description, string Path)> skillIndex)
{
	const string defaultRelativeFile = "SKILL.md";
	static void EnsureConsoleOnNewLineBeforeToolLog()
	{
		try
		{
			if(Console.IsOutputRedirected)
				return;
			if(Console.CursorLeft != 0)
				Console.WriteLine();
		}
		catch(IOException) { /* 无控制台 */ }
		catch(InvalidOperationException) { /* 无控制台 */ }
	}
	static bool TryResolveUnderRoot(string skillRootFull, string relativePath, out string absoluteFile, out string error)
	{
		absoluteFile = string.Empty;
		error = string.Empty;
		var combined = Path.GetFullPath(Path.Combine(skillRootFull, relativePath));
		var rootWithSep = skillRootFull.EndsWith(Path.DirectorySeparatorChar)
			? skillRootFull
			: skillRootFull + Path.DirectorySeparatorChar;
		if(!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
		   && !string.Equals(combined, skillRootFull, StringComparison.OrdinalIgnoreCase))
		{
			error = "错误：路径越界，必须位于技能根目录内。";
			return false;
		}
		if(Directory.Exists(combined))
		{
			error = "错误：目标是目录而非文件。";
			return false;
		}
		if(!File.Exists(combined))
		{
			error = $"错误：文件不存在：{relativePath}";
			return false;
		}
		absoluteFile = combined;
		return true;
	}
	static string FormatDirectoryListing(string skillRootFull)
	{
		if(!Directory.Exists(skillRootFull))
			return"(无法列出：根目录不存在)";
		try
		{
			var lines = Directory
				.EnumerateFileSystemEntries(skillRootFull, "*", SearchOption.AllDirectories)
				.Select(fullPath => Path.GetRelativePath(skillRootFull, fullPath))
				.OrderBy(static relative => relative, StringComparer.OrdinalIgnoreCase)
				.ToArray();
			return lines.Length == 0? "(空目录)" : string.Join(Environment.NewLine, lines);
		}
		catch(IOException exception) { return$"(列出失败：{exception.Message})"; }
		catch(UnauthorizedAccessException exception) { return$"(列出失败：{exception.Message})"; }
	}
	static bool TryConfigureScriptLaunch(string absoluteScriptPath, string skillRootFull, string[] tailArguments, out ProcessStartInfo startInfo, out string error)
	{
		startInfo = new()
		{
			WorkingDirectory = skillRootFull,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		error = string.Empty;
		var extension = Path.GetExtension(absoluteScriptPath);
		switch(extension.ToLowerInvariant())
		{
		case".ps1":
			startInfo.FileName = "powershell.exe";
			startInfo.ArgumentList.Add("-NoProfile");
			startInfo.ArgumentList.Add("-ExecutionPolicy");
			startInfo.ArgumentList.Add("Bypass");
			startInfo.ArgumentList.Add("-File");
			startInfo.ArgumentList.Add(absoluteScriptPath);
			foreach(var segment in tailArguments)
				startInfo.ArgumentList.Add(segment);
			return true;
		case".cmd":
		case".bat":
			startInfo.FileName = "cmd.exe";
			startInfo.ArgumentList.Add("/c");
			startInfo.ArgumentList.Add(absoluteScriptPath);
			foreach(var segment in tailArguments)
				startInfo.ArgumentList.Add(segment);
			return true;
		case".py":
			startInfo.FileName = "python";
			startInfo.ArgumentList.Add(absoluteScriptPath);
			foreach(var segment in tailArguments)
				startInfo.ArgumentList.Add(segment);
			return true;
		case".js":
			startInfo.FileName = "node";
			startInfo.ArgumentList.Add(absoluteScriptPath);
			foreach(var segment in tailArguments)
				startInfo.ArgumentList.Add(segment);
			return true;
		case".sh":
			startInfo.FileName = "bash";
			startInfo.ArgumentList.Add(absoluteScriptPath);
			foreach(var segment in tailArguments)
				startInfo.ArgumentList.Add(segment);
			return true;
		case".exe":
			startInfo.FileName = absoluteScriptPath;
			foreach(var segment in tailArguments)
				startInfo.ArgumentList.Add(segment);
			return true;
		default:
			error = $"错误：不支持的脚本扩展名「{extension}」。支持：.ps1 .bat .cmd .py .js .sh .exe";
			return false;
		}
	}
	[KernelFunction]
	[SuppressMessage("ReSharper", "InconsistentNaming")]
	[Description("读取技能")]
	// ReSharper disable once UnusedMember.Global
	public string LearnSkill(
		[Description("技能id，与系统消息中列表一致")]string skill_id,
		[Description("相对技能根目录的文件路径，缺省表示 SKILL.md")]
		string? relative_path = null)
	{
		if(string.IsNullOrWhiteSpace(skill_id))
			return"错误：skill_id 不能为空。";
		if(!skillIndex.TryGetValue(skill_id.Trim(), out var summary))
			return$"错误：未找到技能 id「{skill_id}」。请使用系统消息中列出的 id。";
		EnsureConsoleOnNewLineBeforeToolLog();
		Console.WriteLine($"[learn {skill_id} {relative_path}]");
		var skillRoot = Path.GetFullPath(summary.Path);
		var useImplicitDefault = string.IsNullOrWhiteSpace(relative_path);
		var relativeSegment = useImplicitDefault? defaultRelativeFile : relative_path!.Trim().TrimStart('/', '\\');
		if(string.IsNullOrEmpty(relativeSegment))
			relativeSegment = defaultRelativeFile;
		if(!TryResolveUnderRoot(skillRoot, relativeSegment, out var absoluteFile, out var resolveError))
			return resolveError;
		string fileText;
		try { fileText = File.ReadAllText(absoluteFile, Encoding.UTF8); }
		catch(IOException exception) { return$"错误：无法读取文件：{exception.Message}"; }
		catch(UnauthorizedAccessException exception) { return$"错误：无权读取文件：{exception.Message}"; }
		if(!useImplicitDefault)
			return fileText;
		var builder = new StringBuilder();
		builder.AppendLine("### 技能目录结构（相对技能根目录）");
		builder.AppendLine(FormatDirectoryListing(skillRoot));
		builder.AppendLine($"### {relativeSegment} 全文");
		builder.AppendLine(fileText);
		builder.AppendLine();
		return builder.ToString();
	}
	[KernelFunction]
	[SuppressMessage("ReSharper", "InconsistentNaming")]
	[Description("在技能根目录作为工作目录下执行技能包内脚本，标准输出与标准错误合并返回。支持 .ps1 .bat .cmd .py .js .sh .exe")]
	public async Task<string> RunSkillScript(
		[Description("技能 id，与系统消息中列表一致")]string skill_id,
		[Description("相对技能根目录的脚本文件路径")]string relative_path,
		[Description("可选：按顺序传给脚本的命令行参数")]string[]? script_args = null)
	{
		if(string.IsNullOrWhiteSpace(skill_id))
			return"错误：skill_id 不能为空。";
		if(string.IsNullOrWhiteSpace(relative_path))
			return"错误：relative_path 不能为空。";
		if(!skillIndex.TryGetValue(skill_id.Trim(), out var summary))
			return$"错误：未找到技能 id「{skill_id}」。请使用系统消息中列出的 id。";
		EnsureConsoleOnNewLineBeforeToolLog();
		Console.WriteLine($"[run_skill_script {skill_id} {relative_path}]");
		var skillRoot = Path.GetFullPath(summary.Path);
		var relativeSegment = relative_path.Trim().TrimStart('/', '\\');
		if(string.IsNullOrEmpty(relativeSegment))
			return"错误：relative_path 无效。";
		if(!TryResolveUnderRoot(skillRoot, relativeSegment, out var absoluteScript, out var resolveError))
			return resolveError;
		var tailArguments = script_args is {Length: > 0}? script_args : Array.Empty<string>();
		if(!TryConfigureScriptLaunch(absoluteScript, skillRoot, tailArguments, out var startInfo, out var launchError))
			return launchError;
		using var process = new Process {StartInfo = startInfo,};
		try { process.Start(); }
		catch(Exception exception) { return$"错误：无法启动进程：{exception.Message}"; }
		var readStandardOutput = process.StandardOutput.ReadToEndAsync();
		var readStandardError = process.StandardError.ReadToEndAsync();
		var waitForExit = process.WaitForExitAsync();
		try
		{
			await Task.WhenAll(readStandardOutput, readStandardError, waitForExit)
				.WaitAsync(TimeSpan.FromMinutes(5))
				.ConfigureAwait(false);
		}
		catch(TimeoutException)
		{
			try { process.Kill(entireProcessTree: true); }
			catch { /* 忽略 */ }
			return"错误：脚本执行超过 5 分钟已终止。";
		}
		var standardOutputText = await readStandardOutput.ConfigureAwait(false);
		var standardErrorText = await readStandardError.ConfigureAwait(false);
		var builder = new StringBuilder();
		builder.AppendLine($"退出码：{process.ExitCode}");
		if(standardOutputText.Length > 0)
		{
			builder.AppendLine("--- stdout ---");
			builder.AppendLine(standardOutputText);
		}
		if(standardErrorText.Length > 0)
		{
			builder.AppendLine("--- stderr ---");
			builder.AppendLine(standardErrorText);
		}
		return builder.ToString();
	}
}
