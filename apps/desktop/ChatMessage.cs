namespace WindowsAICompanion.Desktop;

internal sealed record ChatMessage(string Role, string Content)
{
	public string Speaker => Role == "user" ? "You" : "AI";
}
