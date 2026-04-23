using System.IO.Enumeration;
using System.Text;
using System.Text.Json.Serialization;

internal static class DirectoryTreeTool
{
	internal const string Name = "get_directory_tree";

	internal static readonly string JsonSchemaParameters =
		"""
		{
		  "type": "object",
		  "properties": {
		    "root": { "type": "string", "description": "根目录的绝对或相对路径" },
		    "max_depth": { "type": "integer", "description": "相对根目录的最大深度：0 仅根目录一行，1 含直属子项，以此类推" },
		    "filter": {
		      "type": "string",
		      "description": "名称通配：* 与 ?，语义同 DOS；省略或 * 表示全部。未匹配的子树会裁掉；祖先目录仅在包住匹配项时保留"
		    }
		  },
		  "required": ["root", "max_depth"]
		}
		""";

	internal static string Invoke(string root, int maxDepth, string? filterPattern)
	{
		if(string.IsNullOrWhiteSpace(root))
			return "错误：root 为空。";
		if(maxDepth < 0)
			return "错误：max_depth 不能为负。";
		string fullRoot;
		try
		{
			fullRoot = Path.GetFullPath(root);
		}
		catch(Exception exception)
		{
			return $"错误：无法解析路径：{exception.Message}";
		}
		if(!Directory.Exists(fullRoot))
			return $"错误：目录不存在：{fullRoot}";
		var pattern = string.IsNullOrWhiteSpace(filterPattern)? "*" : filterPattern.Trim();
		var matchAll = pattern is "*" or "";
		try
		{
			var rootNode = FilteredTreeNode.Build(fullRoot, 0, maxDepth, pattern, matchAll);
			if(rootNode is null)
				return "(空：无任何项满足过滤)";
			var builder = new StringBuilder();
			const int maxLines = 3000;
			var linesUsed = 0;
			AppendRootLine(builder, fullRoot, ref linesUsed, maxLines, out var truncated);
			if(truncated)
			{
				builder.AppendLine("... 已截断（超过 3000 行）");
				return builder.ToString();
			}
			for(var index = 0; index < rootNode.Children.Count; index++)
			{
				if(linesUsed >= maxLines)
				{
					builder.AppendLine("... 已截断（超过 3000 行）");
					return builder.ToString();
				}
				var isLast = index == rootNode.Children.Count - 1;
				rootNode.Children[index].AppendLines(builder, string.Empty, isLast, maxLines, ref linesUsed, out truncated);
				if(truncated)
				{
					builder.AppendLine("... 已截断（超过 3000 行）");
					return builder.ToString();
				}
			}
			return builder.ToString();
		}
		catch(UnauthorizedAccessException)
		{
			return $"错误：无权限访问：{fullRoot}";
		}
		catch(IOException exception)
		{
			return $"错误：{exception.Message}";
		}
	}

	static void AppendRootLine(StringBuilder builder, string fullRoot, ref int linesUsed, int maxLines, out bool truncated)
	{
		truncated = false;
		if(linesUsed >= maxLines)
		{
			truncated = true;
			return;
		}
		var normalized = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		builder.Append(normalized).Append(Path.DirectorySeparatorChar).AppendLine();
		linesUsed++;
	}

	private sealed class FilteredTreeNode
	{
		public required string Name { get; init; }
		public required string FullPath { get; init; }
		public required bool IsDirectory { get; init; }
		public List<FilteredTreeNode> Children { get; } = [];

		public static FilteredTreeNode? Build(
			string path,
			int depthFromRoot,
			int maxDepth,
			string pattern,
			bool matchAll)
		{
			var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
			if(string.IsNullOrEmpty(name))
				name = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			var isDirectory = Directory.Exists(path);
			if(!isDirectory && File.Exists(path))
			{
				if(depthFromRoot > maxDepth)
					return null;
				if(!NameMatches(name, pattern, matchAll))
					return null;
				return new FilteredTreeNode
				{
					Name = name,
					FullPath = path,
					IsDirectory = false,
				};
			}
			if(!isDirectory)
				return null;
			var selfMatches = NameMatches(name, pattern, matchAll);
			var node = new FilteredTreeNode
			{
				Name = name,
				FullPath = path,
				IsDirectory = true,
			};
			if(depthFromRoot >= maxDepth)
				return selfMatches? node : null;
			IEnumerable<string> entries;
			try
			{
				entries = Directory.EnumerateFileSystemEntries(path);
			}
			catch
			{
				return selfMatches? node : null;
			}
			var ordered = entries.OrderBy(static entry => entry, StringComparer.OrdinalIgnoreCase).ToList();
			foreach(var entryPath in ordered)
			{
				var childDepth = depthFromRoot + 1;
				if(childDepth > maxDepth)
					continue;
				var child = Build(entryPath, childDepth, maxDepth, pattern, matchAll);
				if(child is not null)
					node.Children.Add(child);
			}
			if(node.Children.Count > 0 || selfMatches)
				return node;
			return null;
		}

		public void AppendLines(
			StringBuilder builder,
			string prefix,
			bool isLast,
			int maxLines,
			ref int linesUsed,
			out bool truncated)
		{
			truncated = false;
			if(linesUsed >= maxLines)
			{
				truncated = true;
				return;
			}
			var connector = isLast? "└── " : "├── ";
			builder.Append(prefix).Append(connector).Append(Name);
			if(IsDirectory)
				builder.Append(Path.DirectorySeparatorChar);
			builder.AppendLine();
			linesUsed++;
			if(linesUsed >= maxLines)
			{
				truncated = true;
				return;
			}
			var childPrefix = prefix + (isLast? "    " : "│   ");
			for(var index = 0; index < Children.Count; index++)
			{
				var last = index == Children.Count - 1;
				Children[index].AppendLines(builder, childPrefix, last, maxLines, ref linesUsed, out truncated);
				if(truncated)
					return;
			}
		}
	}

	static bool NameMatches(string name, string pattern, bool matchAll)
	{
		if(matchAll)
			return true;
		return FileSystemName.MatchesSimpleExpression(pattern, name, OperatingSystem.IsWindows());
	}
}

internal sealed record GetDirectoryTreeArguments(
	[property: JsonPropertyName("root")]string Root,
	[property: JsonPropertyName("max_depth")]int MaxDepth,
	[property: JsonPropertyName("filter")]string? Filter);
