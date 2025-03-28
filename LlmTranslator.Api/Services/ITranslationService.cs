using System.Net.WebSockets;

namespace LlmTranslator.Api.Services
{
    public interface ITranslationService
    {
        ITranslationAdapter CreateTranslationAdapter(ILogger logger, string sourceLanguage, string targetLanguage);
    }

    public interface ITranslationAdapter
    {
        void SetIncomingWebSocket(WebSocket webSocket);
        void SetOutgoingWebSocket(WebSocket webSocket);
        Task ProcessAudioAsync(byte[] audioData);
        void Close();
    }
}

namespace LlmTranslator.Api.Models
{
    public class JambonzRequest
    {
        public string? EventType { get; set; }
        public string? CallSid { get; set; }
        public string? ParentCallSid { get; set; }
        public string? From { get; set; }
        public string? To { get; set; }
        public string? Direction { get; set; }
    }

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