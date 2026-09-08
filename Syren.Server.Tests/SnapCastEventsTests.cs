using System.Net.WebSockets;
using System.Text;
using Syren.Server.Services;
using Xunit;

namespace Syren.Server.Tests;

public sealed class SnapCastEventsTests
{
    [Fact]
    public async Task SourceEventsTriggerReconciliationButVolumeEventsDoNot()
    {
        using var connection = new EventSocket([
            ("{\"method\":\"Stream.", false),
            ("OnUpdate\",\"params\":{\"stream\":{\"status\":\"playing\"}}}", true),
            ("{\"method\":\"Client.OnVolumeChanged\"}", true),
            ("{\"method\":42}", true),
            ("[]", true),
            ("{\"method\":\"Stream.OnUpdate\",\"params\":{\"stream\":{\"status\":\"idle\"}}}", true),
        ]);
        int changes = 0;
        await SnapCastEventsService.ObserveAsync(connection, () => changes++, CancellationToken.None);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task CancellationStopsWaitingForEvents()
    {
        using var cancellation = new CancellationTokenSource();
        using var connection = new EventSocket([]);
        cancellation.Cancel();
        await SnapCastEventsService.ObserveAsync(connection, () => Assert.Fail("Cancelled event"), cancellation.Token);
    }

    private sealed class EventSocket(IEnumerable<(string Text, bool Complete)> frames) : WebSocket
    {
        private readonly Queue<(string Text, bool Complete)> _frames = new(frames);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? description, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? description, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_frames.Count == 0) return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            var frame = _frames.Dequeue();
            byte[] bytes = Encoding.UTF8.GetBytes(frame.Text);
            bytes.AsSpan().CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, frame.Complete));
        }
    }
}
