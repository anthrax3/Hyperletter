using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Hyperletter.Batch;
using Hyperletter.Channel;
using Hyperletter.Extension;
using Hyperletter.IoC;
using Hyperletter.Letter;

namespace Hyperletter {
    public class HyperSocket : IHyperSocket {
        private readonly ConcurrentDictionary<Binding, SocketListener> _listeners = new ConcurrentDictionary<Binding, SocketListener>();
        private readonly ConcurrentDictionary<Binding, IChannel> _channels = new ConcurrentDictionary<Binding, IChannel>();
        private readonly ConcurrentDictionary<Guid, IChannel> _routeChannels = new ConcurrentDictionary<Guid, IChannel>();

        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly LetterDispatcher _letterDispatcher;
        private readonly Timer _heartbeat;
        private readonly HyperletterFactory _factory;

        public SocketOptions Options { get; private set; }
        public IEnumerable<IChannel> Channels { get { return _channels.Values; } }

        public event Action<IHyperSocket, ILetter> Sent;
        public event Action<IHyperSocket, ILetter> Received;
        public event Action<IHyperSocket, Binding, ILetter> Discarded;

        public event Action<ILetter> Requeued;

        public event Action<IHyperSocket, Binding> Connecting;
        public event Action<IHyperSocket, Binding> Connected;
        public event Action<IHyperSocket, Binding, ShutdownReason> Disconnected;
        public event Action<IHyperSocket> Disposed;

        public HyperSocket() : this(new SocketOptions()) {
        }

        public HyperSocket(SocketOptions options) {
            Options = options;

            _factory = new HyperletterFactory(BuildContainer());
            _letterDispatcher = _factory.CreateLetterDispatcher();

            _heartbeat = new Timer(Heartbeat);
            _heartbeat.Change(Options.Heartbeat.Intervall, Options.Heartbeat.Intervall);
        }

        private Container BuildContainer() {
            var container = new Container();

            container.RegisterInstance(this);
            container.RegisterInstance(Options);
            container.RegisterInstance(_cancellationTokenSource.Token);

            container.Register<LetterSerializer>().AsSingleton();
            container.Register<LetterDeserializer>().AsSingleton();
            container.Register<LetterDispatcher>().AsSingleton();
            container.Register<SocketListener>().AsSingleton();
            container.Register<HyperletterFactory>().AsSingleton();

            container.Register<LetterTransmitter>();
            container.Register<LetterReceiver>();

            container.Register<OutboundChannel>();
            container.Register<InboundChannel>();
            container.Register<BatchLetterBuilder>();
            container.Register<BatchChannel>();

            return container;
        }

        private void Heartbeat(object state) {
            foreach(var channel in Channels)
                channel.Heartbeat();
        }

        public void Bind(IPAddress ipAddress, int port) {
            var binding = new Binding(ipAddress, port);

            var listener = _factory.CreateSocketListener(binding);
            _listeners[binding] = listener;
            listener.IncomingChannel += PrepareChannel;
            listener.Start();
        }

        public void Unbind(IPAddress ipAddress, int port) {
            var binding = new Binding(ipAddress, port);
            SocketListener listener;
            if(_listeners.TryRemove(binding, out listener))
                listener.Stop();
        }

        public void Connect(IPAddress ipAddress, int port) {
            var binding = new Binding(ipAddress, port);
            var channel = _factory.CreateOutboundChannel(binding);
            PrepareChannel(channel);
            channel.Connect();
        }

        public void Disconnect(IPAddress ipAddress, int port) {
            Task.Factory.StartNew(() => {
                var binding = new Binding(ipAddress, port);
                IChannel channel;
                if(_channels.TryGetValue(binding, out channel))
                    channel.Disconnect();
            });
        }

        public void Send(ILetter letter) {
            _letterDispatcher.EnqueueLetter(letter);
        }

        public void SendTo(ILetter answer, Guid toNodeId) {
            IChannel channel;
            if (_routeChannels.TryGetValue(toNodeId, out channel))
                channel.Enqueue(answer);
        }

