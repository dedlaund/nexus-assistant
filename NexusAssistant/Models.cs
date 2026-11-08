namespace NexusAssistant;

public enum Activity { Idle, Listening, Recognizing, Processing, Speaking, Error }

public sealed record ChatMessage(string Text, bool IsUser, DateTimeOffset Time)
{
    public string Author => IsUser ? "ВЫ" : "NEXUS";
    public string Clock => Time.ToLocalTime().ToString("HH:mm");
}

public enum PreferredBrowser { Firefox, Chrome, Edge, Brave, SystemDefault }

public sealed record AssistantSettings(bool Intro = true, bool Speak = false, string Model = "",
    PreferredBrowser Browser = PreferredBrowser.Firefox, string ProfileName = "", bool ClickSounds = true);

public sealed record Reply(string Text, bool Error = false);

public sealed record CommandExample(string Title, string Example, string Category);
