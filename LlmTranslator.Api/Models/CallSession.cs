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

        private bool _disposed = false;

        public CallSession(string callSidA, ILogger logger, ITranslationService translationService)
        {
            _callSidA = callSidA;
            _logger = logger;
            _translationService = translationService;
            _cancellationTokenSource = new CancellationTokenSource();
        }

        public void AddWebSocket(WebSocket webSocket, string callSid)
        {
            if (callSid == _callSidA)
            {
                _webSocketA = webSocket;

                // Create translation adapters when the first WebSocket is added
                _adapterAToB = _translationService.CreateTranslationAdapter(
                    _logger,
                    Environment.GetEnvironmentVariable("CALLING_PARTY_LANGUAGE") ?? "English",
                    Environment.GetEnvironmentVariable("CALLED_PARTY_LANGUAGE") ?? "Spanish");

                _adapterBToA = _translationService.CreateTranslationAdapter(
                    _logger,
                    Environment.GetEnvironmentVariable("CALLED_PARTY_LANGUAGE") ?? "Spanish",
                    Environment.GetEnvironmentVariable("CALLING_PARTY_LANGUAGE") ?? "English");

                // Connect the WebSocket to the translation adapter
                _adapterAToB.SetIncomingWebSocket(_webSocketA);
                _adapterBToA.SetOutgoingWebSocket(_webSocketA);

                // Start processing
                _processAudioTaskA = ProcessWebSocketMessagesAsync(_webSocketA, _adapterAToB, "A");
            }
            else
            {
                _callSidB = callSid;
                _webSocketB = webSocket;

                // Connect the WebSocket to the translation adapter
                _adapterBToA.SetIncomingWebSocket(_webSocketB);
                _adapterAToB.SetOutgoingWebSocket(_webSocketB);

                // Start processing
                _processAudioTaskB = ProcessWebSocketMessagesAsync(_webSocketB, _adapterBToA, "B");
            }
        }

        private async Task ProcessWebSocketMessagesAsync(
            WebSocket webSocket,
            ITranslationAdapter adapter,
            string partyLabel)
        {
            var buffer = new byte[4096];
            var receiveResult = await webSocket.ReceiveAsync(
                new ArraySegment<byte>(buffer), _cancellationTokenSource?.Token ?? CancellationToken.None);

            try
            {
                // First message is expected to be a JSON setup message
                if (!receiveResult.EndOfMessage || receiveResult.MessageType != WebSocketMessageType.Text)
                {
                    _logger.LogWarning("Expected text message for initial setup, received: {MessageType}",
                        receiveResult.MessageType);
                    return;
                }

                // Parse the setup message
                var textMessage = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                var setupData = JsonSerializer.Deserialize<SetupMessage>(textMessage);

                _logger.LogInformation("Received setup for party {Party}: {CallSid}, Parent: {ParentCallSid}",
                    partyLabel, setupData?.CallSid, setupData?.ParentCallSid);

                // Now start handling the binary audio data
                while (webSocket.State == WebSocketState.Open &&
                       !(_cancellationTokenSource?.IsCancellationRequested ?? true))
                {
                    receiveResult = await webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), _cancellationTokenSource?.Token ?? CancellationToken.None);

                    if (receiveResult.MessageType == WebSocketMessageType.Binary)
                    {
                        // Copy the received data to a new buffer (to avoid overwriting)
                        var audioData = new byte[receiveResult.Count];
                        Array.Copy(buffer, audioData, receiveResult.Count);

                        // Send to the adapter for translation
                        await adapter.ProcessAudioAsync(audioData);
                    }
                    else if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("WebSocket closed for party {Party}", partyLabel);
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("WebSocket processing canceled for party {Party}", partyLabel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing WebSocket messages for party {Party}", partyLabel);
            }
            finally
            {
                if (webSocket.State != WebSocketState.Closed && webSocket.State != WebSocketState.Aborted)
                {
                    try
                    {
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Session completed",
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error closing WebSocket for party {Party}", partyLabel);
                    }
                }
            }
        }

        public void Close()
        {
            // Cancel ongoing tasks
            _cancellationTokenSource?.Cancel();

            // Close translation adapters
            _adapterAToB?.Close();
            _adapterBToA?.Close();

            // Close WebSockets
            CloseWebSocketAsync(_webSocketA).Wait();
            CloseWebSocketAsync(_webSocketB).Wait();

            // Clean up
            _webSocketA = null;
            _webSocketB = null;
            _adapterAToB = null;
            _adapterBToA = null;
        }

        private async Task CloseWebSocketAsync(WebSocket? webSocket)
        {
            if (webSocket != null &&
                webSocket.State != WebSocketState.Closed &&
                webSocket.State != WebSocketState.Aborted)
            {
                try
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Session closed",
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error closing WebSocket");
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
                    Close();
                    _cancellationTokenSource?.Dispose();
                    _cancellationTokenSource = null;
                }

                _disposed = true;
            }
        }

        private class SetupMessage
        {
            public string? CallSid { get; set; }
            public string? ParentCallSid { get; set; }
        }
    }
}