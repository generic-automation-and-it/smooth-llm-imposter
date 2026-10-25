using SmoothLlmImposter.Application.Features.Routing.Optimization;

namespace SmoothLlmImposter.Application.UnitTest.Routing.Optimization;

public sealed class MessageRewriteValidatorTests
{
    [Fact]
    public void Accepts_a_rewrite_that_preserves_every_protected_class()
    {
        const string original =
            "Please kindly review `src/App.cs` and https://example.com/docs at 10.0.403, not /usr/bin/env " +
            "or $HOME; mention @reviewer; use Bash. See [docs](https://example.com/link).";
        const string rewrite =
            "Review `src/App.cs` and https://example.com/docs at 10.0.403, not /usr/bin/env " +
            "or $HOME; @reviewer; Bash. See [docs](https://example.com/link).";

        MessageRewriteValidator.Validate(original, rewrite, ["Bash", "Read"]).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Please keep `foo()` intact.", "Keep foo() intact.", "code")]
    [InlineData("Please visit https://example.com/a now.", "Visit example.com/a now.", "url")]
    [InlineData("Please see [docs](https://example.com/l) now.", "See docs now.", "link")]
    [InlineData("Please run /usr/bin/env now.", "Run env now.", "path")]
    [InlineData("Please read src/Features/Routing/AGENTS.md now.", "Read AGENTS.md now.", "path")]
    [InlineData("Please open C:\\temp\\file.txt now.", "Open the file now.", "path")]
    [InlineData("Please use 42 items now.", "Use items now.", "number")]
    [InlineData("Please pin 10.0.403-rc.1 now.", "Pin 10.0.403 now.", "version")]
    [InlineData("Please set LLM_MESSAGES_ENABLED now.", "Set it now.", "env")]
    [InlineData("Please do not delete this.", "Please delete this.", "negation")]
    [InlineData("Please run /model now.", "Run it now.", "slash")]
    [InlineData("Please invoke $skill now.", "Invoke it now.", "dollar")]
    [InlineData("Please ask @reviewer now.", "Ask them now.", "mention")]
    [InlineData("Please call config.value now.", "Call it now.", "identifier")]
    [InlineData("Please check user_id now.", "Check it now.", "identifier")]
    [InlineData("Please use getUser now.", "Use it now.", "identifier")]
    public void Rejects_a_rewrite_that_drops_a_protected_element(
        string original, string rewrite, string expectedKind)
    {
        MessageRewriteValidator.Result result = MessageRewriteValidator.Validate(original, rewrite);

        result.IsValid.ShouldBeFalse();
        result.FailedElements.ShouldContain(element => element.StartsWith(expectedKind + ":", StringComparison.Ordinal));
    }

    [Fact]
    public void Requires_a_catalog_name_only_when_the_original_mentions_it()
    {
        MessageRewriteValidator.Validate("Please use Bash.", "Use Bash.", ["Bash", "Read"]).IsValid.ShouldBeTrue();
        MessageRewriteValidator.Validate("Please use the Bash tool.", "Please use it.", ["Bash"])
            .FailedElements.ShouldContain("name:Bash");
    }

    [Theory]
    [InlineData("Please kindly review this entire paragraph.", "Review newValue.", "introduced-identifier")]
    [InlineData("Please kindly review this entire paragraph.", "Review /etc/passwd.", "introduced-path")]
    [InlineData("Please kindly review this entire paragraph.", "Review 42.", "introduced-number")]
    public void Rejects_introduced_elements(string original, string rewrite, string expectedPrefix)
    {
        MessageRewriteValidator.Result result = MessageRewriteValidator.Validate(original, rewrite);

        result.IsValid.ShouldBeFalse();
        result.FailedElements.ShouldContain(element => element.StartsWith(expectedPrefix + ":", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_a_changed_markdown_heading_structure()
    {
        MessageRewriteValidator.Result result = MessageRewriteValidator.Validate(
            "# Title\n\nPlease kindly review this.", "## Title\nReview this.");

        result.FailedElements.ShouldContain("structure:headings");
    }

    [Fact]
    public void Rejects_a_changed_code_fence_count()
    {
        MessageRewriteValidator.Result result = MessageRewriteValidator.Validate(
            "```\ncode\n```\nPlease kindly review this.", "```\ncode\nReview this.");

        result.FailedElements.ShouldContain("structure:fences");
    }

    [Fact]
    public void Rejects_a_changed_list_nesting()
    {
        MessageRewriteValidator.Result result = MessageRewriteValidator.Validate(
            "- one\n  - two\nPlease kindly review this.", "- one\n- two\nReview this.");

        result.FailedElements.ShouldContain("structure:list-nesting");
    }

    [Fact]
    public void Rejects_a_rewrite_that_is_not_shorter()
    {
        const string text = "Please review this.";

        MessageRewriteValidator.Validate(text, text).FailedElements.ShouldContain("length");
    }

    [Fact]
    public void Rejects_a_harness_echo_as_an_added_element()
    {
        MessageRewriteValidator.Result result = MessageRewriteValidator.Validate(
            "Please kindly review this entire paragraph.", "See <system-reminder>x</system-reminder>.");

        result.FailedElements.ShouldContain(element => element.StartsWith("added-harness:", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_an_instruction_echo()
    {
        const string instructions = "Keep all commands exactly.";
        const string original = "Please kindly keep all commands exactly as written.";
        const string rewrite = "Keep all commands exactly.";

        MessageRewriteValidator.Result result =
            MessageRewriteValidator.Validate(original, rewrite, instructionText: instructions);

        result.FailedElements.ShouldContain("added-instruction");
    }

    [Fact]
    public void Negation_matching_is_case_insensitive()
    {
        MessageRewriteValidator.Validate("Please do Not delete this.", "Do not delete this.")
            .IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task A_valid_first_rewrite_makes_no_retry()
    {
        int calls = 0;
        string result = await MessageRewriteValidator.ValidateWithRetryAsync(
            "Please review `foo()`.",
            "Review `foo()`.",
            (_, _, _) =>
            {
                calls++;
                return Task.FromResult("unused");
            },
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe("Review `foo()`.");
        calls.ShouldBe(0);
    }

    [Fact]
    public async Task An_invalid_rewrite_gets_one_targeted_retry()
    {
        int calls = 0;
        IReadOnlyList<string>? failed = null;
        string result = await MessageRewriteValidator.ValidateWithRetryAsync(
            "Please review `foo()`.",
            "Review foo().",
            (original, elements, _) =>
            {
                calls++;
                failed = elements;
                return Task.FromResult("Check `foo()`.");
            },
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe("Check `foo()`.");
        calls.ShouldBe(1);
        failed.ShouldNotBeNull();
        failed!.ShouldContain(element => element.StartsWith("code:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_retry_forwards_the_original()
    {
        int calls = 0;
        string result = await MessageRewriteValidator.ValidateWithRetryAsync(
            "Please review `foo()`.",
            "Review foo().",
            (_, _, _) =>
            {
                calls++;
                return Task.FromResult("Still foo().");
            },
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe("Please review `foo()`.");
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_throwing_retry_forwards_the_original()
    {
        string result = await MessageRewriteValidator.ValidateWithRetryAsync(
            "Please review `foo()`.",
            "Review foo().",
            (_, _, _) => throw new InvalidOperationException("transport down"),
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe("Please review `foo()`.");
    }

    [Fact]
    public async Task Caller_cancellation_propagates_from_the_retry()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            MessageRewriteValidator.ValidateWithRetryAsync(
                "Please review `foo()`.",
                "Review foo().",
                (_, _, token) => Task.FromCanceled<string>(token),
                cancellationToken: cts.Token));
    }
}
