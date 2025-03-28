using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmTranslator.Api.Services
{
    public class UltravoxTranslationService : ITranslationService
    {
        private readonly ILogger<UltravoxTranslationService> _logger;
        private readonly string _apiKey;
        private readonly bool _enableFileLogging;

        public UltravoxTranslationService(ILogger<UltravoxTranslationService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _apiKey = configuration["Ultravox:ApiKey"] ??
                throw new ArgumentNullException("Ultravox:ApiKey is not configured");
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

            var targetVoice = GetVoiceNameByLanguage(targetLanguage);

            return new UltravoxTranslationAdapter(logger, _apiKey, prompt, _enableFileLogging, voice: targetVoice);
        }

        public string GetVoiceNameByLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
                return null;

            // Normalize input for matching
            language = language.ToLowerInvariant().Trim();

            // Define voice mappings with original format
            var voicesByLanguage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["english"] = new List<string> {
                "Mark", "Mark2", "David-English-British", "Mark-Slow",
                "Muyiwa-English", "Elilhiz-English", "Steve-English-Australian",
                "Emily-English", "Tanya-English", "Aaron-English", "Conversationalist-English"
            },
                ["english-british"] = new List<string> {
                "David-English-British"
            },
                ["english-australian"] = new List<string> {
                "Steve-English-Australian"
            },
                ["english-indian"] = new List<string> {
                "Amrut-English-Indian", "Chinmay-English-Indian", "Riya-Rao-English-Indian",
                "Anika-English-Indian", "Monika-English-Indian", "Raju-English-Indian"
            },
                ["spanish"] = new List<string> {
                "Alex-Spanish", "Flavia-Spanish", "Carolina-Spanish", "Miquel-Spanish",
                "Victor-Spanish", "Andrea-Spanish", "Damian-Spanish", "Tatiana-Spanish", "Mauricio-Spanish"
            },
                ["portuguese"] = new List<string> {
                "Ana-Portuguese", "Francisco-Portuguese", "Rosa-Portuguese", "Samuel-Portuguese"
            },
                ["brazilian-portuguese"] = new List<string> {
                "Keren-Brazilian-Portuguese"
            },
                ["french"] = new List<string> {
                "Hugo-French", "Coco-French", "Gabriel-French", "Alize-French", "Nicolas-French"
            },
                ["german"] = new List<string> {
                "Ben-German", "Frida - German", "Susi-German", "HerrGruber-German"
            },
                ["arabic"] = new List<string> {
                "Salma-Arabic", "Raed-Arabic", "Sana-Arabic", "Anas-Arabic"
            },
                ["arabic-egyptian"] = new List<string> {
                "Haytham-Arabic-Egyptian", "Amr-Arabic-Egyptian"
            },
                ["polish"] = new List<string> {
                "Marcin-Polish", "Hanna-Polish", "Bea - Polish", "Pawel - Polish"
            },
                ["romanian"] = new List<string> {
                "Ciprian - Romanian", "Corina - Romanian", "Cristina-Romanian", "Antonia-Romanian"
            },
                ["russian"] = new List<string> {
                "Felix-Russian", "Nadia-Russian"
            },
                ["italian"] = new List<string> {
                "Linda-Italian", "Giovanni-Italian"
            },
                ["hindi"] = new List<string> {
                "Aakash-Hindi"
            },
                ["hindi-urdu"] = new List<string> {
                "Muskaan-Hindi-Urdu", "Anjali-Hindi-Urdu", "Krishna-Hindi-Urdu", "Riya-Hindi-Urdu"
            },
                ["dutch"] = new List<string> {
                "Daniel-Dutch", "Ruth-Dutch"
            },
                ["ukrainian"] = new List<string> {
                "Vira-Ukrainian", "Dmytro-Ukrainian"
            },
                ["turkish"] = new List<string> {
                "Cicek-Turkish", "Doga-Turkish"
            },
                ["japanese"] = new List<string> {
                "Morioki-Japanese", "Asahi-Japanese"
            },
                ["swedish"] = new List<string> {
                "Sanna-Swedish", "Adam-Swedish"
            },
                ["chinese"] = new List<string> {
                "Martin-Chinese", "Maya-Chinese"
            },
                ["tamil"] = new List<string> {
                "Srivi - Tamil", "Ramaa - Tamil"
            },
                ["slovak"] = new List<string> {
                "Peter - Slovak"
            },
                ["finnish"] = new List<string> {
                "Aurora - Finnish", "Christoffer - Finnish"
            },
                ["greek"] = new List<string> {
                "Stefanos - Greek"
            },
                ["bulgarian"] = new List<string> {
                "Julian - Bulgarian"
            },
                ["vietnamese"] = new List<string> {
                "Huyen - Vietnamese", "Trung Caha - Vietnamese"
            },
                ["hungarian"] = new List<string> {
                "Magyar - Hungarian"
            },
                ["danish"] = new List<string> {
                "Mathias - Danish"
            },
                ["czech"] = new List<string> {
                "Denisa - Czech", "Adam - Czech"
            },
                ["norwegian"] = new List<string> {
                "Emma-Norwegian", "Johannes-Norwegian"
            }
            };

            // Try to find voices for the specified language
            if (voicesByLanguage.TryGetValue(language, out var voices) && voices.Count > 0)
            {
                // Return a random voice from the list
                Random random = new Random();
                int index = random.Next(voices.Count);
                return voices[index];
            }

            return null;
        }

    }

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





        // Add these enhanced methods to your UltravoxTranslationAdapter class

        public async Task ProcessAudioAsync(byte[] audioData)
        {
            try
            {
                if (_enableFileLogging && !string.IsNullOrEmpty(_incomingAudioFilePath))
                {
                    await File.AppendAllBytesAsync(_incomingAudioFilePath, audioData);
                    _logger.LogDebug("[DIAG] Wrote {Length} bytes to {FilePath}",
                        audioData.Length, _incomingAudioFilePath);
                }

                // Track and log aggregate audio data
                _totalBytesReceived += audioData.Length;
                if (_totalBytesReceived % 50000 < audioData.Length)
                {
                    _logger.LogInformation("[DIAG] Total audio received: {TotalKB}KB, WebSocket state: {State}",
                        _totalBytesReceived / 1024,
                        _ultravoxWebSocket?.State.ToString() ?? "null");
                }

                // Check if we're properly initialized
                if (!_initialized)
                {
                    _logger.LogWarning("[DIAG] Received audio but Ultravox adapter not yet initialized. Buffering: {Length} bytes",
                        audioData.Length);

                    // Store in a buffer if needed, or just skip
                    return;
                }

                // Check if we're in an error state
                if (_errored)
                {
                    _logger.LogWarning("[DIAG] Received audio but Ultravox adapter is in error state. Skipping: {Length} bytes",
                        audioData.Length);
                    return;
                }

                // For Ultravox, just send the raw PCM audio (no base64 encoding needed)
                if (_ultravoxWebSocket != null && _ultravoxWebSocket.State == WebSocketState.Open)
                {
                    _logger.LogDebug("[DIAG] Sending {Length} bytes to Ultravox", audioData.Length);

                    try
                    {
                        await _ultravoxWebSocket.SendAsync(
                            new ArraySegment<byte>(audioData),
                            WebSocketMessageType.Binary,
                            true,
                            _cancellationTokenSource?.Token ?? CancellationToken.None);

                        _logger.LogDebug("[DIAG] Successfully sent {Length} bytes to Ultravox", audioData.Length);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[DIAG] Error sending audio to Ultravox: {Length} bytes", audioData.Length);
                    }
                }
                else
                {
                    _logger.LogWarning("[DIAG] Cannot send audio to Ultravox - WebSocket {State}",
                        _ultravoxWebSocket?.State.ToString() ?? "null");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DIAG] Unhandled exception in ProcessAudioAsync");
            }
        }

        private async Task ProcessUltravoxAudioAsync(byte[] audioData)
        {
            try
            {
                if (_enableFileLogging && !string.IsNullOrEmpty(_outgoingAudioFilePath))
                {
                    await File.AppendAllBytesAsync(_outgoingAudioFilePath, audioData);
                    _logger.LogDebug("[DIAG] Wrote {Length} bytes to {FilePath}",
                        audioData.Length, _outgoingAudioFilePath);
                }

                // Track and log translated audio data
                _totalBytesSent += audioData.Length;
                if (_totalBytesSent % 50000 < audioData.Length)
                {
                    _logger.LogInformation("[DIAG] Received translated audio from Ultravox: {TotalKB}KB, Outgoing WebSocket: {State}",
                        _totalBytesSent / 1024,
                        _outgoingWebSocket?.State.ToString() ?? "null");
                }

                // Send to outgoing WebSocket if available
                if (_outgoingWebSocket != null && _outgoingWebSocket.State == WebSocketState.Open)
                {
                    _logger.LogDebug("[DIAG] Sending {Length} bytes of translated audio to caller", audioData.Length);

                    try
                    {
                        await _outgoingWebSocket.SendAsync(
                            new ArraySegment<byte>(audioData),
                            WebSocketMessageType.Binary,
                            true,
                            CancellationToken.None);

                        _logger.LogDebug("[DIAG] Successfully sent {Length} bytes of translated audio", audioData.Length);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[DIAG] Error sending translated audio to caller: {Length} bytes", audioData.Length);
                    }
                }
                else
                {
                    _logger.LogWarning("[DIAG] Cannot send translated audio - outgoing WebSocket {State}",
                        _outgoingWebSocket?.State.ToString() ?? "null");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DIAG] Unhandled exception in ProcessUltravoxAudioAsync");
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
                _logger.LogInformation("[DIAG] Starting Ultravox message receiver loop");

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

                        if (messageCount <= 5 || messageCount % 100 == 0)
                        {
                            _logger.LogDebug("[DIAG] Ultravox message {Count}: Type={MessageType}, Count={Count}",
                                messageCount, result.MessageType, result.Count);
                        }

                        if (result.MessageType == WebSocketMessageType.Binary)
                        {
                            binaryMessageCount++;

                            // This is audio data from Ultravox
                            var audioData = new byte[result.Count];
                            Array.Copy(buffer, audioData, result.Count);

                            // Log periodically about receiving binary data
                            if (binaryMessageCount % 20 == 0)
                            {
                                _logger.LogInformation("[DIAG] Received {Count} binary messages from Ultravox, latest size: {Size}",
                                    binaryMessageCount, result.Count);
                            }

                            await ProcessUltravoxAudioAsync(audioData);
                        }
                        else if (result.MessageType == WebSocketMessageType.Text)
                        {
                            textMessageCount++;

                            // This is a JSON control message
                            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            _logger.LogInformation("[DIAG] Ultravox control message #{Count}: {Message}",
                                textMessageCount, message);
                        }
                        else if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _logger.LogInformation("[DIAG] Ultravox WebSocket closing: {CloseStatus} {CloseDescription}",
                                result.CloseStatus, result.CloseStatusDescription);
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInformation("[DIAG] Ultravox receive operation canceled");
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[DIAG] Error receiving message {Count} from Ultravox", messageCount);

                        // Don't break - try to continue receiving
                        await Task.Delay(100);
                    }
                }

                _logger.LogInformation("[DIAG] Exited Ultravox receiver loop - WebSocket state: {State}, Token canceled: {Canceled}, Messages: {Total} (Binary: {Binary}, Text: {Text})",
                    _ultravoxWebSocket?.State.ToString() ?? "null",
                    _cancellationTokenSource?.IsCancellationRequested ?? true,
                    messageCount, binaryMessageCount, textMessageCount);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[DIAG] Ultravox message processing canceled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DIAG] Unhandled error in Ultravox message processing");
            }
        }

        // Add these variables to the class
        private long _totalBytesReceived = 0;
        private long _totalBytesSent = 0;







        public void SetIncomingWebSocket(WebSocket webSocket)
        {
            // Check if we're in an error state before setting up socket
            if (_errored)
            {
                _logger.LogWarning("Not setting up incoming socket - adapter is in error state");
                return;
            }

            _incomingWebSocket = webSocket;

            // Setup event handling for the incoming socket in .NET is different
            // We use a Task to continuously read from the socket, but this is
            // managed by the CallSession class now, not here
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

        /// <summary>
        /// Public method to check if adapter is in a working state
        /// </summary>
        public bool IsHealthy()
        {
            return _initialized &&
                   !_errored &&
                   _ultravoxWebSocket?.State == WebSocketState.Open;
        }

        private class CallCreationResult
        {
            public bool Error { get; set; }
            public string? Message { get; set; }
            public string? JoinUrl { get; set; }
        }

        private class UltravoxResponse
        {
            [JsonPropertyName("callId")]
            public string? CallId { get; set; }

            [JsonPropertyName("clientVersion")]
            public string? ClientVersion { get; set; }

            [JsonPropertyName("created")]
            public string? Created { get; set; }

            [JsonPropertyName("joined")]
            public string? Joined { get; set; }

            [JsonPropertyName("ended")]
            public string? Ended { get; set; }

            [JsonPropertyName("endReason")]
            public string? EndReason { get; set; }

            [JsonPropertyName("firstSpeaker")]
            public string? FirstSpeaker { get; set; }

            [JsonPropertyName("firstSpeakerSettings")]
            public FirstSpeakerSettingsObj? FirstSpeakerSettings { get; set; }

            [JsonPropertyName("inactivityMessages")]
            public InactivityMessageObj[]? InactivityMessages { get; set; }

            [JsonPropertyName("initialOutputMedium")]
            public string? InitialOutputMedium { get; set; }

            [JsonPropertyName("joinTimeout")]
            public string? JoinTimeout { get; set; }

            [JsonPropertyName("joinUrl")]
            public string? JoinUrl { get; set; }

            [JsonPropertyName("languageHint")]
            public string? LanguageHint { get; set; }

            [JsonPropertyName("maxDuration")]
            public string? MaxDuration { get; set; }

            [JsonPropertyName("medium")]
            public MediumObj? Medium { get; set; }

            [JsonPropertyName("model")]
            public string? Model { get; set; }

            [JsonPropertyName("recordingEnabled")]
            public bool RecordingEnabled { get; set; }

            [JsonPropertyName("systemPrompt")]
            public string? SystemPrompt { get; set; }

            [JsonPropertyName("temperature")]
            public double Temperature { get; set; }

            [JsonPropertyName("timeExceededMessage")]
            public string? TimeExceededMessage { get; set; }

            [JsonPropertyName("voice")]
            public string? Voice { get; set; }

            [JsonPropertyName("transcriptOptional")]
            public bool TranscriptOptional { get; set; }

            [JsonPropertyName("errorCount")]
            public int ErrorCount { get; set; }

            [JsonPropertyName("vadSettings")]
            public object? VadSettings { get; set; }

            [JsonPropertyName("shortSummary")]
            public string? ShortSummary { get; set; }

            [JsonPropertyName("summary")]
            public string? Summary { get; set; }

            [JsonPropertyName("experimentalSettings")]
            public object? ExperimentalSettings { get; set; }

            [JsonPropertyName("metadata")]
            public Dictionary<string, object>? Metadata { get; set; }

            [JsonPropertyName("initialState")]
            public object? InitialState { get; set; }

            // Nested classes for complex properties
            public class FirstSpeakerSettingsObj
            {
                [JsonPropertyName("user")]
                public UserObj? User { get; set; }

                public class UserObj
                {
                    // Empty class as shown in the JSON
                }
            }

            public class InactivityMessageObj
            {
                [JsonPropertyName("duration")]
                public string? Duration { get; set; }
            }

            public class MediumObj
            {
                [JsonPropertyName("serverWebSocket")]
                public ServerWebSocketObj? ServerWebSocket { get; set; }

                public class ServerWebSocketObj
                {
                    [JsonPropertyName("inputSampleRate")]
                    public int InputSampleRate { get; set; }

                    [JsonPropertyName("outputSampleRate")]
                    public int OutputSampleRate { get; set; }
                }
            }
        }
    }
}