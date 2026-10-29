using System.Text.Json.Nodes;
using Kasper.Application.Abstractions;
using Kasper.Application.Tools;
using Kasper.Domain.Enums;
using Kasper.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kasper.Infrastructure.Tools;

// Test-only tools (Runtime:EnableMockTools). Deterministic, no network, used to prove
// retries, approvals and duplicate prevention without real services.

public sealed class MockEchoTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "mock.echo", 1, "Test tool: returns the text it is given.", EffectClass.Read, true,
        [new ToolParameter("text", ParamType.Text, true, "Any text.")],
        ["text"], TimeSpan.FromSeconds(5), 1, true, DevOnly: true);

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct) =>
        Task.FromResult(new ToolResult(new JsonObject { ["text"] = Input.Str(input, "text") }));
}

public sealed class MockFlakyTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "mock.flaky", 1, "Test tool: fails with a temporary error on the first N attempts.", EffectClass.Read, true,
        [
            new ToolParameter("failTimes", ParamType.Integer, true, "How many attempts fail."),
            new ToolParameter("permanent", ParamType.Text, false, "\"yes\" makes the failure permanent (never retried)."),
        ],
        ["attempts"], TimeSpan.FromSeconds(5), MaxAttempts: 3, Idempotent: true, DevOnly: true);

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        if (Input.OptStr(input, "permanent") == "yes")
            throw new ToolException("mock_permanent", "Permanent test failure.", ErrorCategory.Permanent);
        if (context.Attempt <= Input.Int(input, "failTimes", 0))
            throw new ToolException("mock_transient", $"Temporary test failure on attempt {context.Attempt}.", ErrorCategory.Transient);
        return Task.FromResult(new ToolResult(new JsonObject { ["attempts"] = context.Attempt }));
    }
}

public sealed class MockSlowTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "mock.slow", 1, "Test tool: waits for the given milliseconds (honours cancellation).", EffectClass.Read, true,
        [new ToolParameter("ms", ParamType.Integer, true, "How long to wait.")],
        ["waited"], TimeSpan.FromSeconds(2), MaxAttempts: 1, Idempotent: true, DevOnly: true);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        var ms = Input.Int(input, "ms", 0);
        await Task.Delay(ms, ct);
        return new ToolResult(new JsonObject { ["waited"] = ms });
    }
}

/// <summary>Pretends to post to an external service. Stores posts in a local outbox keyed by idempotency key.</summary>
public sealed class MockExternalPostTool(KasperDbContext db, IClock clock) : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        "mock.external_post", 1, "Test tool: post a message to a fake external channel.", EffectClass.ExternalWrite, false,
        [
            new ToolParameter("channel", ParamType.Text, true, "Channel name.", 100, Editable: true),
            new ToolParameter("text", ParamType.Text, true, "Message text.", 4000, Editable: true),
        ],
        ["externalId"], TimeSpan.FromSeconds(10), MaxAttempts: 2, Idempotent: true, DevOnly: true);

    public Task<ToolPreview> PreviewAsync(ToolContext context, JsonObject input, CancellationToken ct) =>
        Task.FromResult(new ToolPreview(
            $"Post to #{Input.Str(input, "channel")} (test)",
            $"mock channel #{Input.Str(input, "channel")}",
            [new PreviewField("Channel", "#" + Input.Str(input, "channel"))],
            Input.Str(input, "text")));

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonObject input, CancellationToken ct)
    {
        // Duplicate prevention (FR-025): the idempotency key identifies this step.
        var existing = await db.MockOutbox.FirstOrDefaultAsync(o => o.IdempotencyKey == context.IdempotencyKey, ct);
        if (existing is null)
        {
            existing = new MockOutboxEntry
            {
                IdempotencyKey = context.IdempotencyKey,
                Channel = Input.Str(input, "channel"),
                Text = Input.Str(input, "text"),
                CreatedAtUtc = clock.UtcNow,
            };
            db.MockOutbox.Add(existing);
            await db.SaveChangesAsync(ct);
        }
        return new ToolResult(new JsonObject { ["externalId"] = $"mock-{existing.Id}" });
    }
}
