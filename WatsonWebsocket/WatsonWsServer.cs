﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WatsonWebsocket
{
    /// <summary>
    /// Watson Websocket server.
    /// </summary>
    public class WatsonWsServer : IDisposable
    {
        #region Public-Members

        /// <summary>
        /// Determine if the server is listening for new connections.
        /// </summary>
        public bool IsListening
        {
            get
            {
                if (_Listener != null)
                {
                    return _Listener.IsListening;
                }

                return false;
            }
        }

        /// <summary>
        /// Event fired when a client connects.
        /// </summary>
        public event EventHandler<ClientConnectedEventArgs> ClientConnected;

        /// <summary>
        /// Event fired when a client disconnects.
        /// </summary>
        public event EventHandler<ClientDisconnectedEventArgs> ClientDisconnected;

        /// <summary>
        /// Event fired when the server stops.
        /// </summary>
        public event EventHandler ServerStopped;

        /// <summary>
        /// Event fired when a message is received.
        /// </summary>
        public event EventHandler<MessageReceivedEventArgs> MessageReceived;

        /// <summary>
        /// Indicate whether or not invalid or otherwise unverifiable certificates should be accepted.  Default is true.
        /// </summary>
        public bool AcceptInvalidCertificates
        {
            get
            {
                return _AcceptInvalidCertificates;
            }
            set
            {
                _AcceptInvalidCertificates = value;
            }
        }

        /// <summary>
        /// Specify the IP addresses that are allowed to connect.  If none are supplied, all IP addresses are permitted.
        /// </summary>
        public List<string> PermittedIpAddresses = new List<string>();

        /// <summary>
        /// Method to invoke when sending a log message.
        /// </summary>
        public Action<string> Logger = null;

        /// <summary>
        /// Method to invoke when receiving a raw (non-websocket) HTTP request.
        /// </summary>
        public Action<HttpListenerContext> HttpHandler = null;

        /// <summary>
        /// Statistics.
        /// </summary>
        public Statistics Stats
        {
            get
            {
                return _Stats;
            }
        }

        #endregion

        #region Private-Members

        private bool _AcceptInvalidCertificates = true;
        private string _ListenerIp;
        private int _ListenerPort;
        private IPAddress _ListenerIpAddress;
        private string _ListenerPrefix;
        private HttpListener _Listener;
        private readonly object _PermittedIpsLock = new object();
        private ConcurrentDictionary<string, ClientMetadata> _Clients;
        private CancellationTokenSource _TokenSource;
        private CancellationToken _Token;
        private Task _AcceptConnectionsTask;
        private Statistics _Stats = new Statistics();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Initializes the Watson websocket server.
        /// Be sure to call 'Start()' to start the server.
        /// </summary>
        /// <param name="listenerIp">The IP address upon which to listen.</param>
        /// <param name="listenerPort">The TCP port upon which to listen.</param>
        /// <param name="ssl">Enable or disable SSL.</param>
        public WatsonWsServer(
            string listenerIp,
            int listenerPort,
            bool ssl)
        {
            if (listenerPort < 1) throw new ArgumentOutOfRangeException(nameof(listenerPort));

            if (String.IsNullOrEmpty(listenerIp))
            {
                _ListenerIpAddress = IPAddress.Loopback;
                _ListenerIp = _ListenerIpAddress.ToString();
            }
            else if (listenerIp == "*" || listenerIp == "+")
            {
                _ListenerIp = listenerIp;
                _ListenerIpAddress = IPAddress.Any;
            }
            else
            {
                if (!IPAddress.TryParse(listenerIp, out _ListenerIpAddress))
                {
                    _ListenerIpAddress = Dns.GetHostEntry(listenerIp).AddressList[0];
                }

                _ListenerIp = listenerIp;
            }

            _ListenerPort = listenerPort;

            if (ssl) _ListenerPrefix = "https://" + _ListenerIp + ":" + _ListenerPort + "/";
            else _ListenerPrefix = "http://" + _ListenerIp + ":" + _ListenerPort + "/";

            _Listener = new HttpListener();
            _Listener.Prefixes.Add(_ListenerPrefix);

            _TokenSource = new CancellationTokenSource();
            _Token = _TokenSource.Token;
            _Clients = new ConcurrentDictionary<string, ClientMetadata>();
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Tear down the server and dispose of background workers.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Start accepting new connections.
        /// </summary>
        public void Start()
        {
            if (IsListening) throw new InvalidOperationException("Watson websocket server is already running.");

            _Stats = new Statistics();

            Logger?.Invoke("[WatsonWsServer.Start] starting " + _ListenerPrefix);

            if (_AcceptInvalidCertificates) ServicePointManager.ServerCertificateValidationCallback += (sender, certificate, chain, sslPolicyErrors) => true;

            _TokenSource = new CancellationTokenSource();
            _Token = _TokenSource.Token;

            _AcceptConnectionsTask = Task.Run(AcceptConnections, _Token);
        }

        /// <summary>
        /// Stop accepting new connections.
        /// </summary>
        public void Stop()
        {
            if (!IsListening) throw new InvalidOperationException("Watson websocket server is not running.");

            Logger?.Invoke("[WatsonWsServer.Stop] stopping " + _ListenerPrefix);

            _Listener.Stop();
        }

        /// <summary>
        /// Send text data to the specified client, asynchronously.
        /// </summary>
        /// <param name="ipPort">IP:port of the recipient client.</param>
        /// <param name="data">String containing data.</param>
        /// <param name="cancellationToken">Cancellation token for cancelling of the operation by the user.</param>
        /// <returns>Task with Boolean indicating if the message was sent successfully.</returns>
        public Task<bool> SendAsync(string ipPort, string data, CancellationToken cancellationToken = default)
        {
            if (String.IsNullOrEmpty(data)) throw new ArgumentNullException(nameof(data));
            if (!_Clients.TryGetValue(ipPort, out ClientMetadata client))
            {
                Logger?.Invoke("[WatsonWsServer.SendAsync " + ipPort + "] unable to find client");
                return Task.FromResult(false);
            }

            return MessageWriteAsync(client, Encoding.UTF8.GetBytes(data), WebSocketMessageType.Text, cancellationToken);
        }

        /// <summary>
        /// Send binary data to the specified client, asynchronously.
        /// </summary>
        /// <param name="ipPort">IP:port of the recipient client.</param>
        /// <param name="data">Byte array containing data.</param>
        /// <param name="cancellationToken">Cancellation token for cancelling of the operation by the user.</param>
        /// <returns>Task with Boolean indicating if the message was sent successfully.</returns>
        public Task<bool> SendAsync(string ipPort, byte[] data, CancellationToken cancellationToken = default)
        {
            if (data == null || data.Length < 1) throw new ArgumentNullException(nameof(data));
            if (!_Clients.TryGetValue(ipPort, out ClientMetadata client))
            {
                Logger?.Invoke("[WatsonWsServer.SendAsync " + ipPort + "] unable to find client");
                return Task.FromResult(false);
            }

            return MessageWriteAsync(client, data, WebSocketMessageType.Binary, cancellationToken);
        }

        /// <summary>
        /// Send binary data to the specified client, asynchronously.
        /// </summary>
        /// <param name="ipPort">IP:port of the recipient client.</param>
        /// <param name="data">Byte array containing data.</param>
        /// <param name="msgType">Web socket message type.</param>
        /// <param name="cancellationToken">Cancellation token for cancelling of the operation by the user.</param>
        /// <returns>Task with Boolean indicating if the message was sent successfully.</returns>
        public Task<bool> SendAsync(string ipPort, byte[] data, WebSocketMessageType msgType, CancellationToken cancellationToken = default)
        {
            if (data == null || data.Length < 1) throw new ArgumentNullException(nameof(data));
            if (!_Clients.TryGetValue(ipPort, out ClientMetadata client))
            {
                Logger?.Invoke("[WatsonWsServer.SendAsync " + ipPort + "] unable to find client");
                return Task.FromResult(false);
            }

            return MessageWriteAsync(client, data, msgType, cancellationToken);
        }

        /// <summary>
        /// Determine whether or not the specified client is connected to the server.
        /// </summary>
        /// <param name="ipPort">IP:port of the recipient client.</param>
        /// <returns>Boolean indicating if the client is connected to the server.</returns>
        public bool IsClientConnected(string ipPort)
        {
            return _Clients.TryGetValue(ipPort, out _);
        }

        /// <summary>
        /// List the IP:port of each connected client.
        /// </summary>
        /// <returns>A string list containing each client IP:port.</returns>
        public IEnumerable<string> ListClients()
        {
            return _Clients.Keys.ToArray();
        }

        /// <summary>
        /// Forcefully disconnect a client.
        /// </summary>
        /// <param name="ipPort">IP:port of the client.</param>
        public void DisconnectClient(string ipPort)
        {
            // force disconnect of client
            if (_Clients.TryGetValue(ipPort, out var client))
            {
                client.TokenSource.Cancel();
            }
        }

        /// <summary>
        /// Retrieve the awaiter.
        /// </summary>
        /// <returns>TaskAwaiter.</returns>
        public TaskAwaiter GetAwaiter()
        {
            return _AcceptConnectionsTask.GetAwaiter();
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Tear down the server and dispose of background workers.
        /// </summary>
        /// <param name="disposing">Disposing.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_Clients != null)
                {
                    foreach (KeyValuePair<string, ClientMetadata> client in _Clients)
                    {
                        client.Value.TokenSource.Cancel();
                    }
                }

                if (_Listener != null)
                {
                    if (_Listener.IsListening) _Listener.Stop();
                    _Listener.Close();
                }

                _TokenSource.Cancel();
            }
        }

        private async Task AcceptConnections()
        {
            string header = "[WatsonWsServer.AcceptConnections] ";

            try
            {
                _Listener.Start();

                while (true)
                {
                    if (_Token.IsCancellationRequested) break;
                    if (!_Listener.IsListening)
                    {
                        Task.Delay(100).Wait();
                        continue;
                    }

                    HttpListenerContext ctx = await _Listener.GetContextAsync().ConfigureAwait(false);
                    string ip = ctx.Request.RemoteEndPoint.Address.ToString();
                    int port = ctx.Request.RemoteEndPoint.Port;
                    string ipPort = ip + ":" + port;

                    lock (_PermittedIpsLock)
                    {
                        if (PermittedIpAddresses != null
                            && PermittedIpAddresses.Count > 0
                            && !PermittedIpAddresses.Contains(ip))
                        {
                            Logger?.Invoke(header + "rejecting connection from " + ipPort + " (not permitted)");
                            ctx.Response.StatusCode = 401;
                            ctx.Response.Close();
                            continue;
                        }
                    }

                    if (!ctx.Request.IsWebSocketRequest)
                    {
                        if (HttpHandler == null)
                        {
                            Logger?.Invoke(header + "non-websocket request rejected from " + ipPort);
                            ctx.Response.StatusCode = 400;
                            ctx.Response.Close();
                        }
                        else
                        {
                            Logger?.Invoke(header + "non-websocket request forwarded to HTTP handler from " + ipPort + ": " + ctx.Request.HttpMethod.ToString() + " " + ctx.Request.RawUrl);
                            HttpHandler.Invoke(ctx);
                        }

                        continue;
                    }
                    else
                    {
                        /*
                        HttpListenerRequest req = ctx.Request;
                        Console.WriteLine(Environment.NewLine + req.HttpMethod.ToString() + " " + req.RawUrl);
                        if (req.Headers != null && req.Headers.Count > 0)
                        {
                            Console.WriteLine("Headers:");
                            var items = req.Headers.AllKeys.SelectMany(req.Headers.GetValues, (k, v) => new { key = k, value = v });
                            foreach (var item in items)
                            {
                                Console.WriteLine("  {0}: {1}", item.key, item.value);
                            }
                        }
                        */
                    }

                    await Task.Run(() =>
                    {
                        Logger?.Invoke(header + "starting data receiver for " + ipPort);

                        CancellationTokenSource tokenSource = new CancellationTokenSource();
                        CancellationToken token = tokenSource.Token;

                        Task.Run(async () =>
                        {
                            WebSocketContext wsContext = await ctx.AcceptWebSocketAsync(subProtocol: null);
                            WebSocket ws = wsContext.WebSocket;
                            ClientMetadata md = new ClientMetadata(ctx, ws, wsContext, tokenSource);

                            _Clients.TryAdd(md.IpPort, md);

                            ClientConnected?.Invoke(this, new ClientConnectedEventArgs(md.IpPort, ctx.Request));
                            await Task.Run(() => DataReceiver(md), token);

                        }, token);

                    }, _Token).ConfigureAwait(false);
                }
            }
            catch (HttpListenerException)
            {
                // thrown when disposed
            }
            catch (OperationCanceledException)
            {
                // thrown when disposed
            }
            catch (ObjectDisposedException)
            {
                // thrown when disposed
            }
            catch (Exception e)
            {
                Logger?.Invoke(header + "exception:" + Environment.NewLine + e.ToString());
            }
            finally
            {
                ServerStopped?.Invoke(this, EventArgs.Empty);
            }
        }

        private async Task DataReceiver(ClientMetadata md)
        {
            string header = "[WatsonWsServer.DataReceiver " + md.IpPort + "] ";
            Logger?.Invoke(header + "starting data receiver");

            try
            {
                while (true)
                {
                    MessageReceivedEventArgs msg = await MessageReadAsync(md).ConfigureAwait(false);

                    _Stats.ReceivedMessages = _Stats.ReceivedMessages + 1;
                    _Stats.ReceivedBytes += msg.Data.Length;

                    if (msg.Data != null)
                    {
                        MessageReceived?.Invoke(this, msg);
                    }
                    else
                    {
                        await Task.Delay(100).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // thrown when disposed
            }
            catch (WebSocketException)
            {
                Logger?.Invoke(header + "websocket disconnected");
            }
            catch (Exception e)
            {
                Logger?.Invoke(header + "exception: " + Environment.NewLine + e.ToString());
            }
            finally
            {
                string ipPort = md.IpPort;
                ClientDisconnected?.Invoke(this, new ClientDisconnectedEventArgs(md.IpPort));
                md.Ws.Dispose();
                Logger?.Invoke(header + "disconnected");
                _Clients.TryRemove(ipPort, out _);
            }
        }

        private async Task<MessageReceivedEventArgs> MessageReadAsync(ClientMetadata md)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                byte[] buffer = new byte[65536];
                ArraySegment<byte> seg = new ArraySegment<byte>(buffer);

                while (true)
                {
                    WebSocketReceiveResult result = await md.Ws.ReceiveAsync(seg, md.TokenSource.Token).ConfigureAwait(false);
                    if (result.CloseStatus != null
                        || md.Ws.State != WebSocketState.Open
                        || result.MessageType == WebSocketMessageType.Close
                        || md.TokenSource.Token.IsCancellationRequested)
                    {
                        throw new WebSocketException("Websocket closed.");
                    }

                    if (result.Count > 0)
                    {
                        stream.Write(buffer, 0, result.Count);
                    }

                    if (result.EndOfMessage)
                    {
                        return new MessageReceivedEventArgs(md.IpPort, stream.ToArray(), result.MessageType);
                    }
                }
            }
        }

        private async Task<bool> MessageWriteAsync(ClientMetadata md, byte[] data, WebSocketMessageType msgType, CancellationToken cancellationToken = default)
        {
            string header = "[WatsonWsServer.MessageWriteAsync " + md.IpPort + "] ";

            try
            {
                #region Send-Message

                // Cannot have two simultaneous SendAsync calls so use a
                // semaphore to block the second until the first has completed

                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(md.TokenSource.Token, cancellationToken))
                {
                    await md.SendLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                    try
                    {
                        await md.Ws.SendAsync(new ArraySegment<byte>(data, 0, data.Length), msgType, true, linkedCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        md.SendLock.Release();
                    }
                }

                _Stats.SentMessages += 1;
                _Stats.SentBytes += data.Length;

                return true;

                #endregion
            }
            catch (OperationCanceledException oce)
            {
                Logger?.Invoke(header + "disconnected (canceled): " + oce.Message);
            }
            catch (WebSocketException wse)
            {
                Logger?.Invoke(header + "disconnected (websocket exception): " + wse.Message);
            }
            catch (Exception e)
            {
                Logger?.Invoke(header + "disconnected due to exception: " + Environment.NewLine + e.ToString());
            }

            return false;
        }

        #endregion
    }
}
