using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace MeleMcp.Core.Tests;

/// <summary>
/// The pipe client's side of a request's life (LiveBridge.Exchange), against a stand-in for MeleBridge's end of the pipe: each
/// request carries an id of its own and only its reply is taken, a request the bridge answers as still running or never answers
/// is reported as maybe done, and a batch that stops part-way says which requests were answered and which were never sent.
/// </summary>
[TestClass]
public class PipeStateTests
{
    /// <summary>Short, so a request the stand-in never answers fails fast.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(300);

    private static JsonObject Call(string function, params JsonNode?[] args) =>
        new() { ["op"] = "call", ["object"] = "BIOG_V_Plot.BioGlobalVariableTable_0", ["function"] = function, ["args"] = new JsonArray(args) };

    private static string Reply(JsonObject request, JsonObject body)
    {
        if (request["id"] is { } id) body["id"] = id.DeepClone();
        return body.ToJsonString();
    }

    private static string Ok(JsonObject request, int value) => Reply(request, new JsonObject { ["ok"] = true, ["return"] = value });

    [TestMethod]
    public void Exchange_GivesEachRequestItsOwnId_AndStripsIdAndOk()
    {
        var bridge = new FakeBridge((r, _) => [Ok(r, 7)]);
        var replies = LiveBridge.Exchange(bridge, [Call("GetInt", 1), Call("GetInt", 2), Call("GetInt", 3)], 5, Wait);

        var ids = bridge.Requests.Select(r => r["id"]!.GetValue<long>()).ToList();
        Assert.HasCount(3, ids);
        Assert.HasCount(3, ids.Distinct());
        Assert.IsTrue(bridge.Requests.All(r => r["timeoutMs"]!.GetValue<int>() == 5000));
        Assert.IsTrue(replies.All(r => !r.ContainsKey("id") && !r.ContainsKey("ok") && r["return"]!.GetValue<int>() == 7));
    }

    /// <summary>A line that answers another request (a late reply) is skipped, not taken for this request's answer.</summary>
    [TestMethod]
    public void Exchange_SkipsAReplyWithAnotherId()
    {
        var bridge = new FakeBridge((r, _) =>
        [
            new JsonObject { ["ok"] = true, ["return"] = 1, ["id"] = r["id"]!.GetValue<long>() - 1000 }.ToJsonString(),
            Ok(r, 2),
        ]);
        var reply = LiveBridge.Exchange(bridge, [Call("GetInt", 1)], 5, Wait).Single();
        Assert.AreEqual(2, reply["return"]!.GetValue<int>());
    }

    /// <summary>A reply without an id (the error reply to a line the bridge could not parse) is still this request's.</summary>
    [TestMethod]
    public void Exchange_TakesAReplyWithoutId()
    {
        var bridge = new FakeBridge((_, _) => [new JsonObject { ["ok"] = true, ["return"] = 3 }.ToJsonString()]);
        var reply = LiveBridge.Exchange(bridge, [Call("GetInt", 1)], 5, Wait).Single();
        Assert.AreEqual(3, reply["return"]!.GetValue<int>());
    }

    /// <summary>
    /// MeleBridge 5's answer for a request still running past its timeout is no refusal: it may still take effect. It reads as a
    /// missing reply does ("did not answer", which scenario.py takes for a reply lost to a load).
    /// </summary>
    [TestMethod]
    public void Exchange_RunningReply_IsAnIOExceptionThatSaysItMayStillComplete()
    {
        var bridge = new FakeBridge((r, _) => [Reply(r, new JsonObject { ["ok"] = false, ["running"] = true, ["error"] = "the game thread did not answer the request in time" })]);
        var request = new JsonObject { ["op"] = "set", ["object"] = "BIOA_MYM01.TheWorld.PersistentLevel.BioPawn_3", ["property"] = "Location", ["value"] = null };

        var e = Assert.ThrowsExactly<IOException>(() => LiveBridge.Exchange(bridge, [request], 5, Wait));
        StringAssert.Contains(e.Message, "did not answer 'set BioPawn_3.Location' in time");
        StringAssert.Contains(e.Message, "may still complete");
    }

    [TestMethod]
    public void Exchange_NoReply_IsAnIOExceptionThatSaysItMayHaveStarted()
    {
        var bridge = new FakeBridge((_, _) => []);
        var e = Assert.ThrowsExactly<IOException>(() => LiveBridge.Exchange(bridge, [new JsonObject { ["op"] = "console", ["command"] = "stat fps" }], 5, Wait));
        StringAssert.Contains(e.Message, "did not answer 'console stat fps' within");
        StringAssert.Contains(e.Message, "may have started in the game");
    }

