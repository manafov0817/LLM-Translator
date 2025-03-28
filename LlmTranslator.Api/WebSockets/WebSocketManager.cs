using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

            _logger.LogInformation("[DIAG] New WebSocket connection: {ConnectionId}, Remote IP: {RemoteIP}, Path: {Path}",
                connectionId, context.Connection.RemoteIpAddress, context.Request.Path);

            // Add a timeout to close connections that don't send setup info
            var timeoutTask = Task.Delay(10000, timeout.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                {
                    _logger.LogWarning("[DIAG] Closing WebSocket connection due to setup timeout: {ConnectionId}", connectionId);
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

            var buffer = new byte[16384]; // Increased buffer size for diagnostics
            bool setupReceived = false;

            try
            {
                // Wait for the first message which should be a JSON setup message
                _logger.LogInformation("[DIAG] Waiting for initial setup message: {ConnectionId}", connectionId);

                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                _logger.LogInformation("[DIAG] Received initial message: Type={MessageType}, Count={Count}, EndOfMessage={EndOfMessage}",
                    result.MessageType, result.Count, result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    setupReceived = true;
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    _logger.LogInformation("[DIAG] Received setup message: {Message}", message);

                    try
                    {
                        // Cancel the timeout
                        timeout.Cancel();

                        // Parse the setup message
                        var setupData = JsonSerializer.Deserialize<SetupMessage>(message);

                        if (setupData?.CallSid == null)
                        {
                            _logger.LogWarning("[DIAG] Invalid setup data, missing CallSid: {Message}", message);
                            await CloseSocketAsync(webSocket, "Invalid setup data, missing CallSid");
                            return;
                        }

                        // Store the call SID in the socket info
                        socketInfo.CallSid = setupData.CallSid;
                        socketInfo.ParentCallSid = setupData.ParentCallSid;

                        _logger.LogInformation("[DIAG] Valid setup received: CallSid={CallSid}, ParentCallSid={ParentCallSid}, Direction={Direction}",
                            setupData.CallSid, setupData.ParentCallSid, setupData.Direction);

                        // Add to YardMaster
                        try
                        {
                            _logger.LogInformation("[DIAG] Adding WebSocket to YardMaster: CallSid={CallSid}, ParentCallSid={ParentCallSid}",
                                setupData.CallSid, setupData.ParentCallSid);

                            _yardMaster.AddJambonzWebSocket(webSocket, setupData.CallSid, setupData.ParentCallSid);

                            _logger.LogInformation("[DIAG] Successfully added WebSocket to YardMaster");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[DIAG] Error adding WebSocket to YardMaster");
                            await CloseSocketAsync(webSocket, "Error adding to call session");
                            return;
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogError(ex, "[DIAG] Invalid JSON received: {Message}", message);
                        await CloseSocketAsync(webSocket, "Invalid setup data");
                        return;
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // This is unexpected, but we'll handle it gracefully
                    _logger.LogWarning("[DIAG] Received binary data instead of text setup. Size: {Size} bytes", result.Count);

                    // Try to determine if this is a Jambonz connection by examining the first few bytes
                    var firstFewBytes = new byte[Math.Min(16, result.Count)];
                    Array.Copy(buffer, firstFewBytes, firstFewBytes.Length);
                    var hexString = BitConverter.ToString(firstFewBytes);

                    _logger.LogInformation("[DIAG] First bytes of binary data: {HexString}", hexString);

                    // Continue without setup data - we'll assign a dummy setup
                    socketInfo.CallSid = $"unknown-{connectionId}";

                    _logger.LogWarning("[DIAG] Assigning temporary CallSid: {CallSid}", socketInfo.CallSid);

                    // Cancel the timeout since we received something
                    timeout.Cancel();

                    // Process this binary data immediately so it's not lost
                    processData(result.MessageType, result.Count);
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("[DIAG] WebSocket closed during setup: {Status} {Description}",
                        result.CloseStatus, result.CloseStatusDescription);
                    return;
                }

                // Keep reading until the WebSocket is closed
                int messageCount = 0;

                _logger.LogInformation("[DIAG] Starting main WebSocket receive loop: {ConnectionId}", connectionId);

                // Main loop for reading WebSocket messages
                while (webSocket.State == WebSocketState.Open)
                {
                    messageCount++;

                    try
                    {
                        result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                        if (messageCount <= 5 || messageCount % 100 == 0)
                        {
                            _logger.LogDebug("[DIAG] WebSocket message {Count}: Type={MessageType}, Size={Size}",
                                messageCount, result.MessageType, result.Count);
                        }

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _logger.LogInformation("[DIAG] WebSocket closing: {Status} {Description}",
                                result.CloseStatus, result.CloseStatusDescription);
                            break;
                        }

                        // Process the data - at this point YardMaster should handle it
                        processData(result.MessageType, result.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[DIAG] Error receiving WebSocket data");
                        break;
                    }
                }

                _logger.LogInformation("[DIAG] WebSocket connection closed: {ConnectionId}, State: {State}, Messages: {Count}",
                    connectionId, webSocket.State, messageCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DIAG] Unhandled error in WebSocketManager: {ConnectionId}", connectionId);
            }
            finally
            {
                _sockets.TryRemove(webSocket, out _);
                try { timeout.Dispose(); } catch { }

                try
                {
                    if (webSocket.State != WebSocketState.Closed && webSocket.State != WebSocketState.Aborted)
                    {
                        await CloseSocketAsync(webSocket, "Manager cleanup");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[DIAG] Error during final WebSocket cleanup");
                }

                _logger.LogInformation("[DIAG] WebSocket connection handler exited: {ConnectionId}, Setup received: {SetupReceived}",
                    connectionId, setupReceived);
            }

            // Local function to process received data
            void processData(WebSocketMessageType messageType, int count)
            {
                if (!setupReceived && messageType == WebSocketMessageType.Binary)
                {
                    _logger.LogWarning("[DIAG] Received binary data before setup. This is unexpected but will be processed.");
                }

                if (messageType == WebSocketMessageType.Text)
                {
                    var textMessage = Encoding.UTF8.GetString(buffer, 0, count);
                    _logger.LogInformation("[DIAG] Received text message: {Message}", textMessage);
                }
                // Binary data is handled by YardMaster/CallSession via the WebSocket itself
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
                _logger.LogInformation("[DIAG] Closing WebSocket: {State}, Reason: {Reason}",
                    webSocket.State, reason);

                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        reason,
                        CancellationToken.None);

                    _logger.LogInformation("[DIAG] WebSocket closed successfully");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DIAG] Error closing WebSocket: {Reason}", reason);
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
            [JsonPropertyName("sampleRate")]
            public int SampleRate { get; set; }

            [JsonPropertyName("mixType")]
            public string MixType { get; set; } = string.Empty;

            [JsonPropertyName("callSid")]
            public string CallSid { get; set; } = string.Empty;

            [JsonPropertyName("direction")]
            public string Direction { get; set; } = string.Empty;

            [JsonPropertyName("from")]
            public string From { get; set; } = string.Empty;

            [JsonPropertyName("to")]
            public string To { get; set; } = string.Empty;

            [JsonPropertyName("callId")]
            public string CallId { get; set; } = string.Empty;

            [JsonPropertyName("sbcCallid")]
            public string SbcCallid { get; set; } = string.Empty;

            [JsonPropertyName("sipStatus")]
            public int SipStatus { get; set; }

            [JsonPropertyName("sipReason")]
            public string SipReason { get; set; } = string.Empty;

            [JsonPropertyName("callStatus")]
            public string CallStatus { get; set; } = string.Empty;

            [JsonPropertyName("accountSid")]
            public string AccountSid { get; set; } = string.Empty;

            [JsonPropertyName("traceId")]
            public string TraceId { get; set; } = string.Empty;

            [JsonPropertyName("applicationSid")]
            public string ApplicationSid { get; set; } = string.Empty;

            [JsonPropertyName("fsSipAddress")]
            public string FsSipAddress { get; set; } = string.Empty;

            [JsonPropertyName("originatingSipIp")]
            public string OriginatingSipIp { get; set; } = string.Empty;

            [JsonPropertyName("apiBaseUrl")]
            public string ApiBaseUrl { get; set; } = string.Empty;

            [JsonPropertyName("fsPublicIp")]
            public string FsPublicIp { get; set; } = string.Empty;

            [JsonPropertyName("parentCallSid")]
            public string? ParentCallSid { get; set; }
        }
    }
}