using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace ImajinationAPI.Services
{
    public sealed class BookingMessageStreamService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<string>>> _subscriptions = new();

        public BookingMessageSubscription Subscribe(Guid bookingId)
        {
            var subscriptionId = Guid.NewGuid();
            var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            var bookingChannels = _subscriptions.GetOrAdd(bookingId, static _ => new ConcurrentDictionary<Guid, Channel<string>>());
            bookingChannels[subscriptionId] = channel;

            return new BookingMessageSubscription(subscriptionId, channel.Reader, () => RemoveSubscription(bookingId, subscriptionId));
        }

        public ValueTask PublishAsync(Guid bookingId, object payload, CancellationToken cancellationToken = default)
        {
            if (!_subscriptions.TryGetValue(bookingId, out var bookingChannels) || bookingChannels.IsEmpty)
            {
                return ValueTask.CompletedTask;
            }

            var serializedPayload = JsonSerializer.Serialize(payload, JsonOptions);
            foreach (var channel in bookingChannels.Values)
            {
                channel.Writer.TryWrite(serializedPayload);
            }

            return ValueTask.CompletedTask;
        }

        private void RemoveSubscription(Guid bookingId, Guid subscriptionId)
        {
            if (!_subscriptions.TryGetValue(bookingId, out var bookingChannels))
            {
                return;
            }

            if (bookingChannels.TryRemove(subscriptionId, out var channel))
            {
                channel.Writer.TryComplete();
            }

            if (bookingChannels.IsEmpty)
            {
                _subscriptions.TryRemove(bookingId, out _);
            }
        }
    }

    public sealed class BookingMessageSubscription : IAsyncDisposable
    {
        private readonly Action _disposeAction;
        private int _disposed;

        public BookingMessageSubscription(Guid id, ChannelReader<string> reader, Action disposeAction)
        {
            Id = id;
            Reader = reader;
            _disposeAction = disposeAction;
        }

        public Guid Id { get; }
        public ChannelReader<string> Reader { get; }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _disposeAction();
            }

            return ValueTask.CompletedTask;
        }
    }
}
