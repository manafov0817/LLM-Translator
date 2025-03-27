using System.Net.WebSockets;

namespace LlmTranslator.Api.Services
{
    /// <summary>
    /// Interface for translation services (OpenAI, Ultravox, etc.)
    /// </summary>
    public interface ITranslationService
    {
        /// <summary>
        /// Creates a translation adapter for a specific language pair
        /// </summary>
        /// <param name="logger">Logger for the adapter</param>
        /// <param name="sourceLanguage">Source language</param>
        /// <param name="targetLanguage">Target language</param>
        /// <returns>A translation adapter</returns>
        ITranslationAdapter CreateTranslationAdapter(ILogger logger, string sourceLanguage, string targetLanguage);
    }

    /// <summary>
    /// Interface for a translation adapter that handles WebSocket connections
    /// and audio processing
    /// </summary>
    public interface ITranslationAdapter
    {
        /// <summary>
        /// Sets the WebSocket for incoming audio
        /// </summary>
        /// <param name="webSocket">WebSocket</param>
        void SetIncomingWebSocket(WebSocket webSocket);

        /// <summary>
        /// Sets the WebSocket for outgoing audio
        /// </summary>
        /// <param name="webSocket">WebSocket</param>
        void SetOutgoingWebSocket(WebSocket webSocket);

        /// <summary>
        /// Processes audio data
        /// </summary>
        /// <param name="audioData">Audio data as bytes</param>
        Task ProcessAudioAsync(byte[] audioData);

        /// <summary>
        /// Closes the adapter and any connections
        /// </summary>
        void Close();
    }
}

namespace LlmTranslator.Api.Models
{
    /// <summary>
    /// Jambonz webhook request model
    /// </summary>
    public class JambonzRequest
    {
        public string? EventType { get; set; }
        public string? CallSid { get; set; }
        public string? ParentCallSid { get; set; }
        public string? From { get; set; }
        public string? To { get; set; }
        public string? Direction { get; set; }
        // Add other properties as needed
    }

    /// <summary>
    /// Jambonz webhook response model
    /// </summary>
    public class JambonzResponse
    {
        public string? Verb { get; set; }
        public string? ActionHook { get; set; }
        public string[]? Input { get; set; }
        public object? Listen { get; set; }
        public object[]? Dub { get; set; }
        public object? Transcribe { get; set; }
        public object[]? CallActions { get; set; }
    }
}