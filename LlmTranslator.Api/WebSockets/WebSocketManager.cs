using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LlmTranslator.Api.Utils;

namespace LlmTranslator.Api.WebSockets
{
    /// <summary>
    /// Manages WebSocket connections for audio streaming
    /// </summary>
    public class WebSocketManager
    {
        private readonly ILogger<WebSocketManager> _logger;
        private readonly YardMaster _yardMaster;
        private readonly ConcurrentDictionary<WebSocket, WebSocketInfo> _sockets;

        public WebSocketManager(ILogger<WebSocketManager> logger, YardMaster yardMaster)
        {
            _logger = logger;
            _yardMaster = yardMaster;
            _sockets = new ConcurrentDictionary<WebSocket, WebSocketInfo>();
        }

        /// <summary>
        /// Handles a new WebSocket connection
        /// </summary>
        /// <param name="webSocket">The WebSocket</param>
        /// <param name="context">The HTTP context</param>
        public async Task HandleWebSocketConnection(WebSocket webSocket, HttpContext context)
        {
            var connectionId = Guid.NewGuid().ToString();
            var timeout = new CancellationTokenSource();

            // Add a timeout to close connections that don't send setup info
            var timeoutTask = Task.Delay(10000, timeout.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                {
                    _logger.LogWarning("Closing WebSocket connection due to setup timeout: {ConnectionId}", connectionId);
                    CloseSocketAsync(webSocket, "Setup timeout").Wait();
                }
            }, TaskContinuationOptions.OnlyOnRanToCompletion);

            var socketInfo = new WebSocketInfo
            {
                ConnectionId = connectionId,
                TimeoutSource = timeout,
                ConnectedAt = DateTimeOffset.UtcNow
            };

            _sockets.TryAdd(webSocket, socketInfo);

            var buffer = new byte[4096];

            try
            {
                // Wait for the first message which should be a JSON setup message
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    _logger.LogDebug("Received setup message: {Message}", message);

                    try
                    {
                        // Cancel the timeout
                        timeout.Cancel();

                        // Parse the setup message
                        var setupData = JsonSerializer.Deserialize<SetupMessage>(message);

                        if (setupData?.CallSid == null)
                        {
                            _logger.LogWarning("Invalid setup data, missing CallSid: {Message}", message);
                            await CloseSocketAsync(webSocket, "Invalid setup data, missing CallSid");
                            return;
                        }

                        // Store the call SID in the socket info
                        socketInfo.CallSid = setupData.CallSid;
                        socketInfo.ParentCallSid = setupData.ParentCallSid;

                        // Add to YardMaster
                        _yardMaster.AddJambonzWebSocket(webSocket, setupData.CallSid, setupData.ParentCallSid);

                        // Keep the connection alive
                        await KeepAliveAsync(webSocket);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogError(ex, "Invalid JSON received: {Message}", message);
                        await CloseSocketAsync(webSocket, "Invalid setup data");
                    }
                }
                else
                {
                    _logger.LogWarning("Expected text message for initial setup, received: {MessageType}", result.MessageType);
                    await CloseSocketAsync(webSocket, "Expected text message for initial setup");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling WebSocket connection: {ConnectionId}", connectionId);
                await CloseSocketAsync(webSocket, "Error handling connection");
            }
            finally
            {
                _sockets.TryRemove(webSocket, out _);
                timeout.Dispose();
            }
        }

        /// <summary>
        /// Keeps the WebSocket connection alive
        /// </summary>
        /// <param name="webSocket">The WebSocket</param>
        private async Task KeepAliveAsync(WebSocket webSocket)
        {
            try
            {
                // Use a task that never completes to keep the socket open
                // The actual socket will be managed by the YardMaster
                var cts = new CancellationTokenSource();
                await Task.Delay(-1, cts.Token);
            }
            catch (TaskCanceledException)
            {
                // Expected when the token is canceled
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error keeping WebSocket connection alive");
            }
        }

        /// <summary>
        /// Closes a WebSocket connection
        /// </summary>
        /// <param name="webSocket">The WebSocket</param>
        /// <param name="reason">The reason for closing</param>
        private async Task CloseSocketAsync(WebSocket webSocket, string reason)
        {
            try
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        reason,
                        CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing WebSocket: {Reason}", reason);
            }
        }

        private class WebSocketInfo
        {
            public string ConnectionId { get; set; } = string.Empty;
            public string? CallSid { get; set; }
            public string? ParentCallSid { get; set; }
            public DateTimeOffset ConnectedAt { get; set; }
            public CancellationTokenSource? TimeoutSource { get; set; }
        }

        private class SetupMessage
        {
            public string CallSid { get; set; } = string.Empty;
            public string? ParentCallSid { get; set; }
        }
    }
}