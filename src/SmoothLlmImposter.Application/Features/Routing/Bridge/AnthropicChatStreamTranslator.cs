using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace SmoothLlmImposter.Application.Features.Routing.Bridge;

/// <summary>
/// Incremental Chat Completions SSE → Messages SSE translator (HLD 016 spec 03). Bounded per-stream
/// state, one held tool-call argument buffer, and a single writer for frames and timers.
/// </summary>
internal sealed class AnthropicChatStreamTranslator(ILogger<AnthropicChatStreamTranslator> logger)
{
    internal static readonly TimeSpan PingIdle = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan FinishGrace = TimeSpan.FromSeconds(2);

    private const int MaxLineBytes = 8 * 1024 * 1024;
    private const int MaxEventBytes = 8 * 1024 * 1024;
    private const int MaxToolCalls = 128;
    private const int MaxToolArgsBytes = 4 * 1024 * 1024;
    private const int MaxPendingBytes = 16 * 1024 * 1024;
    private const int ErrorMessageMaxChars = 2_000;

    public async IAsyncEnumerable<string> TranslateAsync(
        IAsyncEnumerable<string> upstreamLines,
        AnthropicChatBridgeContext context,
        TimeProvider timeProvider,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Capacity 1: the producer must not run ahead of the single writer. This keeps NFR-02 memory bounded
        // to one parsed event plus the parser's partial line, however fast the upstream is.
        var channel = Channel.CreateBounded<UpstreamEvent>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        Task producer = ProduceAsync(upstreamLines, channel.Writer, linked.Token);
        var state = new StreamState(context, timeProvider, logger);

        try
        {
            while (!state.IsTerminal)
            {
                ReadOutcome outcome = await ReadAsync(channel.Reader, state.NextWait(), timeProvider, linked.Token);

                IEnumerable<string> frames = outcome.Kind switch
                {
                    ReadOutcomeKind.Event => state.HandleEvent(outcome.Event!),
                    ReadOutcomeKind.TimedOut => state.HandleTimer(),
                    _ => state.HandleEndOfStream()
                };

                foreach (string frame in frames)
                {
                    yield return frame;
                }
            }
        }
        finally
        {
            linked.Cancel();
            try
            {
                await producer;
            }
            catch (OperationCanceledException)
            {
                // Expected when the client aborts or the terminal state stops reading.
            }
        }
    }

    // ---------------------------------------------------------------- upstream producer

