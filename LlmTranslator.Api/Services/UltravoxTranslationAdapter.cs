using LlmTranslator.Api.Models;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LlmTranslator.Api.Services
{
    public class UltravoxTranslationAdapter : ITranslationAdapter
    {
        private readonly ILogger _logger;
        private readonly string _apiKey;
        private readonly string _prompt;
        private readonly bool _enableFileLogging;
        private readonly string _model;
        private readonly string _voice;

        private ClientWebSocket? _ultravoxWebSocket;
        private WebSocket? _incomingWebSocket;
        private WebSocket? _outgoingWebSocket;
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _receiveTask;
        private bool _initialized = false;
        private bool _errored = false;
        private string? _joinUrl = null;

        private string? _incomingAudioFilePath;
        private string? _outgoingAudioFilePath;
        private static int _instanceCounter = 0;

        // Track bytes for monitoring
        private long _totalBytesReceived = 0;
        private long _totalBytesSent = 0;

        public UltravoxTranslationAdapter(
            ILogger logger,
            string apiKey,
            string prompt,
            bool enableFileLogging,
            string model = "fixie-ai/ultravox",
            string voice = "Mark")
        {
            _logger = logger;
            _apiKey = apiKey;
            _prompt = prompt;
            _enableFileLogging = enableFileLogging;
            _model = model;
            _voice = voice;

            if (_enableFileLogging)
            {
                var instanceId = Interlocked.Increment(ref _instanceCounter);
                var tempPath = Path.GetTempPath();

                _incomingAudioFilePath = Path.Combine(tempPath, $"jambonz-in-audio-ultravox-{instanceId}.raw");
                _outgoingAudioFilePath = Path.Combine(tempPath, $"ultravox-out-audio-{instanceId}.raw");

                _logger.LogInformation("Audio debugging enabled, will log incoming audio to: {IncomingPath}",
                    _incomingAudioFilePath);
                _logger.LogInformation("Audio debugging enabled, will log outgoing audio to: {OutgoingPath}",
                    _outgoingAudioFilePath);

                // Clear the files
                File.WriteAllBytes(_incomingAudioFilePath, Array.Empty<byte>());
                File.WriteAllBytes(_outgoingAudioFilePath, Array.Empty<byte>());
            }

            // Initialize the Ultravox connection
            Task.Run(SafeInitializeAsync);
        }

        private async Task SafeInitializeAsync()
        {
            try
            {
                await InitializeConnectionAsync();
            }
            catch (Exception ex)
            {
                _errored = true;
                _logger.LogError(ex, "Failed to initialize Ultravox connection");
            }
        }

        private async Task InitializeConnectionAsync()
        {
            try
            {
                // First create a call to get the joinUrl
                var callData = await CreateCallAsync();

                // Check if there was an error during call creation
                if (callData.Error)
                {
                    _errored = true;
                    _logger.LogError("Failed to create Ultravox call: {Message}", callData.Message);
                    return;
                }

                if (string.IsNullOrEmpty(callData.JoinUrl))
                {
                    _errored = true;
                    _logger.LogError("No joinUrl returned from Ultravox API");
                    return;
                }

                _joinUrl = callData.JoinUrl;

                // Then connect to the WebSocket
                await ConnectToUltravoxAsync();
                _initialized = true;
            }
            catch (Exception ex)
            {
                _errored = true;
                _logger.LogError(ex, "Failed to initialize Ultravox connection");
            }
        }

        private async Task<CallCreationResult> CreateCallAsync()
        {
            try
            {
                var payload = new
                {
                    systemPrompt = _prompt,
                    model = _model,
                    voice = _voice,
                    firstSpeaker = "FIRST_SPEAKER_USER",
                    firstSpeakerSettings = new
                    {
                        user = new { }
                    },
                    inactivityMessages = new[]
                    {
                        new
                        {
                            duration = "120s"
                        }
                    },
                    medium = new
                    {
                        serverWebSocket = new
                        {
                            inputSampleRate = 8000,
                            outputSampleRate = 8000
                        }
                    }
                };
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
                _logger.LogDebug("Sending request to Ultravox API");
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync("https://api.ultravox.ai/api/calls", content);
                if (!response.IsSuccessStatusCode)
                {
                    // Handle specific error codes
                    if (response.StatusCode == System.Net.HttpStatusCode.PaymentRequired)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        _logger.LogError("Ultravox subscription issue: Payment required. {Detail}", errorContent);
                        return new CallCreationResult
                        {
                            Error = true,
                            Message = $"Ultravox subscription issue: Payment required. {errorContent}"
                        };
                    }
                    return new CallCreationResult
                    {
                        Error = true,
                        Message = $"Failed to create Ultravox call: {response.StatusCode}"
                    };
                }
                var responseContent = await response.Content.ReadAsStringAsync();
                var responseData = JsonSerializer.Deserialize<UltravoxResponse>(responseContent);
                if (responseData == null || string.IsNullOrEmpty(responseData.JoinUrl))
                {
                    return new CallCreationResult
                    {
                        Error = true,
                        Message = "Invalid response from Ultravox API"
                    };
                }
                _logger.LogInformation("Ultravox Call registered with joinUrl: {JoinUrl}", responseData.JoinUrl);
                return new CallCreationResult
                {
                    Error = false,
                    JoinUrl = responseData.JoinUrl
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating Ultravox call");
                return new CallCreationResult
                {
                    Error = true,
                    Message = ex.Message
                };
            }
        }

        private async Task ConnectToUltravoxAsync()
        {
            if (string.IsNullOrEmpty(_joinUrl))
            {
                _logger.LogError("Cannot connect to Ultravox: No joinUrl available");
                return;
            }

            _logger.LogInformation("Connecting to Ultravox WebSocket: {JoinUrl}", _joinUrl);

            try
            {
                _cancellationTokenSource = new CancellationTokenSource();
                _ultravoxWebSocket = new ClientWebSocket();

                await _ultravoxWebSocket.ConnectAsync(new Uri(_joinUrl), _cancellationTokenSource.Token);

                _logger.LogInformation("Connected to Ultravox WebSocket");

                // Start receiving messages
                _receiveTask = ReceiveUltravoxMessagesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error connecting to Ultravox WebSocket");
                _ultravoxWebSocket?.Dispose();
                _ultravoxWebSocket = null;
            }
        }

        public async Task ProcessAudioAsync(byte[] audioData)
        {
            try
            {
                if (_enableFileLogging && !string.IsNullOrEmpty(_incomingAudioFilePath))
                {
                    await File.AppendAllBytesAsync(_incomingAudioFilePath, audioData);
                }

                // Track audio data
                _totalBytesReceived += audioData.Length;

                // Periodic logging of total received
                if (_totalBytesReceived % 50000 < audioData.Length)
                {
                    _logger.LogInformation("Total audio received: {TotalKB}KB",
                        _totalBytesReceived / 1024);
                }

                // Check if we're properly initialized
                if (!_initialized)
                {
                    _logger.LogWarning("Received audio but Ultravox adapter not yet initialized");
                    return;
                }

                // Check if we're in an error state
                if (_errored)
                {
                    _logger.LogWarning("Received audio but Ultravox adapter is in error state");
                    return;
                }

                // For Ultravox, just send the raw PCM audio (no base64 encoding needed)
                if (_ultravoxWebSocket != null && _ultravoxWebSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        await _ultravoxWebSocket.SendAsync(
                            new ArraySegment<byte>(audioData),
                            WebSocketMessageType.Binary,
                            true,
                            _cancellationTokenSource?.Token ?? CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error sending audio to Ultravox");
                    }
                }
                else
                {
                    _logger.LogWarning("Cannot send audio to Ultravox - WebSocket not available");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in ProcessAudioAsync");
            }
        }

        private async Task ProcessUltravoxAudioAsync(byte[] audioData)
        {
            try
            {
                if (_enableFileLogging && !string.IsNullOrEmpty(_outgoingAudioFilePath))
                {
                    await File.AppendAllBytesAsync(_outgoingAudioFilePath, audioData);
                }

                // Track translated audio data
                _totalBytesSent += audioData.Length;

                // Periodic logging of total sent
                if (_totalBytesSent % 50000 < audioData.Length)
                {
                    _logger.LogInformation("Received translated audio from Ultravox: {TotalKB}KB",
                        _totalBytesSent / 1024);
                }

                // Send to outgoing WebSocket if available
                if (_outgoingWebSocket != null && _outgoingWebSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        await _outgoingWebSocket.SendAsync(
                            new ArraySegment<byte>(audioData),
                            WebSocketMessageType.Binary,
                            true,
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error sending translated audio to caller");
                    }
                }
                else
                {
                    _logger.LogWarning("Cannot send translated audio - outgoing WebSocket not available");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in ProcessUltravoxAudioAsync");
            }
        }

        private async Task ReceiveUltravoxMessagesAsync()
        {
            var buffer = new byte[16384]; // 16KB buffer
            int messageCount = 0;
            int textMessageCount = 0;
            int binaryMessageCount = 0;

            try
            {
                _logger.LogInformation("Starting Ultravox message receiver loop");

                while (_ultravoxWebSocket != null &&
                       _ultravoxWebSocket.State == WebSocketState.Open &&
                       !(_cancellationTokenSource?.IsCancellationRequested ?? true))
                {
                    messageCount++;

                    try
                    {
                        var result = await _ultravoxWebSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            _cancellationTokenSource?.Token ?? CancellationToken.None);

                        if (result.MessageType == WebSocketMessageType.Binary)
                        {
                            binaryMessageCount++;

                            // This is audio data from Ultravox
                            var audioData = new byte[result.Count];
                            Array.Copy(buffer, audioData, result.Count);

                            // Log periodically about receiving binary data
                            if (binaryMessageCount % 20 == 0)
                            {
                                _logger.LogInformation("Received {Count} binary messages from Ultravox",
                                    binaryMessageCount);
                            }

                            await ProcessUltravoxAudioAsync(audioData);
                        }
                        else if (result.MessageType == WebSocketMessageType.Text)
                        {
                            textMessageCount++;

                            // This is a JSON control message
                            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            _logger.LogInformation("Ultravox control message: {Message}", message);
                        }
                        else if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _logger.LogInformation("Ultravox WebSocket closing");
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInformation("Ultravox receive operation canceled");
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error receiving message from Ultravox");

                        // Don't break - try to continue receiving
                        await Task.Delay(100);
                    }
                }

                _logger.LogInformation("Exited Ultravox receiver loop - Messages: {Total} (Binary: {Binary}, Text: {Text})",
                    messageCount, binaryMessageCount, textMessageCount);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Ultravox message processing canceled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in Ultravox message processing");
            }
        }

        public void SetIncomingWebSocket(WebSocket webSocket)
        {
            // Check if we're in an error state before setting up socket
            if (_errored)
            {
                _logger.LogWarning("Not setting up incoming socket - adapter is in error state");
                return;
            }

            _incomingWebSocket = webSocket; 
        }

        public void SetOutgoingWebSocket(WebSocket webSocket)
        {
            _outgoingWebSocket = webSocket;
        }

        public void Close()
        {
            _logger.LogInformation("Closing Ultravox connection");

            _cancellationTokenSource?.Cancel();

            // Close Ultravox WebSocket
            if (_ultravoxWebSocket != null)
            {
                try
                {
                    if (_ultravoxWebSocket.State == WebSocketState.Open)
                    {
                        _ultravoxWebSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Session closed",
                            CancellationToken.None).Wait();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error closing Ultravox WebSocket");
                }
                finally
                {
                    _ultravoxWebSocket.Dispose();
                    _ultravoxWebSocket = null;
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