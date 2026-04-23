namespace Agent;
public sealed record SkillSummary(string Id, string Description, string Path)
{
	public static IEnumerable<string> DefaultSkillRepositoryRoots()
	{
		var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if(string.IsNullOrEmpty(userProfile))
			yield break;
		yield return System.IO.Path.Combine(userProfile, ".cursor", "skills");
	}
	public static Dictionary<string, SkillSummary> BuildIndex(IEnumerable<string> skillRepositoryRoots)
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
				var skillDirectory = System.IO.Path.GetDirectoryName(skillMarkdownPath);
				if(string.IsNullOrEmpty(skillDirectory))
					continue;
				skillDirectory = System.IO.Path.GetFullPath(skillDirectory);
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
		var result = new Dictionary<string, SkillSummary>(StringComparer.OrdinalIgnoreCase);
		foreach(var entry in orderedEntries)
		{
			if(!perNameCount.TryGetValue(entry.BaseName, out var occurrence))
				occurrence = 0;
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
			result[id] = new SkillSummary(id, entry.Description, entry.SkillDirectory);
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
			if(value.Length >= 2 && value[0] == '"' && value[^1] == '"')
				value = value[1..^1];
			if(key.Equals("name", StringComparison.OrdinalIgnoreCase))
				name = value;
			else if(key.Equals("description", StringComparison.OrdinalIgnoreCase))
				description = value;
		}
		return true;
	}
}
