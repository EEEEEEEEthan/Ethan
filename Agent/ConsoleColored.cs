namespace Agent;
static class ConsoleColored
{
	public static void WriteLine(ConsoleColor foreground, string? value)
	{
		var previousForeground = Console.ForegroundColor;
		try
		{
			Console.ForegroundColor = foreground;
			Console.WriteLine(value);
		}
		finally
		{
			Console.ForegroundColor = previousForeground;
		}
	}
	public static void Write(ConsoleColor foreground, string? value)
	{
		if(string.IsNullOrEmpty(value))
			return;
		var previousForeground = Console.ForegroundColor;
		try
		{
			Console.ForegroundColor = foreground;
			Console.Write(value);
		}
		finally
		{
			Console.ForegroundColor = previousForeground;
		}
	}
}