    /// <summary>game_plot's set then read-back: the read-back unanswered must not hide that the set was answered.</summary>
    [TestMethod]
    public void Exchange_NoReplyToTheSecondOfThree_NamesTheAnsweredAndTheNeverSent()
    {
        var bridge = new FakeBridge((r, n) => n == 2 ? [] : [Ok(r, 0)]);
        var requests = new List<JsonObject> { Call("SetBool", 8421, true, false), Call("GetBool", 8421), Call("GetBool", 8422) };

        var e = Assert.ThrowsExactly<IOException>(() => LiveBridge.Exchange(bridge, requests, 5, Wait));
        Assert.HasCount(2, bridge.Requests, "the request after the unanswered one is never sent");
        StringAssert.Contains(e.Message, "That was request 2 of 3 ('call BioGlobalVariableTable_0.GetBool [8421]')");
        StringAssert.Contains(e.Message, "answered before it: 'call BioGlobalVariableTable_0.SetBool [8421,true,false]'");
        StringAssert.Contains(e.Message, "never sent: 'call BioGlobalVariableTable_0.GetBool [8422]'");
    }

    /// <summary>A refusal keeps its type (callers catch InvalidOperationException for one) and its words, first.</summary>
    [TestMethod]
    public void Exchange_RefusalInABatch_KeepsTypeAndText_AndNamesTheOthers()
    {
        var bridge = new FakeBridge((r, n) => n == 1 ? [Ok(r, 0)] : [Reply(r, new JsonObject { ["ok"] = false, ["error"] = "no object at X (paths as find returns them)" })]);
        var requests = new List<JsonObject> { new() { ["op"] = "console", ["command"] = "stat fps" }, new() { ["op"] = "console", ["command"] = "flush" } };

        var e = Assert.ThrowsExactly<InvalidOperationException>(() => LiveBridge.Exchange(bridge, requests, 5, Wait));
        StringAssert.StartsWith(e.Message, "no object at X (paths as find returns them). That was request 2 of 2 ('console flush')");
        StringAssert.Contains(e.Message, "answered before it: 'console stat fps'; never sent: none.");
    }

    /// <summary>One request alone fails with the bridge's own words, as before batches named their requests.</summary>
    [TestMethod]
    public void Exchange_SingleRefusal_IsTheBridgesMessage()
    {
        var bridge = new FakeBridge((r, _) => [Reply(r, new JsonObject { ["ok"] = false, ["error"] = "no object at X (paths as find returns them)" })]);
        var e = Assert.ThrowsExactly<InvalidOperationException>(() => LiveBridge.Exchange(bridge, [Call("GetInt", 1)], 5, Wait));
        Assert.AreEqual("no object at X (paths as find returns them)", e.Message);
    }

    /// <summary>The client waits past the bridge's own answer: timeoutMs plus RunningGrace (1.5 s, GameThread.cpp), with margin.</summary>
    [TestMethod]
    public void ReplyWait_OutlastsTheBridgesRunningAnswer()
    {
        Assert.IsTrue(LiveBridge.ReplyWait(5) >= TimeSpan.FromSeconds(5 + 1.5 + 1), LiveBridge.ReplyWait(5).ToString());
    }

    /// <summary>
    /// MeleBridge's end of the pipe: each request line written gets the reply lines <paramref name="answer"/> gives for it and its
    /// 1-based number; none means it never answers. Reads wait for a reply line, and stop when the reader's time is up.
    /// </summary>
    private sealed class FakeBridge(Func<JsonObject, int, IEnumerable<string>> answer) : Stream
    {
        private readonly Channel<byte[]> replies = Channel.CreateUnbounded<byte[]>();
        private readonly List<byte> line = [];
        private ReadOnlyMemory<byte> unread = ReadOnlyMemory<byte>.Empty;

        public List<JsonObject> Requests { get; } = [];

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            foreach (var b in buffer)
            {
                if (b != (byte)'\n')
                {
                    line.Add(b);
                    continue;
                }
                var request = JsonNode.Parse(Encoding.UTF8.GetString([.. line]))!.AsObject();
                line.Clear();
                Requests.Add(request);
                foreach (var reply in answer(request, Requests.Count)) replies.Writer.TryWrite(Encoding.UTF8.GetBytes(reply + "\n"));
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (unread.IsEmpty) unread = await replies.Reader.ReadAsync(cancellationToken);
            var n = Math.Min(buffer.Length, unread.Length);
            unread[..n].CopyTo(buffer);
            unread = unread[n..];
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
