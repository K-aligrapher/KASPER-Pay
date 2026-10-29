namespace Kasper.Application.Options;

public sealed class OllamaOptions
{
    public const string Section = "Ollama";

    public string BaseUrl { get; set; } = "http://127.0.0.1:11434";

    /// <summary>Installed on the dev laptop. Switch to a general instruct model later (PRD §16).</summary>
    public string Model { get; set; } = "qwen2.5-coder:7b";

    /// <summary>Generous because the first request after idle includes loading the model into VRAM.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>How long Ollama keeps the model loaded after a request (Ollama keep_alive).</summary>
    public string KeepAlive { get; set; } = "30m";

    /// <summary>Context window passed to Ollama (num_ctx). Keep small enough for ~8 GB VRAM.</summary>
    public int ContextLength { get; set; } = 8192;
}
