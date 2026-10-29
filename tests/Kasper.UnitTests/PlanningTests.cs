using System.Text.Json.Nodes;
using Kasper.Application.Approvals;
using Kasper.Application.Options;
using Kasper.Application.Planning;
using Kasper.Application.Tools;
using Kasper.Domain.Entities;
using Kasper.Domain.Enums;
using Kasper.Infrastructure.Planning;
using Kasper.Infrastructure.Tools;
using Microsoft.Extensions.Options;

namespace Kasper.UnitTests;

public class PlanValidatorTests
{
    private static readonly IToolRegistry Registry = new ToolRegistry(
    [
        new GenerateTextTool(null!),
        new WriteTextFileTool(null!, null!),
        new CommitFileTool(null!, null!, null!, null!),
        new MockEchoTool(),
    ], new Monitor(new RuntimeOptions { EnableMockTools = false }));

    private static readonly HashSet<string> Folders = ["folder:1", "folder:2"];

    private static Plan SaveNote(string fileName = "note.md", string folder = "folder:1", string artifactRef = "$s1.artifact") => new("Save a note",
    [
        new PlanStep("s1", "files.write_text", "prepare", Obj(("fileName", fileName), ("content", "hi"))),
        new PlanStep("s2", "files.commit", "save", Obj(("artifact", artifactRef), ("folder", folder))),
    ]);

    private static JsonObject Obj(params (string Key, JsonNode? Value)[] props) =>
        new(props.Select(p => KeyValuePair.Create(p.Key, p.Value)));

    [Fact]
    public void Valid_plan_passes() => Assert.Empty(PlanValidator.Validate(SaveNote(), Registry, Folders));

    [Fact]
    public void Unknown_tool_is_rejected()
    {
        var plan = new Plan("x", [new PlanStep("s1", "shell.execute", "", Obj(("cmd", "del *")))]);
        Assert.Contains(PlanValidator.Validate(plan, Registry, Folders), e => e.Contains("unknown tool"));
    }

    [Fact]
    public void Disabled_mock_tools_are_rejected()
    {
        var plan = new Plan("x", [new PlanStep("s1", "mock.echo", "", Obj(("text", "hi")))]);
        Assert.Contains(PlanValidator.Validate(plan, Registry, Folders), e => e.Contains("unknown tool"));
    }

    [Fact]
    public void Model_cannot_add_parameters_like_requiresApproval()
    {
        var plan = SaveNote();
        plan.Steps[1].Input["requiresApproval"] = false;
        Assert.Contains(PlanValidator.Validate(plan, Registry, Folders), e => e.Contains("no parameter 'requiresApproval'"));
    }

    [Theory]
    [InlineData("folder:9")]
    [InlineData(@"C:\Windows")]
    [InlineData("folder:1/../..")]
    public void Invented_folders_are_rejected(string folder) =>
        Assert.Contains(PlanValidator.Validate(SaveNote(folder: folder), Registry, Folders), e => e.Contains("allowed folders"));

    [Theory]
    [InlineData(@"..\evil.md")]
    [InlineData("sub/dir.md")]
    [InlineData("CON.md")]
    [InlineData("payload.exe")]
    [InlineData("C:file.md")]
    public void Unsafe_file_names_are_rejected(string name) =>
        Assert.NotEmpty(PlanValidator.Validate(SaveNote(fileName: name), Registry, Folders));

    [Theory]
    [InlineData("$s2.artifact")] // forward reference
    [InlineData("$s9.artifact")] // unknown step
    [InlineData("$s1.path")]     // output that does not exist
    [InlineData("C:\\secret.txt")] // raw path instead of a reference
    public void Bad_references_are_rejected(string reference) =>
        Assert.NotEmpty(PlanValidator.Validate(SaveNote(artifactRef: reference), Registry, Folders));

