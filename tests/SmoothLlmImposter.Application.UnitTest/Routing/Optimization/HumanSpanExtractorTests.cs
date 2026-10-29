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

    [Fact]
    public void Pasted_signed_json_array_is_excluded_and_prose_around_it_stays_human()
    {
        const string signed = """[{"type":"thinking","thinking":"","signature":"CAQSlmwKEAgT"}]""";
        string text = $"what is this? {signed} explain";

        HumanSpanExtractor.Segment[] segments = HumanSpanExtractor.Split(text).ToArray();

        HumanSpanExtractor.ExtractHumanText(text).ShouldBe("what is this? \n explain");
        HumanSpanExtractor.Reassemble(segments, ["WHAT IS THIS? ", " EXPLAIN"])
            .ShouldBe($"WHAT IS THIS? {signed} EXPLAIN");
    }

    [Fact]
    public void Signed_object_nested_below_other_json_is_excluded_as_one_span()
    {
        const string signed = """{"content":[{"type":"thinking","signature":"sig"}],"id":1}""";

        HumanSpanExtractor.Split($"see {signed}").ShouldContain(segment =>
            segment.Text == signed && !segment.IsHuman);
    }

    [Fact]
    public void Json_without_a_signature_key_stays_human()
    {
        const string text = """config {"name":"alpha","items":[1,2]} end""";

        HumanSpanExtractor.ExtractHumanText(text).ShouldBe(text);
    }

    [Fact]
    public void Signature_key_with_a_non_string_value_stays_human()
    {
        const string text = """data [{"signature": 42}] end""";

        HumanSpanExtractor.ExtractHumanText(text).ShouldBe(text);
    }

    [Fact]
    public void Truncated_signed_json_is_excluded_to_the_end_of_the_message()
    {
        const string text = "what are these signature things?\n[{\"signature\":\"CAQSlmwKEAgT0XMaSSwH";

        HumanSpanExtractor.ExtractHumanText(text).ShouldBe("what are these signature things?\n");
    }

    [Fact]
    public void Stray_prose_bracket_before_a_balanced_signed_blob_keeps_the_prose_after_it()
    {
        const string text = "Note [see below\n[{\"signature\":\"abc\"}] explain this";

        HumanSpanExtractor.ExtractHumanText(text).ShouldBe("Note [see below\n\n explain this");
    }

    [Fact]
    public void Stray_prose_bracket_before_a_truncated_signed_blob_stays_human()
    {
        const string text = "Note [x\n[{\"signature\":\"CAQS";

        HumanSpanExtractor.ExtractHumanText(text).ShouldBe("Note [x\n");
    }

    [Fact]
    public void Quoted_signature_key_in_prose_does_not_hide_a_later_truncated_blob()
    {
        const string text = "Why does \"signature\": \"abc\" fail?\n[{\"signature\":\"CAQS";

        HumanSpanExtractor.ExtractHumanText(text).ShouldBe("Why does \"signature\": \"abc\" fail?\n");
    }

    [Fact]
    public void Disjoint_signed_blobs_are_each_excluded()
    {
        const string text = """a [{"signature":"x"}] b [{"signature":"y"}] c""";

        HumanSpanExtractor.ExtractHumanText(text).ShouldBe("a \n b \n c");
    }

    [Fact]
    public void Unclosed_json_without_a_string_signature_stays_human()
    {
        const string text = """see [{"signature": 42""";

        HumanSpanExtractor.ExtractHumanText(text).ShouldBe(text);
    }

    [Fact]
    public void Signature_word_in_prose_with_an_unclosed_bracket_stays_human()
    {
        const string text = "a [ signature here";

        HumanSpanExtractor.ExtractHumanText(text).ShouldBe(text);
    }

    [Fact]
    public void Brackets_inside_json_strings_do_not_end_the_span_early()
    {
        const string signed = """[{"signature":"a]b}c","thinking":"}"}]""";

        HumanSpanExtractor.Split($"x {signed} y").ShouldContain(segment =>
            segment.Text == signed && !segment.IsHuman);
    }

    [Fact]
    public void Stray_quote_in_prose_does_not_hide_a_signed_array_on_a_later_line()
    {
        const string signed = """[{"signature":"sig"}]""";
        string text = $"it's a \"quote\n{signed}";

        HumanSpanExtractor.Split(text).ShouldContain(segment =>
            segment.Text == signed && !segment.IsHuman);
    }
}
