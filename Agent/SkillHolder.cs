using System.Text;
namespace Agent;
static class SkillHolder
{
	public static Dictionary<string, (string Id, string Description, string Path)> Index { get { return field ??= BuildIndex(DefaultSkillRepositoryRoots); } private set; }
	static IEnumerable<string> DefaultSkillRepositoryRoots
	{
		get
		{
			var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			if(string.IsNullOrEmpty(userProfile))
				yield break;
			yield return Path.Combine(userProfile, ".cursor", "skills");
			yield return Path.Combine(userProfile, ".ethan", "skills");
		}
	}
	public static void Rebuild() { Index = null!; }
	public static string BuildAgentSystemPrompt(
		IReadOnlyDictionary<string, (string Id, string Description, string Path)> index)
	{
		var builder = new StringBuilder();
		builder.AppendLine("可用技能（id 与摘要），新对话请据此选用；需要细则时请调用 learn_skill。");
		foreach(var pair in index.OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase))
			builder.AppendLine($"- {pair.Key}: {pair.Value.Description}");
		builder.AppendLine();
		builder.AppendLine(
			"工具 learn_skill(skill_id, relative_path?)：relative_path 为相对技能根目录的文件路径；不传时读取 SKILL.md 全文，并附带该技能目录下相对路径列表；传入路径时只返回该文件的完整 UTF-8 文本。");
		return builder.ToString();
	}
	static Dictionary<string, (string Id, string Description, string Path)> BuildIndex(
		IEnumerable<string> skillRepositoryRoots)
	{
		var orderedEntries = new List<(string SkillDirectory, string BaseName, string Description)>();
		foreach(var root in skillRepositoryRoots)
		{
			if(string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
				continue;
			foreach(var skillMarkdownPath in Directory
				        .EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories)
				        .OrderBy(static fullPath => fullPath, StringComparer.OrdinalIgnoreCase))
			{
				var skillDirectory = Path.GetDirectoryName(skillMarkdownPath);
				if(string.IsNullOrEmpty(skillDirectory))
					continue;
				skillDirectory = Path.GetFullPath(skillDirectory);
				var parsed = TryParseFrontmatter(skillMarkdownPath, out var parsedName, out var parsedDescription);
				string baseName;
				string description;
				if(parsed && !string.IsNullOrWhiteSpace(parsedName))
				{
					baseName = parsedName.Trim();
					description = parsedDescription.Trim();
				}
				else
				{
					baseName = new DirectoryInfo(skillDirectory).Name;
					description = parsed? parsedDescription.Trim() : string.Empty;
				}
				orderedEntries.Add((skillDirectory, baseName, description));
			}
		}
		var perNameCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		var result = new Dictionary<string, (string Id, string Description, string Path)>(
			StringComparer.OrdinalIgnoreCase);
		foreach(var entry in orderedEntries)
		{
			var occurrence = perNameCount.GetValueOrDefault(entry.BaseName, 0);
			occurrence++;
			perNameCount[entry.BaseName] = occurrence;
			var id = occurrence == 1? entry.BaseName : $"{entry.BaseName}{occurrence}";
			var folderName = new DirectoryInfo(entry.SkillDirectory).Name;
			var disambiguator = 0;
			while(result.ContainsKey(id))
			{
				disambiguator++;
				id = disambiguator == 1
					? $"{entry.BaseName}{occurrence}__{folderName}"
					: $"{entry.BaseName}{occurrence}__{folderName}_{disambiguator}";
			}
			result[id] = (id, entry.Description, entry.SkillDirectory);
			Console.WriteLine($"import {id}");
		}
		return result;
	}
	static bool TryParseFrontmatter(string skillMarkdownPath, out string name, out string description)
	{
		name = string.Empty;
		description = string.Empty;
		string[] lines;
		try { lines = File.ReadAllLines(skillMarkdownPath); }
		catch(IOException) { return false; }
		catch(UnauthorizedAccessException) { return false; }
		if(lines.Length == 0 || !lines[0].Trim().Equals("---", StringComparison.Ordinal))
			return false;
		for(var lineIndex = 1; lineIndex < lines.Length; lineIndex++)
		{
			var line = lines[lineIndex];
			if(line.Trim().Equals("---", StringComparison.Ordinal))
				break;
			var colonIndex = line.IndexOf(':');
			if(colonIndex <= 0)
				continue;
			var key = line[..colonIndex].Trim();
			var value = line[(colonIndex + 1)..].Trim();
			if(value is ['"', _, ..,] && value[^1] == '"')
				value = value[1..^1];
			if(key.Equals("name", StringComparison.OrdinalIgnoreCase))
				name = value;
			else if(key.Equals("description", StringComparison.OrdinalIgnoreCase))
				description = value;
		}
		return true;
	}
}
