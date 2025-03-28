using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LlmTranslator.Api.Services;

namespace LlmTranslator.Api.Models
{
    /// <summary>
    /// Manages a single call session with translation between two parties
    /// Similar to the switchman.js in the Node.js implementation
    /// </summary>
    public class CallSession : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _callSidA;
        private readonly ITranslationService _translationService;

        private string? _callSidB;
        private WebSocket? _webSocketA;
        private WebSocket? _webSocketB;

        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _processAudioTaskA;
        private Task? _processAudioTaskB;

        // Translation adapters for both directions
        private ITranslationAdapter? _adapterAToB;
        private ITranslationAdapter? _adapterBToA;

        // Diagnostics
        private long _totalBytesReceivedA = 0;
        private long _totalBytesReceivedB = 0;
        private DateTime _lastActivityTime = DateTime.UtcNow;

        private bool _disposed = false;

        public CallSession(string callSidA, ILogger logger, ITranslationService translationService)
        {
            _callSidA = callSidA;
            _logger = logger;
            _translationService = translationService;
            _cancellationTokenSource = new CancellationTokenSource();

            _logger.LogInformation("[DIAG] Created new CallSession for {CallSid} with cancellation token state: {TokenCanceled}",
                callSidA, _cancellationTokenSource.IsCancellationRequested);

            // Start heartbeat for diagnostics
            Task.Run(DiagnosticHeartbeat);
        }

        private async Task DiagnosticHeartbeat()
        {
            while (!_disposed && !(_cancellationTokenSource?.IsCancellationRequested ?? true))
            {
                try
                {
                    _logger.LogInformation("[DIAG] Session heartbeat - CallSid: {CallSid}, WebSocketA: {WebSocketAState}, WebSocketB: {WebSocketBState}, " +
                        "BytesA: {BytesA}KB, BytesB: {BytesB}KB, TaskA: {TaskAStatus}, TaskB: {TaskBStatus}, " +
                        "Last activity: {LastActivity}s ago",
                        _callSidA,
                        _webSocketA?.State.ToString() ?? "null",
                        _webSocketB?.State.ToString() ?? "null",
                        _totalBytesReceivedA / 1024,
                        _totalBytesReceivedB / 1024,
                        _processAudioTaskA?.Status.ToString() ?? "null",
                        _processAudioTaskB?.Status.ToString() ?? "null",
                        (DateTime.UtcNow - _lastActivityTime).TotalSeconds);

                    await Task.Delay(5000);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[DIAG] Error in diagnostic heartbeat");
                }
            }
        }

        public void AddWebSocket(WebSocket webSocket, string callSid)
        {
            _logger.LogInformation("[DIAG] AddWebSocket called with callSid: {CallSid}, parent: {ParentCallSid}, WebSocket state: {State}",
                callSid, _callSidA, webSocket.State);

            if (callSid == _callSidA)
            {
                _webSocketA = webSocket;
                _logger.LogInformation("[DIAG] Setting WebSocketA for callSid: {CallSid}", callSid);

                try
                {
                    // Create translation adapters when the first WebSocket is added
                    string sourceLanguage = Environment.GetEnvironmentVariable("CallingPartyLanguage") ?? "English";
                    string targetLanguage = Environment.GetEnvironmentVariable("CalledPartyLanguage") ?? "Turkish";

                    _logger.LogInformation("[DIAG] Creating translation adapters with languages: {SourceLang} -> {TargetLang}",
                        sourceLanguage, targetLanguage);

                    _adapterAToB = _translationService.CreateTranslationAdapter(
                        _logger,
                        sourceLanguage,
                        targetLanguage);

                    _adapterBToA = _translationService.CreateTranslationAdapter(
                        _logger,
                        targetLanguage,
                        sourceLanguage);

                    _logger.LogInformation("[DIAG] Created adapters AtoB: {AdapterAToB}, BtoA: {AdapterBToA}",
                        _adapterAToB != null, _adapterBToA != null);

                    // Connect the WebSocket to the translation adapter
                    _adapterAToB.SetIncomingWebSocket(_webSocketA);
                    _adapterBToA.SetOutgoingWebSocket(_webSocketA);

                    // Start processing with extensive error handling
                    try
                    {
                        _logger.LogInformation("[DIAG] Starting ProcessWebSocketMessagesAsync for party A");
                        _processAudioTaskA = ProcessWebSocketMessagesAsync(_webSocketA, _adapterAToB, "A");
                        _logger.LogInformation("[DIAG] Successfully started ProcessWebSocketMessagesAsync for party A: {TaskState}",
                            _processAudioTaskA.Status);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[DIAG] Failed to start ProcessWebSocketMessagesAsync for party A");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[DIAG] Error setting up adapters for party A");
                }
            }
            else
            {
                _callSidB = callSid;
                _webSocketB = webSocket;
                _logger.LogInformation("[DIAG] Setting WebSocketB for callSid: {CallSid}", callSid);

                try
                {
                    // Connect the WebSocket to the translation adapter
                    _adapterBToA.SetIncomingWebSocket(_webSocketB);
                    _adapterAToB.SetOutgoingWebSocket(_webSocketB);

                    // Start processing
                    try
                    {
                        _logger.LogInformation("[DIAG] Starting ProcessWebSocketMessagesAsync for party B");
                        _processAudioTaskB = ProcessWebSocketMessagesAsync(_webSocketB, _adapterBToA, "B");
                        _logger.LogInformation("[DIAG] Successfully started ProcessWebSocketMessagesAsync for party B: {TaskState}",
                            _processAudioTaskB.Status);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[DIAG] Failed to start ProcessWebSocketMessagesAsync for party B");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[DIAG] Error setting up adapters for party B");
                }
            }
        }

