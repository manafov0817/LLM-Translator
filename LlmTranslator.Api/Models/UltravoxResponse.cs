using System.Text.Json.Serialization;

namespace LlmTranslator.Api.Models
{
    public class UltravoxResponse
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


        public class InactivityMessageObj
        {
            [JsonPropertyName("duration")]
            public string? Duration { get; set; }
        }


    }
    public class FirstSpeakerSettingsObj
    {
        [JsonPropertyName("user")]
        public UserObj? User { get; set; }

        public class UserObj
        {
            // Empty class as shown in the JSON
        }
    }
    public class MediumObj
    {
        [JsonPropertyName("serverWebSocket")]
        public ServerWebSocketObj? ServerWebSocket { get; set; }
    }
    public class ServerWebSocketObj
    {
        [JsonPropertyName("inputSampleRate")]
        public int InputSampleRate { get; set; }

        [JsonPropertyName("outputSampleRate")]
        public int OutputSampleRate { get; set; }
    }
}