        public void Dispose() {
            Task.Factory.StartNew(() => {
                _cancellationTokenSource.Cancel();
                _heartbeat.Dispose();

                foreach(var listener in _listeners.Values)
                    listener.Stop();

                foreach(var channel in _channels.Values)
                    channel.Disconnect();

                var evnt = Disposed;
                if (evnt != null)
                    evnt(this);
            });
        }

        private void PrepareChannel(IChannel channel) {
            if(Options.Batch.Enabled)
                channel = _factory.CreateBatchChannel(channel);

            HookChannel(channel);

            _channels[channel.Binding] = channel;
            channel.Initialize();
        }

        private void HookChannel(IChannel channel) {
            channel.Received += ChannelReceived;
            channel.FailedToSend += ChannelFailedToSend;
            channel.Sent += ChannelSent;
            channel.ChannelDisconnected += ChannelDisconnected;
            channel.ChannelConnecting += ChannelConnecting;
            channel.ChannelConnected += ChannelConnected;
            channel.ChannelInitialized += ChannelInitialized;
            channel.ChannelInitialized += ChannelAvailable;
            channel.ChannelQueueEmpty += ChannelAvailable;
        }

        private void UnhookChannel(IChannel channel) {
            channel.Received -= ChannelReceived;
            channel.FailedToSend -= ChannelFailedToSend;
            channel.Sent -= ChannelSent;
            channel.ChannelDisconnected -= ChannelDisconnected;
            channel.ChannelConnecting -= ChannelConnecting;
            channel.ChannelConnected -= ChannelConnected;
            channel.ChannelInitialized -= ChannelInitialized;
            channel.ChannelInitialized -= ChannelAvailable;
            channel.ChannelQueueEmpty -= ChannelAvailable;
        }

        private void ChannelConnecting(IChannel channel) {
            var evnt = Connecting;
            if (evnt != null)
                evnt(this, channel.Binding);
        }

        private void ChannelConnected(IChannel obj) {
            var evnt = Connected;
            if (evnt != null)
                evnt(this, obj.Binding);
        }

        private void ChannelInitialized(IChannel obj) {
            _routeChannels.TryAdd(obj.RemoteNodeId, obj);
        }

        private void ChannelDisconnected(IChannel channel, ShutdownReason reason) {
            var binding = channel.Binding;

            _routeChannels.Remove(channel.RemoteNodeId);

            if(channel.Direction == Direction.Inbound || reason == ShutdownReason.Requested) {
                _channels.Remove(binding);
                UnhookChannel(channel);
            }

            var evnt = Disconnected;
            if (evnt != null)
                evnt(this, binding, reason);
        }

        private void ChannelReceived(IChannel channel, ILetter letter) {
            var evnt = Received;
            if (evnt != null)
                evnt(this, letter);
        }

        private void ChannelSent(IChannel channel, ILetter letter) {
            var evnt = Sent;
            if (evnt != null)
                evnt(this, letter);
        }

        private void ChannelFailedToSend(IChannel channel, ILetter letter) {
            if(letter.Options.HasFlag(LetterOptions.Multicast)) {
                Discard(channel, letter);
            } else if(letter.Options.HasFlag(LetterOptions.Requeue)) {
                Requeue(letter);
            } else {
                Discard(channel, letter);
            }
        }

        private void Discard(IChannel channel, ILetter letter) {
            var evnt = Discarded;
            if (evnt != null && !letter.Options.HasFlag(LetterOptions.SilentDiscard))
                evnt(this, channel.Binding, letter);
        }

        private void Requeue(ILetter letter) {
            _letterDispatcher.EnqueueLetter(letter);

            var evnt = Requeued;
            if (evnt != null)
                evnt(letter);
        }

        private void ChannelAvailable(IChannel channel) {
            _letterDispatcher.EnqueueChannel(channel);
        }
    }
}