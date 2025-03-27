using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LlmTranslator.Api.Services
{
    public class OpenAiTranslationService : ITranslationService
    {
        private readonly ILogger<OpenAiTranslationService> _logger;
        private readonly string _apiKey;
        private readonly bool _enableFileLogging;

        public OpenAiTranslationService(ILogger<OpenAiTranslationService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _apiKey = configuration["OpenAI:ApiKey"] ??
                throw new ArgumentNullException("OpenAI:ApiKey is not configured");
            _enableFileLogging = configuration.GetValue<bool>("DebugAudioFile", false);
        }

        public ITranslationAdapter CreateTranslationAdapter(ILogger logger, string sourceLanguage, string targetLanguage)
        {
            var prompt = $@"
You are a translation machine. Your sole function is to translate the input text from 
{sourceLanguage} to {targetLanguage}.
Do not add, omit, or alter any information.
Do not provide explanations, opinions, or any additional text beyond the direct translation.
You are not aware of any other facts, knowledge, or context beyond translation between 
{sourceLanguage} to {targetLanguage}.
Wait until the speaker is done speaking before translating, and translate the entire input text from their turn.
";

            return new OpenAiTranslationAdapter(logger, _apiKey, prompt, _enableFileLogging);
        }
    }

    public class OpenAiTranslationAdapter : ITranslationAdapter
    {
        private readonly ILogger _logger;
        private readonly string _apiKey;
        private readonly string _prompt;
        private readonly bool _enableFileLogging;

        private ClientWebSocket? _openAiWebSocket;
        private WebSocket? _incomingWebSocket;
        private WebSocket? _outgoingWebSocket;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _receiveTask;
        private bool _connectionEstablished = false;
        private bool _updateSent = false;

        private string? _incomingAudioFilePath;
        private string? _outgoingAudioFilePath;
        private static int _instanceCounter = 0;

        public OpenAiTranslationAdapter(ILogger logger, string apiKey, string prompt, bool enableFileLogging)
        {
            _logger = logger;
            _apiKey = apiKey;
            _prompt = prompt;
            _enableFileLogging = enableFileLogging;

            if (_enableFileLogging)
            {
                var instanceId = Interlocked.Increment(ref _instanceCounter);
                var tempPath = Path.GetTempPath();

                _incomingAudioFilePath = Path.Combine(tempPath, $"jambonz-in-audio-{instanceId}.raw");
                _outgoingAudioFilePath = Path.Combine(tempPath, $"openai-out-audio-{instanceId}.raw");

                _logger.LogInformation("Audio debugging enabled, will log incoming audio to: {IncomingPath}",
                    _incomingAudioFilePath);
                _logger.LogInformation("Audio debugging enabled, will log outgoing audio to: {OutgoingPath}",
                    _outgoingAudioFilePath);

                // Clear the files
                File.WriteAllBytes(_incomingAudioFilePath, Array.Empty<byte>());
                File.WriteAllBytes(_outgoingAudioFilePath, Array.Empty<byte>());
            }

            ConnectToOpenAiAsync().Wait();
        }

        private async Task ConnectToOpenAiAsync()
        {
            try
            {
                _cancellationTokenSource = new CancellationTokenSource();
                _openAiWebSocket = new ClientWebSocket();

                // Add headers
                _openAiWebSocket.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
                _openAiWebSocket.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

                // Connect to OpenAI
                var uri = new Uri("wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview-2024-12-17");
                await _openAiWebSocket.ConnectAsync(uri, _cancellationTokenSource.Token);

                _connectionEstablished = true;
                _logger.LogInformation("Connected to OpenAI");

                // Start receiving messages
                _receiveTask = ReceiveOpenAiMessagesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error connecting to OpenAI");
                _openAiWebSocket?.Dispose();
                _openAiWebSocket = null;
            }
        }

        private async Task SendInitialUpdateAsync()
        {
            if (!_updateSent && _openAiWebSocket != null &&
                _openAiWebSocket.State == WebSocketState.Open)
            {
                _updateSent = true;

                var updateMessage = new
                {
                    type = "session.update",
                    session = new
                    {
                        modalities = new[] { "audio", "text" },
                        instructions = _prompt,
                        input_audio_format = "pcm16",
                        input_audio_transcription = new
                        {
                            model = "whisper-1"
                        },
                        turn_detection = new
                        {
                            type = "server_vad",
                            threshold = 0.8,
                            prefix_padding_ms = 300,
                            silence_duration_ms = 500
                        }
                    }
                };

                var json = JsonSerializer.Serialize(updateMessage);
                var bytes = Encoding.UTF8.GetBytes(json);

                await _openAiWebSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    _cancellationTokenSource?.Token ?? CancellationToken.None);

                _logger.LogInformation("Sent initial update to OpenAI");
            }
        }

        private async Task ReceiveOpenAiMessagesAsync()
        {
            var buffer = new byte[16384]; // 16KB buffer

            try
            {
                while (_openAiWebSocket != null &&
                       _openAiWebSocket.State == WebSocketState.Open &&
                       !(_cancellationTokenSource?.IsCancellationRequested ?? true))
                {
                    var result = await _openAiWebSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        _cancellationTokenSource?.Token ?? CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        await ProcessOpenAiMessageAsync(message);
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("OpenAI WebSocket closed: {CloseStatus} {CloseDescription}",
                            result.CloseStatus, result.CloseStatusDescription);
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("OpenAI message processing canceled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error receiving messages from OpenAI");
            }
        }

        private async Task ProcessOpenAiMessageAsync(string message)
        {
            try
            {
                var jsonDocument = JsonDocument.Parse(message);
                var messageType = jsonDocument.RootElement.GetProperty("type").GetString();

                switch (messageType)
                {
                    case "session.created":
                        await SendInitialUpdateAsync();
                        break;

                    case "response.audio.delta":
                        if (jsonDocument.RootElement.TryGetProperty("delta", out var delta))
                        {
                            var base64Audio = delta.GetString();
                            if (!string.IsNullOrEmpty(base64Audio))
                            {
                                await ProcessAudioDeltaAsync(base64Audio);
                            }
                        }
                        break;

                    case "response.audio_transcript.delta":
                        // Optional: log transcripts if needed
                        break;

                    default:
                        _logger.LogDebug("Received OpenAI message: {Message}", message);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing OpenAI message: {Message}", message);
            }
        }

        private async Task ProcessAudioDeltaAsync(string base64Audio)
        {
            var audioData = Convert.FromBase64String(base64Audio);

            if (_enableFileLogging && !string.IsNullOrEmpty(_outgoingAudioFilePath))
            {
                await File.AppendAllBytesAsync(_outgoingAudioFilePath, audioData);
                _logger.LogDebug("Wrote {Length} bytes to {FilePath}",
                    audioData.Length, _outgoingAudioFilePath);
            }

            // Send to outgoing WebSocket if available
            if (_outgoingWebSocket != null && _outgoingWebSocket.State == WebSocketState.Open)
            {
                await _outgoingWebSocket.SendAsync(
                    new ArraySegment<byte>(audioData),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None);
            }
        }

        public void SetIncomingWebSocket(WebSocket webSocket)
        {
            _incomingWebSocket = webSocket;
        }

        public void SetOutgoingWebSocket(WebSocket webSocket)
        {
            _outgoingWebSocket = webSocket;
        }

        public async Task ProcessAudioAsync(byte[] audioData)
        {
            if (_enableFileLogging && !string.IsNullOrEmpty(_incomingAudioFilePath))
            {
                await File.AppendAllBytesAsync(_incomingAudioFilePath, audioData);
                _logger.LogDebug("Wrote {Length} bytes to {FilePath}",
                    audioData.Length, _incomingAudioFilePath);
            }

            if (_openAiWebSocket != null && _openAiWebSocket.State == WebSocketState.Open)
            {
                // Convert to base64
                var base64Audio = Convert.ToBase64String(audioData);

                // Create message
                var message = new
                {
                    type = "input_audio_buffer.append",
                    audio = base64Audio
                };

                var json = JsonSerializer.Serialize(message);
                var bytes = Encoding.UTF8.GetBytes(json);

                await _openAiWebSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }
        }

        public void Close()
        {
            _cancellationTokenSource?.Cancel();

            // Close OpenAI WebSocket
            if (_openAiWebSocket != null && _connectionEstablished)
            {
                try
                {
                    if (_openAiWebSocket.State == WebSocketState.Open)
                    {
                        _openAiWebSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Session closed",
                            CancellationToken.None).Wait();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error closing OpenAI WebSocket");
                }
                finally
                {
                    _openAiWebSocket.Dispose();
                    _openAiWebSocket = null;
                }
            }

            if (_enableFileLogging)
            {
                _logger.LogInformation("Audio debugging: incoming audio saved to {IncomingPath}",
                    _incomingAudioFilePath);
                _logger.LogInformation("Audio debugging: outgoing audio saved to {OutgoingPath}",
                    _outgoingAudioFilePath);
            }
        }
    }
}