    private async Task ProduceAsync(IAsyncEnumerable<string> lines, ChannelWriter<UpstreamEvent> writer, CancellationToken cancellationToken)
    {
        var eventName = "message";
        var dataLines = new List<string>();
        int eventBytes = 0;

        try
        {
            await foreach (string line in lines.WithCancellation(cancellationToken))
            {
                int lineBytes = Encoding.UTF8.GetByteCount(line);
                if (lineBytes > MaxLineBytes)
                {
                    await writer.WriteAsync(UpstreamEvent.FromLimit("Upstream event exceeded the size limit"), cancellationToken);
                    return;
                }

                if (line.Length == 0)
                {
                    if (dataLines.Count > 0)
                    {
                        await EmitEventAsync(writer, eventName, dataLines, cancellationToken);
                    }

                    eventName = "message";
                    dataLines.Clear();
                    eventBytes = 0;
                    continue;
                }

                if (line.StartsWith(':'))
                {
                    // Comment: liveness only, never starts the message.
                    continue;
                }

                int colon = line.IndexOf(':');
                string field = colon < 0 ? line : line[..colon];
                string value = colon < 0 ? string.Empty : line[(colon + 1)..];
                if (value.StartsWith(' '))
                {
                    value = value[1..];
                }

                if (field == "event")
                {
                    eventName = value;
                }
                else if (field == "data")
                {
                    eventBytes += Encoding.UTF8.GetByteCount(value);
                    if (eventBytes > MaxEventBytes)
                    {
                        await writer.WriteAsync(UpstreamEvent.FromLimit("Upstream event exceeded the size limit"), cancellationToken);
                        return;
                    }

                    dataLines.Add(value);
                }
            }

            if (dataLines.Count > 0)
            {
                await EmitEventAsync(writer, eventName, dataLines, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                await writer.WriteAsync(
                    UpstreamEvent.FromTransport(ex is TimeoutException ? "Upstream timed out" : "Upstream connection lost"),
                    cancellationToken);
            }
            catch (Exception)
            {
                // The consumer already stopped; the transport fault is moot.
            }

            return;
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static async Task EmitEventAsync(
        ChannelWriter<UpstreamEvent> writer,
        string eventName,
        List<string> dataLines,
        CancellationToken cancellationToken)
    {
        string data = string.Join('\n', dataLines).Trim();
        dataLines.Clear();
        if (data.Length == 0)
        {
            return;
        }

        if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
        {
            await writer.WriteAsync(UpstreamEvent.FromDone(), cancellationToken);
            return;
        }

        JsonObject? chunk = AnthropicChatJson.TryParseObject(data);
        if (chunk is null)
        {
            await writer.WriteAsync(UpstreamEvent.FromNoise(), cancellationToken);
            return;
        }

        bool isError = chunk["choices"] is not JsonArray &&
            (string.Equals(eventName, "error", StringComparison.Ordinal) ||
             IsNonEmptyError(chunk["error"]) ||
             string.Equals(AnthropicChatJson.String(chunk["type"]), "error", StringComparison.Ordinal));

        await writer.WriteAsync(
            isError ? UpstreamEvent.FromError(chunk) : UpstreamEvent.FromChunk(chunk),
            cancellationToken);
    }

    private static bool IsNonEmptyError(JsonNode? node) =>
        node switch
        {
            JsonObject obj => obj.Count > 0,
            JsonValue value when value.GetValueKind() == System.Text.Json.JsonValueKind.String =>
                !string.IsNullOrEmpty(value.GetValue<string>()),
            _ => false
        };

    private static async Task<ReadOutcome> ReadAsync(
        ChannelReader<UpstreamEvent> reader,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Cancel the losing operation on every read: an abandoned 8-second timer per chunk would otherwise
        // accumulate for the stream's lifetime (NFR-02 memory bound).
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ValueTask<bool> wait = reader.WaitToReadAsync(waitCts.Token);
        Task<bool> waitTask = wait.AsTask();
        Task delay = Task.Delay(timeout, timeProvider, delayCts.Token);

        Task completed = await Task.WhenAny(waitTask, delay);
        cancellationToken.ThrowIfCancellationRequested();

        if (completed == waitTask)
        {
            delayCts.Cancel();
            try
            {
                await delay;
            }
            catch (OperationCanceledException)
            {
            }

            if (!await waitTask)
            {
                return ReadOutcome.Completed();
            }

            return reader.TryRead(out UpstreamEvent? item)
                ? ReadOutcome.FromEvent(item!)
                : ReadOutcome.Completed();
        }

        waitCts.Cancel();
        try
        {
            await waitTask;
        }
        catch (OperationCanceledException)
        {
        }

        return ReadOutcome.TimedOut();
    }

    private enum ReadOutcomeKind
    {
        Event,
        TimedOut,
        Completed
    }

    private readonly record struct ReadOutcome(ReadOutcomeKind Kind, UpstreamEvent? Event)
    {
        public static ReadOutcome FromEvent(UpstreamEvent item) => new(ReadOutcomeKind.Event, item);

        public static ReadOutcome TimedOut() => new(ReadOutcomeKind.TimedOut, null);

        public static ReadOutcome Completed() => new(ReadOutcomeKind.Completed, null);
    }

    private sealed record UpstreamEvent(UpstreamEventKind Kind, JsonObject? Chunk, string? Message)
    {
        public static UpstreamEvent FromChunk(JsonObject chunk) => new(UpstreamEventKind.Chunk, chunk, null);

        public static UpstreamEvent FromError(JsonObject chunk) => new(UpstreamEventKind.Error, chunk, null);

        public static UpstreamEvent FromDone() => new(UpstreamEventKind.Done, null, null);

        public static UpstreamEvent FromNoise() => new(UpstreamEventKind.Noise, null, null);

        public static UpstreamEvent FromLimit(string message) => new(UpstreamEventKind.Limit, null, message);

        public static UpstreamEvent FromTransport(string message) => new(UpstreamEventKind.Transport, null, message);
    }

    private enum UpstreamEventKind
    {
        Chunk,
        Error,
        Done,
        Noise,
        Limit,
        Transport
    }

    // ---------------------------------------------------------------- stream state

    private sealed class StreamState
    {
        private readonly AnthropicChatBridgeContext _context;
        private readonly TimeProvider _time;
        private readonly List<CallRecord> _calls = [];
        private readonly HashSet<string> _usedIds = new(StringComparer.Ordinal);
        private readonly List<string> _frames = [];

        private long _outputUnits;
        private long _totalPendingBytes;
        private bool _started;
        private bool _done;
        private bool _failed;
        private string _msgId = string.Empty;
        private int _nextIdx;
        private BlockKind _openKind;
        private int _openIdx = -1;
        private bool _holdMode;
        private int _emittedToolUse;
        private bool _emittedAnyContent;
        private string? _finishReason;
        private bool _finishSeen;
        private DateTimeOffset _finishDeadline;
        private DateTimeOffset _lastWriteAt;
        private double? _usagePrompt;
        private double? _usageCompletion;
        private double? _usageTotal;
        private double? _usageCacheRead;
        private double? _usageCacheWrite;
        private bool _usageAfterFinish;

        private readonly ILogger _logger;

        public StreamState(AnthropicChatBridgeContext context, TimeProvider timeProvider, ILogger logger)
        {
            _context = context;
            _time = timeProvider;
            _logger = logger;
            _lastWriteAt = timeProvider.GetUtcNow();
        }

        public bool IsTerminal => _done || _failed;

        public TimeSpan NextWait()
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (!_started)
            {
                return Remaining(PingIdle - (now - _lastWriteAt));
            }

            if (_finishSeen)
            {
                return Remaining(_finishDeadline - now);
            }

            return Remaining(PingIdle - (now - _lastWriteAt));
        }

        private static TimeSpan Remaining(TimeSpan value) => value > TimeSpan.Zero ? value : TimeSpan.Zero;

        public IEnumerable<string> HandleTimer()
        {
            if (!_started)
            {
                Start();
                return Flush();
            }

            if (_finishSeen)
            {
                FinalizeMessage();
                return Flush();
            }

            Add(AnthropicChatFrames.Ping());
            return Flush();
        }

        public IEnumerable<string> HandleEndOfStream()
        {
            if (!_started)
            {
                Fail("api_error", "Upstream stream ended before completion");
                return Flush();
            }

            if (_finishSeen)
            {
                FinalizeMessage();
            }
            else
            {
                Fail("api_error", "Upstream stream ended before completion");
            }

            return Flush();
        }

        public IEnumerable<string> HandleEvent(UpstreamEvent evt)
        {
            switch (evt.Kind)
            {
                case UpstreamEventKind.Chunk:
                    ProcessChunk(evt.Chunk!);
                    break;

                case UpstreamEventKind.Error:
                {
                    AnthropicChatFailure failure = AnthropicChatStreamErrorMapper.Map(evt.Chunk!);
                    Fail(failure.Type, failure.Message);
                    break;
                }

                case UpstreamEventKind.Done:
                    if (!_started)
                    {
                        Start();
                    }
                    FinalizeMessage();
                    break;

                case UpstreamEventKind.Limit:
                case UpstreamEventKind.Transport:
                    Fail("api_error", evt.Message ?? "Upstream connection lost");
                    break;

                case UpstreamEventKind.Noise:
                    break;
            }

            return Flush();
        }

        private void ProcessChunk(JsonObject chunk)
        {
            if (!_started)
            {
                Start();
            }

            if (IsTerminal)
            {
                return;
            }

            JsonObject? choice = SelectChoice(chunk["choices"]);
            JsonObject? delta = choice?["delta"] as JsonObject ?? choice?["message"] as JsonObject;

            List<string> textPieces = ExtractTextPieces(delta);
            List<string> reasoningPieces = ExtractReasoningPieces(delta);
            List<JsonObject> toolEntries = ExtractToolEntries(delta);
            string? finishReason = NonEmpty(AnthropicChatJson.String(choice?["finish_reason"]))?.ToLowerInvariant();
            JsonObject? usage = chunk["usage"] as JsonObject ?? choice?["usage"] as JsonObject;

            if (choice?["error"] is JsonObject choiceError &&
                textPieces.Count == 0 && reasoningPieces.Count == 0 &&
                toolEntries.Count == 0 && usage is null)
            {
                AnthropicChatFailure failure = AnthropicChatStreamErrorMapper.Map(choiceError);
                Fail(failure.Type, failure.Message);
                return;
            }

            foreach (string piece in reasoningPieces)
            {
                if (AnyPending())
                {
                    CompleteAll(CompleteTrigger.Content);
                }

                if (IsTerminal)
                {
                    return;
                }

                if (_openKind != BlockKind.Thinking)
                {
                    CloseOpenBlock();
                    Open(BlockKind.Thinking);
                }

                Add(AnthropicChatFrames.ThinkingDelta(_openIdx, piece));
                _outputUnits += AnthropicChatTokenEstimator.UnitsOf(piece);
                _emittedAnyContent = true;
            }

            foreach (string piece in textPieces)
            {
                if (AnyPending())
                {
                    CompleteAll(CompleteTrigger.Content);
                }

                if (IsTerminal)
                {
                    return;
                }

                if (_openKind != BlockKind.Text)
                {
                    CloseOpenBlock();
                    Open(BlockKind.Text);
                }

                Add(AnthropicChatFrames.TextDelta(_openIdx, piece));
                _outputUnits += AnthropicChatTokenEstimator.UnitsOf(piece);
                _emittedAnyContent = true;
            }

            foreach (JsonObject entry in toolEntries)
            {
                ToolEntry(entry);
                if (IsTerminal)
                {
                    return;
                }
            }

            if (NonEmpty(finishReason) is { } reason)
            {
                _finishReason = reason;
                if (!_finishSeen)
                {
                    _finishSeen = true;
                    _finishDeadline = _time.GetUtcNow() + FinishGrace;
                }

                CompleteAll(CompleteTrigger.Finish);
                if (IsTerminal)
                {
                    return;
                }
            }

            if (usage is not null)
            {
                AnthropicChatUsageReader.Counts counts = AnthropicChatUsageReader.Read(usage);
                if (!counts.IsPlaceholder)
                {
                    MergeUsage(counts);
                    if (_finishSeen)
                    {
                        _usageAfterFinish = true;
                    }
                }
            }

            if (_finishSeen && _usageAfterFinish)
            {
                FinalizeMessage();
            }
        }

        private void MergeUsage(AnthropicChatUsageReader.Counts counts)
        {
            _usagePrompt = counts.Prompt ?? _usagePrompt;
            _usageCompletion = counts.Completion ?? _usageCompletion;
            _usageTotal = counts.Total ?? _usageTotal;
            _usageCacheRead = counts.CacheRead ?? _usageCacheRead;
            _usageCacheWrite = counts.CacheWrite ?? _usageCacheWrite;
        }

        private void ToolEntry(JsonObject entry)
        {
            JsonObject? function = entry["function"] as JsonObject;
            string? id = NonEmpty(AnthropicChatJson.String(entry["id"])) ??
                (function is not null ? NonEmpty(AnthropicChatJson.String(function["id"])) : null);
            string? name = function is not null && NonEmpty(AnthropicChatJson.String(function["name"])) is { } fname
                ? fname
                : NonEmpty(AnthropicChatJson.String(entry["name"]));

            string fragment = ExtractArgumentFragment(entry, function);
            if (id is null && name is null && fragment.Length == 0)
            {
                return;
            }

            int? index = NumericIndex(entry["index"]);
            CallRecord? candidate = index is int idx
                ? _calls.LastOrDefault(c => c.UpstreamIndex == idx)
                : _calls.LastOrDefault();

            bool isNew = candidate is null || IsNewCall(entry, candidate, id, name);

            if (isNew)
            {
                CloseOpenBlock();

                if (!_holdMode && _calls.Any(c => c.State == CallState.Pending))
                {
                    if (_calls.Where(c => c.State == CallState.Pending).All(c => c.Balanced))
                    {
                        CompleteAll(CompleteTrigger.Content);
                    }
                    else
                    {
                        _holdMode = true;
                    }
                }

                if (_calls.Count >= MaxToolCalls)
                {
                    Fail("api_error", "Upstream returned too many tool calls");
                    return;
                }

                candidate = new CallRecord(NextOrderNo(), index);
                _calls.Add(candidate);
            }

            if (candidate!.State != CallState.Pending)
            {
                _logger.LogWarning("Discarded a late tool-argument fragment for an already-emitted call");
                return;
            }

            if (id is not null && candidate.Id is null)
            {
                candidate.Id = id;
            }

            if (name is not null && candidate.Name is null)
            {
                candidate.Name = name.Length <= 64 ? name : name[..64];
            }

            if (fragment.Length > 0)
            {
                candidate.Append(fragment);
                if (candidate.ArgsBytes > MaxToolArgsBytes)
                {
                    Fail("api_error", "Upstream tool call exceeded the size limit");
                    return;
                }

                _totalPendingBytes = _calls.Where(c => c.State == CallState.Pending).Sum(c => c.ArgsBytes);
                if (_totalPendingBytes > MaxPendingBytes)
                {
                    Fail("api_error", "Upstream tool call exceeded the size limit");
                }
            }
        }

        private int NextOrderNo() => _calls.Count == 0 ? 0 : _calls[^1].OrderNo + 1;

        private static bool IsNewCall(JsonObject entry, CallRecord candidate, string? id, string? name)
        {
            if (id is not null && candidate.Id is not null &&
                !string.Equals(id, candidate.Id, StringComparison.Ordinal))
            {
                return true;
            }

            if (name is not null && candidate.Name is not null)
            {
                bool sameId = id is not null && string.Equals(id, candidate.Id, StringComparison.Ordinal);
                if (!sameId &&
                    (!string.Equals(name, candidate.Name, StringComparison.Ordinal) ||
                     candidate.State != CallState.Pending ||
                     (candidate.Args.Length > 0 && candidate.Balanced)))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ExtractArgumentFragment(JsonObject entry, JsonObject? function)
        {
            JsonNode? node = function is not null && function.ContainsKey("arguments")
                ? function["arguments"]
                : entry["arguments"];

            return node switch
            {
                JsonValue value when value.GetValueKind() == System.Text.Json.JsonValueKind.String => value.GetValue<string>(),
                JsonObject obj => AnthropicChatJson.Compact(obj),
                JsonArray array => AnthropicChatJson.Compact(array),
                _ => string.Empty
            };
        }

        private static int? NumericIndex(JsonNode? node)
        {
            if (AnthropicChatJson.Number(node) is not double value || !double.IsFinite(value))
            {
                return null;
            }

            int truncated = (int)Math.Truncate(value);
            return truncated < 0 ? 0 : truncated;
        }

        private bool AnyPending() => _calls.Any(c => c.State == CallState.Pending);

        private void CompleteAll(CompleteTrigger trigger)
        {
            bool lengthCut = trigger is CompleteTrigger.Finish or CompleteTrigger.End &&
                AnthropicChatResponseMapper.IsLengthFinishReason(_finishReason);

            var pending = _calls
                .Where(c => c.State == CallState.Pending)
                .OrderBy(c => c.UpstreamIndex ?? int.MaxValue)
                .ThenBy(c => c.OrderNo)
                .ToArray();

            foreach (CallRecord call in pending)
            {
                EmitCall(call, lengthCut);
                if (IsTerminal)
                {
                    return;
                }
            }
        }

        private void EmitCall(CallRecord call, bool lengthCut)
        {
            if (call.Name is null)
            {
                if (!_emittedAnyContent && _emittedToolUse == 0)
                {
                    Fail("api_error", "Upstream returned a tool call without a name");
                    return;
                }

                _logger.LogWarning("Dropped an upstream tool call without a usable name");
                call.State = CallState.Dropped;
                return;
            }

            string argsText = call.Args.ToString().Trim();
            if (lengthCut)
            {
                if (!AnthropicChatToolArguments.TryMapText(argsText, allowRepair: false, out string compact))
                {
                    _logger.LogWarning("Dropped tool call '{Name}' whose arguments were cut off by the length limit", call.Name);
                    call.State = CallState.Dropped;
                    return;
                }

                argsText = compact;
            }
            else if (!AnthropicChatToolArguments.TryMapText(argsText, allowRepair: true, out string compact))
            {
                Fail("api_error", $"Upstream returned invalid arguments for tool call {call.Name}");
                return;
            }
            else
            {
                argsText = compact;
            }

            string id;
            if (call.Id is not null &&
                AnthropicChatToolIdentity.IsLegalId(call.Id) &&
                _usedIds.Add(call.Id))
            {
                id = call.Id;
            }
            else
            {
                id = AnthropicChatIds.NewToolId(_usedIds);
            }

            string originalName = _context.ToolIdentity.OriginalFor(call.Name);
            int index = _nextIdx++;
            Add(AnthropicChatFrames.ToolUseStart(index, id, originalName));
            Add(AnthropicChatFrames.ToolInputDelta(index, argsText));
            Add(AnthropicChatFrames.ContentBlockStop(index));
            _emittedToolUse++;
            _emittedAnyContent = true;
            _outputUnits += AnthropicChatTokenEstimator.UnitsOf(originalName) + AnthropicChatTokenEstimator.UnitsOf(argsText);
            call.State = CallState.Emitted;
        }

        private void Start()
        {
            if (_started)
            {
                return;
            }

            _msgId = AnthropicChatIds.NewMessageId();
            Add(AnthropicChatFrames.MessageStart(_msgId, _context.InboundModel, _context.RequestTokenEstimate));
            Add(AnthropicChatFrames.Ping());
            _started = true;
        }

        private void Open(BlockKind kind)
        {
            _openKind = kind;
            _openIdx = _nextIdx++;
            Add(kind == BlockKind.Thinking
                ? AnthropicChatFrames.ThinkingStart(_openIdx)
                : AnthropicChatFrames.TextStart(_openIdx));
        }

        private void CloseOpenBlock()
        {
            if (_openKind == BlockKind.None)
            {
                return;
            }

            Add(AnthropicChatFrames.ContentBlockStop(_openIdx));
            _openKind = BlockKind.None;
            _openIdx = -1;
        }

        private void FinalizeMessage()
        {
            if (IsTerminal)
            {
                return;
            }

            CompleteAll(CompleteTrigger.End);
            if (IsTerminal)
            {
                return;
            }

            CloseOpenBlock();

            string stopReason = _emittedToolUse > 0
                ? "tool_use"
                : AnthropicChatResponseMapper.IsLengthFinishReason(_finishReason) ? "max_tokens" : "end_turn";

            (long input, long cacheCreation, long cacheRead, long output) = FinalUsage();
            Add(AnthropicChatFrames.MessageDelta(stopReason, input, cacheCreation, cacheRead, output));
            Add(AnthropicChatFrames.MessageStop());
            _done = true;
        }

        private (long Input, long CacheCreation, long CacheRead, long Output) FinalUsage()
        {
            bool promptReported = _usagePrompt is not null;
            long cacheRead = (long)(_usageCacheRead ?? 0);
            long cacheWrite = (long)(_usageCacheWrite ?? 0);
            long input = promptReported
                ? Math.Max(0, (long)_usagePrompt!.Value - cacheRead - cacheWrite)
                : _context.RequestTokenEstimate;

            long? completion = _usageCompletion is double c
                ? (long)c
                : _usageTotal is double total && _usagePrompt is double prompt
                    ? Math.Max(0, (long)(total - prompt))
                    : null;

            long output;
            if (completion is null || (completion == 0 && _emittedAnyContent))
            {
                output = Math.Max(1, (long)Math.Ceiling(_outputUnits / 4.0));
            }
            else
            {
                output = completion.Value;
            }

            return (input, cacheWrite, cacheRead, output);
        }

        private void Fail(string type, string message)
        {
            if (IsTerminal)
            {
                return;
            }

            Add(AnthropicChatFrames.Error(type, Truncate(message, ErrorMessageMaxChars)));
            _failed = true;
        }

        private static string Truncate(string text, int maxChars)
        {
            if (text.Length <= maxChars)
            {
                return text;
            }

            int cut = maxChars;
            if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
            {
                cut--;
            }

            return text[..cut];
        }

        private void Add(string frame)
        {
            _frames.Add(frame);
            _lastWriteAt = _time.GetUtcNow();
        }

        private List<string> Flush()
        {
            var frames = new List<string>(_frames);
            _frames.Clear();
            return frames;
        }

        private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

        private static JsonObject? SelectChoice(JsonNode? choices)
        {
            if (choices is not JsonArray array || array.Count == 0)
            {
                return null;
            }

            foreach (JsonNode? node in array)
            {
                if (node is JsonObject choice &&
                    AnthropicChatJson.Number(choice["index"]) is double index && index == 0)
                {
                    return choice;
                }
            }

            return array[0] as JsonObject;
        }

        private static List<string> ExtractTextPieces(JsonObject? delta)
        {
            var pieces = new List<string>();
            if (delta is null)
            {
                return pieces;
            }

            string? contentPiece = null;
            switch (delta["content"])
            {
                case JsonValue value when value.GetValueKind() == System.Text.Json.JsonValueKind.String:
                    contentPiece = NonEmpty(value.GetValue<string>());
                    break;

                case JsonArray parts:
                {
                    var builder = new StringBuilder();
                    foreach (JsonNode? part in parts)
                    {
                        if (AnthropicChatJson.String(part) is { } stringPart)
                        {
                            builder.Append(stringPart);
                        }
                        else if (part is JsonObject partObject &&
                            AnthropicChatJson.String(partObject["text"]) is { } partText)
                        {
                            builder.Append(partText);
                        }
                    }

                    contentPiece = NonEmpty(builder.ToString());
                    break;
                }
            }

            if (contentPiece is not null)
            {
                pieces.Add(contentPiece);
            }

            if (NonEmpty(AnthropicChatJson.String(delta["refusal"])) is { } refusal)
            {
                if (pieces.Count > 0)
                {
                    pieces.Add("\n");
                }

                pieces.Add(refusal);
            }

            return pieces;
        }

        private static List<string> ExtractReasoningPieces(JsonObject? delta)
        {
            var pieces = new List<string>();
            if (delta is null)
            {
                return pieces;
            }

            if (delta["reasoning_details"] is JsonArray details)
            {
                foreach (JsonNode? detail in details)
                {
                    if (AnthropicChatJson.String(detail) is { } stringPiece)
                    {
                        if (!AnthropicChatJson.IsWhiteSpaceOnly(stringPiece))
                        {
                            pieces.Add(stringPiece);
                        }
                        continue;
                    }

                    if (detail is not JsonObject entry ||
                        AnthropicChatJson.String(entry["type"]) is "reasoning.encrypted" or "redacted_thinking")
                    {
                        continue;
                    }

                    foreach (string member in (string[])["text", "reasoning", "thinking", "summary"])
                    {
                        if (NonEmpty(AnthropicChatJson.String(entry[member])) is { } piece)
                        {
                            pieces.Add(piece);
                            break;
                        }
                    }
                }
            }

            if (pieces.Count == 0)
            {
                foreach (string field in (string[])["reasoning_content", "reasoning", "thinking"])
                {
                    if (NonEmpty(AnthropicChatJson.String(delta[field])) is { } piece)
                    {
                        pieces.Add(piece);
                        break;
                    }
                }
            }

            return pieces;
        }

        private static List<JsonObject> ExtractToolEntries(JsonObject? delta)
        {
            var entries = new List<JsonObject>();
            if (delta?["tool_calls"] is not JsonArray toolCalls)
            {
                return entries;
            }

            foreach (JsonNode? node in toolCalls)
            {
                if (node is JsonObject entry)
                {
                    entries.Add(entry);
                }
            }

            return entries;
        }

        private enum BlockKind
        {
            None,
            Text,
            Thinking
        }

        private enum CompleteTrigger
        {
            Content,
            Finish,
            End
        }

        private enum CallState
        {
            Pending,
            Emitted,
            Dropped
        }

        private sealed class CallRecord(int orderNo, int? upstreamIndex)
        {
            private bool _inString;
            private bool _escape;
            private int _depth;
            private bool _startedValue;

            public int OrderNo { get; } = orderNo;

            public int? UpstreamIndex { get; } = upstreamIndex;

            public string? Id { get; set; }

            public string? Name { get; set; }

            public StringBuilder Args { get; } = new();

            public long ArgsBytes { get; private set; }

            public bool Balanced { get; private set; }

            public CallState State { get; set; } = CallState.Pending;

            public void Append(string fragment)
            {
                Args.Append(fragment);
                ArgsBytes += Encoding.UTF8.GetByteCount(fragment);

                foreach (char c in fragment)
                {
                    if (_inString)
                    {
                        if (_escape)
                        {
                            _escape = false;
                        }
                        else if (c == '\\')
                        {
                            _escape = true;
                        }
                        else if (c == '"')
                        {
                            _inString = false;
                        }

                        continue;
                    }

                    if (char.IsWhiteSpace(c))
                    {
                        continue;
                    }

                    _startedValue = true;
                    switch (c)
                    {
                        case '"':
                            _inString = true;
                            break;
                        case '{':
                        case '[':
                            _depth++;
                            break;
                        case '}':
                        case ']':
                            _depth--;
                            break;
                    }
                }

                Balanced = _startedValue && !_inString && _depth == 0;
            }
        }
    }

}
