namespace Kasper.Domain.Enums;

/// <summary>What a tool does to the world. Decides whether Final Review is required.</summary>
public enum EffectClass
{
    /// <summary>Reads only.</summary>
    Read,
    /// <summary>Writes local files or app data (staging or committed).</summary>
    LocalWrite,
    /// <summary>Changes an external service (Notion, Slack, ...).</summary>
    ExternalWrite,
    /// <summary>Replaces or removes something.</summary>
    Destructive,
}
