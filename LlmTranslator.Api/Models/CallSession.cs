using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LlmTranslator.Api.Services;

namespace LlmTranslator.Api.Models
{
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

                try
                {
                    string sourceLanguage = Environment.GetEnvironmentVariable("CallingPartyLanguage") ?? "Turkish";
                    string targetLanguage = Environment.GetEnvironmentVariable("CalledPartyLanguage") ?? "English";

                    _adapterAToB = _translationService.CreateTranslationAdapter(
                        _logger,
                        sourceLanguage,
                        targetLanguage);

                    _adapterBToA = _translationService.CreateTranslationAdapter(
                        _logger,
                        targetLanguage,
                        sourceLanguage);

                    _adapterAToB.SetIncomingWebSocket(_webSocketA);
                    _adapterBToA.SetOutgoingWebSocket(_webSocketA);

                    try
                    {
                        _processAudioTaskA = ProcessWebSocketMessagesAsync(_webSocketA, _adapterAToB, "A");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to start processing for party A");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error setting up adapters for party A");
                }
            }
            else
            {
                _callSidB = callSid;
                _webSocketB = webSocket;

                try
                {
                    _adapterBToA.SetIncomingWebSocket(_webSocketB);
                    _adapterAToB.SetOutgoingWebSocket(_webSocketB);

                    try
                    {
                        _processAudioTaskB = ProcessWebSocketMessagesAsync(_webSocketB, _adapterBToA, "B");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to start processing for party B");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error setting up adapters for party B");
                }
            }
        }

        private async Task ProcessWebSocketMessagesAsync(
            WebSocket webSocket,
            ITranslationAdapter adapter,
            string partyLabel)
        {
            var buffer = new byte[4096];

            try
            {
                var receiveResult = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), _cancellationTokenSource?.Token ?? CancellationToken.None);

                if (receiveResult.MessageType != WebSocketMessageType.Text)
                {
                    _logger.LogWarning("Expected text message for initial setup for party {Party}", partyLabel);
                }
                else
                {
                    var textMessage = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                    try
                    {
                        var setupData = JsonSerializer.Deserialize<SetupMessage>(textMessage);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error parsing setup message for party {Party}", partyLabel);
                    }
                }

                while (webSocket.State == WebSocketState.Open &&
                       !(_cancellationTokenSource?.IsCancellationRequested ?? true))
                {
                    try
                    {
                        receiveResult = await webSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer), _cancellationTokenSource?.Token ?? CancellationToken.None);

                        if (receiveResult.MessageType == WebSocketMessageType.Binary)
                        {
                            var audioData = new byte[receiveResult.Count];
                            Array.Copy(buffer, audioData, receiveResult.Count);

                            await adapter.ProcessAudioAsync(audioData);
                        }
                        else if (receiveResult.MessageType == WebSocketMessageType.Text)
                        {
                            // Process text messages if needed
                        }
                        else if (receiveResult.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error receiving message for party {Party}", partyLabel);
                        await Task.Delay(100);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Operation canceled
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in WebSocket processing for party {Party}", partyLabel);
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
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
            }

            try
            {
                _adapterAToB?.Close();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing adapter AtoB");
            }

            try
            {
                _adapterBToA?.Close();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing adapter BtoA");
            }

            try
            {
                CloseWebSocketAsync(_webSocketA).Wait();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing WebSocketA");
            }

            try
            {
                CloseWebSocketAsync(_webSocketB).Wait();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing WebSocketB");
            }

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

                    try
                    {
                        _cancellationTokenSource?.Dispose();
                        _cancellationTokenSource = null;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error disposing cancellation token source");
                    }
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