        private async Task ProcessWebSocketMessagesAsync(
            WebSocket webSocket,
            ITranslationAdapter adapter,
            string partyLabel)
        {
            _logger.LogInformation("[DIAG] ProcessWebSocketMessagesAsync ENTERED for party {Party}, WebSocket: {State}",
                partyLabel, webSocket.State);

            var buffer = new byte[4096];

            try
            {
                // First message is expected to be a JSON setup message
                var receiveResult = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), _cancellationTokenSource?.Token ?? CancellationToken.None);

                _logger.LogInformation("[DIAG] First message received for party {Party}: Type={MessageType}, Count={Count}, EndOfMessage={EndOfMessage}",
                    partyLabel, receiveResult.MessageType, receiveResult.Count, receiveResult.EndOfMessage);

                if (receiveResult.MessageType != WebSocketMessageType.Text)
                {
                    _logger.LogWarning("[DIAG] Expected text message for initial setup for party {Party}, received: {MessageType}",
                        partyLabel, receiveResult.MessageType);

                    // Don't return - continue processing even if the first message is not text
                    // Many issues may be due to stopping here
                    _logger.LogInformation("[DIAG] Continuing despite non-text first message for party {Party}", partyLabel);
                }
                else
                {
                    // Parse the setup message
                    var textMessage = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                    try
                    {
                        var setupData = JsonSerializer.Deserialize<SetupMessage>(textMessage);
                        _logger.LogInformation("[DIAG] Received setup for party {Party}: {CallSid}, Parent: {ParentCallSid}",
                            partyLabel, setupData?.CallSid, setupData?.ParentCallSid);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[DIAG] Error parsing setup message for party {Party}: {Message}",
                            partyLabel, textMessage);
                    }
                }

                // Track for binary messages
                int messageCount = 0;
                int textMessageCount = 0;
                int binaryMessageCount = 0;

                // Now start handling the binary audio data
                _logger.LogInformation("[DIAG] Starting main receive loop for party {Party}", partyLabel);

                while (webSocket.State == WebSocketState.Open &&
                       !(_cancellationTokenSource?.IsCancellationRequested ?? true))
                {
                    messageCount++;
                    _lastActivityTime = DateTime.UtcNow;

                    try
                    {
                        receiveResult = await webSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer), _cancellationTokenSource?.Token ?? CancellationToken.None);

                        if (messageCount <= 5 || messageCount % 100 == 0)
                        {
                            _logger.LogDebug("[DIAG] Message {Count} received for party {Party}: Type={MessageType}, Count={Count}",
                                messageCount, partyLabel, receiveResult.MessageType, receiveResult.Count);
                        }

                        if (receiveResult.MessageType == WebSocketMessageType.Binary)
                        {
                            binaryMessageCount++;
                            // Copy the received data to a new buffer (to avoid overwriting)
                            var audioData = new byte[receiveResult.Count];
                            Array.Copy(buffer, audioData, receiveResult.Count);

                            // Update diagnostic counters
                            if (partyLabel == "A")
                                _totalBytesReceivedA += receiveResult.Count;
                            else
                                _totalBytesReceivedB += receiveResult.Count;

                            // Log periodic stats
                            if (binaryMessageCount % 100 == 0)
                            {
                                _logger.LogInformation("[DIAG] Party {Party} received {Count} binary messages, total bytes: {TotalBytes}KB",
                                    partyLabel, binaryMessageCount,
                                    (partyLabel == "A" ? _totalBytesReceivedA : _totalBytesReceivedB) / 1024);
                            }

                            // Send to the adapter for translation
                                await adapter.ProcessAudioAsync(audioData);
                        }
                        else if (receiveResult.MessageType == WebSocketMessageType.Text)
                        {
                            textMessageCount++;
                            var textMessage = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                            _logger.LogInformation("[DIAG] Text message for party {Party}: {Message}",
                                partyLabel, textMessage);
                        }
                        else if (receiveResult.MessageType == WebSocketMessageType.Close)
                        {
                            _logger.LogInformation("[DIAG] WebSocket closed for party {Party}: {Status} {Description}",
                                partyLabel, receiveResult.CloseStatus, receiveResult.CloseStatusDescription);
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInformation("[DIAG] Receive operation canceled for party {Party}", partyLabel);
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[DIAG] Error receiving message {Count} for party {Party}",
                            messageCount, partyLabel);

                        // Don't break - try to continue receiving
                        await Task.Delay(100);
                    }
                }

                _logger.LogInformation("[DIAG] Exited receive loop for party {Party} - WebSocket state: {State}, Token canceled: {Canceled}",
                    partyLabel, webSocket.State, _cancellationTokenSource?.IsCancellationRequested ?? true);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[DIAG] WebSocket processing canceled for party {Party}", partyLabel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DIAG] Error in main WebSocket processing for party {Party}", partyLabel);
            }
            finally
            {
                if (webSocket.State != WebSocketState.Closed && webSocket.State != WebSocketState.Aborted)
                {
                    try
                    {
                        _logger.LogInformation("[DIAG] Closing WebSocket for party {Party} from state {State}",
                            partyLabel, webSocket.State);

                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Session completed",
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[DIAG] Error closing WebSocket for party {Party}", partyLabel);
                    }
                }

                _logger.LogInformation("[DIAG] ProcessWebSocketMessagesAsync EXITED for party {Party}", partyLabel);
            }
        }

        public void Close()
        {
            _logger.LogInformation("[DIAG] Close() called for session {CallSid}", _callSidA);

            // Cancel ongoing tasks
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                _logger.LogInformation("[DIAG] Canceling token for session {CallSid}", _callSidA);
                _cancellationTokenSource.Cancel();
            }

            // Close translation adapters with error handling
            try
            {
                _logger.LogInformation("[DIAG] Closing adapter AtoB: {AdapterExists}", _adapterAToB != null);
                _adapterAToB?.Close();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DIAG] Error closing adapter AtoB");
            }

            try
            {
                _logger.LogInformation("[DIAG] Closing adapter BtoA: {AdapterExists}", _adapterBToA != null);
                _adapterBToA?.Close();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DIAG] Error closing adapter BtoA");
            }

            // Close WebSockets with error handling
            try
            {
                _logger.LogInformation("[DIAG] Closing WebSocketA: {State}", _webSocketA?.State.ToString() ?? "null");
                CloseWebSocketAsync(_webSocketA).Wait();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DIAG] Error in CloseWebSocketAsync for WebSocketA");
            }

            try
            {
                _logger.LogInformation("[DIAG] Closing WebSocketB: {State}", _webSocketB?.State.ToString() ?? "null");
                CloseWebSocketAsync(_webSocketB).Wait();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DIAG] Error in CloseWebSocketAsync for WebSocketB");
            }

            // Clean up
            _webSocketA = null;
            _webSocketB = null;
            _adapterAToB = null;
            _adapterBToA = null;

            _logger.LogInformation("[DIAG] Session {CallSid} close completed", _callSidA);
        }

        private async Task CloseWebSocketAsync(WebSocket? webSocket)
        {
            if (webSocket != null &&
                webSocket.State != WebSocketState.Closed &&
                webSocket.State != WebSocketState.Aborted)
            {
                try
                {
                    _logger.LogInformation("[DIAG] Closing WebSocket in state: {State}", webSocket.State);
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Session closed",
                        CancellationToken.None);
                    _logger.LogInformation("[DIAG] WebSocket closed successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[DIAG] Error closing WebSocket in state {State}", webSocket.State);
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _logger.LogInformation("[DIAG] Disposing CallSession {CallSid}", _callSidA);
                    Close();

                    try
                    {
                        _cancellationTokenSource?.Dispose();
                        _cancellationTokenSource = null;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[DIAG] Error disposing cancellation token source");
                    }
                }

                _disposed = true;
                _logger.LogInformation("[DIAG] CallSession {CallSid} disposed", _callSidA);
            }
        }

        private class SetupMessage
        {
            public string? CallSid { get; set; }
            public string? ParentCallSid { get; set; }
        }
    }
}