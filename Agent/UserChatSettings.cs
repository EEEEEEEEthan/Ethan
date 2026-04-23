using System.Text.Json;
using System.Text.Json.Serialization;
namespace Agent;
public sealed record UserChatSettings(
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
