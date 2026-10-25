using SmoothLlmImposter.Application.Features.Routing.Optimization;

namespace SmoothLlmImposter.Application.UnitTest.Routing.Optimization;

public sealed class HumanSpanExtractorTests
{
    [Fact]
    public void Plain_text_is_one_human_segment()
    {
        HumanSpanExtractor.Segment[] segments = HumanSpanExtractor.Split("hello world").ToArray();

        segments.ShouldHaveSingleItem().ShouldBe(new HumanSpanExtractor.Segment("hello world", true));
    }

    [Fact]
    public void Closed_harness_span_is_excluded_and_reassembly_is_byte_identical()
    {
        const string text = "before<system-reminder>hidden</system-reminder>after";

        HumanSpanExtractor.Segment[] segments = HumanSpanExtractor.Split(text).ToArray();

        HumanSpanExtractor.ExtractHumanText(text).ShouldBe("before\nafter");
        HumanSpanExtractor.Reassemble(segments, ["BEFORE", "AFTER"])
            .ShouldBe("BEFORE<system-reminder>hidden</system-reminder>AFTER");
    }

    [Fact]
    public void Command_block_followed_by_local_output_is_excluded()
    {
        const string text = "human <command-name>/model</command-name><command-args>x</command-args><local-command-stdout>ok</local-command-stdout> tail";

        HumanSpanExtractor.ExtractHumanText(text).ShouldBe("human \n tail");
    }

    [Fact]
    public void Skill_command_block_without_local_output_is_kept()
    {
        const string text = "run <command-name>/skill</command-name><command-args>arg</command-args> now";

        HumanSpanExtractor.ExtractHumanText(text).ShouldBe(text);
    }

    [Fact]
    public void Unclosed_harness_opener_does_not_swallow_a_later_closed_block()
    {
        const string text = "<system-reminder>keep me<system-reminder>hidden</system-reminder>";

        HumanSpanExtractor.Segment[] segments = HumanSpanExtractor.Split(text).ToArray();

        HumanSpanExtractor.ExtractHumanText(text).ShouldBe("<system-reminder>keep me");
        HumanSpanExtractor.Reassemble(segments, ["<system-reminder>KEEP ME"])
            .ShouldBe("<system-reminder>KEEP ME<system-reminder>hidden</system-reminder>");
    }

    [Fact]
    public void Unclosed_command_opener_does_not_hide_a_later_valid_block()
    {
        const string text = "<command-name>broken <command-args>/skill</command-args><local-command-stdout>ok</local-command-stdout>";

        HumanSpanExtractor.Split(text).ShouldContain(segment =>
            segment.Text == "<command-name>broken " && segment.IsHuman);
        HumanSpanExtractor.Split(text).ShouldContain(segment =>
            segment.Text.Contains("<command-args>/skill</command-args>", StringComparison.Ordinal) && !segment.IsHuman);
    }

    [Fact]
    public void A_superseded_command_opener_does_not_swallow_a_later_block()
    {
        const string text = "<command-name>broken <command-name>/model</command-name><local-command-stdout>ok</local-command-stdout>";

        HumanSpanExtractor.Split(text).ShouldContain(segment =>
            segment.Text == "<command-name>broken " && segment.IsHuman);
        HumanSpanExtractor.Split(text).ShouldContain(segment =>
            segment.Text.Contains("<command-name>/model</command-name>", StringComparison.Ordinal) && !segment.IsHuman);
    }

    [Fact]
    public void Human_segments_are_joined_with_a_separator_not_concatenated()
    {
        const string text = "run <system-reminder>x</system-reminder> tests";

        HumanSpanExtractor.ExtractHumanText(text).ShouldBe("run \n tests");
    }

    [Fact]
    public void Reassembly_rejects_a_mismatched_rewrite_count()
    {
        HumanSpanExtractor.Segment[] segments =
            HumanSpanExtractor.Split("a<system-reminder>x</system-reminder>b").ToArray();

        Should.Throw<ArgumentException>(() => HumanSpanExtractor.Reassemble(segments, ["only one"]));
    }
}