    [Fact]
    public void Too_many_steps_are_rejected()
    {
        var steps = Enumerable.Range(1, 9).Select(i => new PlanStep($"s{i}", "ai.generate_text", "", Obj(("instructions", "x")))).ToList();
        Assert.Contains(PlanValidator.Validate(new Plan("x", steps), Registry, Folders), e => e.Contains("Too many steps"));
    }

    private sealed class Monitor(RuntimeOptions value) : IOptionsMonitor<RuntimeOptions>
    {
        public RuntimeOptions CurrentValue => value;
        public RuntimeOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<RuntimeOptions, string?> listener) => null;
    }
}

public class StepReferenceAndHashTests
{
    [Fact]
    public void References_resolve_from_earlier_outputs()
    {
        var input = new JsonObject { ["content"] = "$s1.text", ["fileName"] = "a.md" };
        var resolved = StepReferences.Resolve(input, key => key == "s1" ? new JsonObject { ["text"] = "hello" } : null);
        Assert.Equal("hello", resolved!["content"]!.GetValue<string>());
        Assert.Equal("a.md", resolved["fileName"]!.GetValue<string>());
    }

    [Fact]
    public void Unresolvable_references_return_null() =>
        Assert.Null(StepReferences.Resolve(new JsonObject { ["content"] = "$s1.text" }, _ => null));

    [Fact]
    public void Hash_ignores_key_order_but_not_content()
    {
        var a = ActionHasher.Compute("t", 1, new JsonObject { ["x"] = "1", ["y"] = "2" });
        var b = ActionHasher.Compute("t", 1, new JsonObject { ["y"] = "2", ["x"] = "1" });
        var c = ActionHasher.Compute("t", 1, new JsonObject { ["x"] = "1", ["y"] = "3" });
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Hash_changes_when_target_file_content_or_tool_version_changes()
    {
        var input = new JsonObject { ["artifact"] = "abc" };
        var original = ActionHasher.Compute("files.commit", 1, input, new Dictionary<string, string> { ["artifact"] = "aaa" });
        Assert.NotEqual(original, ActionHasher.Compute("files.commit", 1, input, new Dictionary<string, string> { ["artifact"] = "bbb" }));
        Assert.NotEqual(original, ActionHasher.Compute("files.commit", 2, input, new Dictionary<string, string> { ["artifact"] = "aaa" }));
    }
}

public class ApprovalEntityTests
{
    private static Approval New(DateTime now) =>
        Approval.Request(Guid.NewGuid(), Guid.NewGuid(), "hash", "Save", "target", "{}", "{}", now, TimeSpan.FromHours(1));

    [Fact]
    public void Cannot_decide_twice()
    {
        var now = DateTime.UtcNow;
        var a = New(now);
        a.Approve("me", now);
        Assert.Throws<InvalidOperationException>(() => a.Reject("me", now, null));
    }

    [Fact]
    public void Cannot_approve_after_expiry()
    {
        var now = DateTime.UtcNow;
        var a = New(now);
        Assert.Throws<InvalidOperationException>(() => a.Approve("me", now.AddHours(2)));
    }
}

public class PlannerParsingTests
{
    [Fact]
    public void Parses_plan_with_fences()
    {
        var (plan, reply, error) = OllamaPlanner.Parse("""
            ```json
            {"reply":"On it","summary":"Save note","steps":[{"id":"s1","tool":"mock.echo","purpose":"p","input":{"text":"hi"}}]}
            ```
            """);
        Assert.Null(error);
        Assert.Equal("On it", reply);
        Assert.Equal("mock.echo", plan!.Steps[0].ToolId);
    }

    [Fact]
    public void Empty_steps_means_just_a_reply()
    {
        var (plan, reply, error) = OllamaPlanner.Parse("""{"reply":"Miso.","steps":[]}""");
        Assert.Null(plan);
        Assert.Null(error);
        Assert.Equal("Miso.", reply);
    }

    [Fact]
    public void Garbage_is_an_error() => Assert.NotNull(OllamaPlanner.Parse("sure! here you go").Error);
}
