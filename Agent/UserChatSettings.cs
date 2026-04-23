using System.Text.Json;
using System.Text.Json.Serialization;
namespace Agent;
public static class UserChatSettings
{
	public static string SettingsFilepath { get; }
	public static string? ApiKey { get; set; }
	public static string Model { get; set; }
	public static string BaseUrl { get; set; }
	static UserChatSettings()
	{
		var userSettingsDirectory = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"Ethan",
			"Agent");
		SettingsFilepath = Path.Combine(userSettingsDirectory, "settings.json");
		var environmentApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
		var environmentModel = Environment.GetEnvironmentVariable("OPENAI_MODEL");
		var environmentBaseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
		ApiKey = null;
		Model = "gpt-4o-mini";
		BaseUrl = "https://api.openai.com";
		if(File.Exists(SettingsFilepath))
			try
			{
				var jsonText = File.ReadAllText(SettingsFilepath);
				var loaded = JsonSerializer.Deserialize<UserChatSettingsFile>(jsonText, ChatJson.serializer);
				if(loaded is {})
				{
					ApiKey = loaded.ApiKey;
					if(!string.IsNullOrWhiteSpace(loaded.Model))
						Model = loaded.Model;
					if(!string.IsNullOrWhiteSpace(loaded.BaseUrl))
						BaseUrl = loaded.BaseUrl.TrimEnd('/');
				}
			}
			catch(JsonException) { Console.WriteLine($"提示：配置文件格式无效，已忽略：{SettingsFilepath}"); }
		if(!string.IsNullOrEmpty(environmentApiKey))
			ApiKey = environmentApiKey;
		if(!string.IsNullOrEmpty(environmentModel))
			Model = environmentModel;
		if(!string.IsNullOrEmpty(environmentBaseUrl))
			BaseUrl = environmentBaseUrl.TrimEnd('/');
	}
	public static void Persist()
	{
		var parentDirectory = Path.GetDirectoryName(SettingsFilepath);
		if(!string.IsNullOrEmpty(parentDirectory))
			Directory.CreateDirectory(parentDirectory);
		var payload = new UserChatSettingsFile(ApiKey, BaseUrl.TrimEnd('/'), Model);
		var jsonText = JsonSerializer.Serialize(payload, ChatJson.serializer);
		File.WriteAllText(SettingsFilepath, jsonText);
	}
}
file sealed record UserChatSettingsFile(
	[property: JsonPropertyName("apiKey")]string? ApiKey,
	[property: JsonPropertyName("baseUrl")]
	string BaseUrl,
	[property: JsonPropertyName("model")]string Model);
public static class ChatJson
{
	public static readonly JsonSerializerOptions serializer = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};
}
