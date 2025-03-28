namespace LlmTranslator.Api.Models
{
    public class CallCreationResult
    {
        public bool Error { get; set; }
        public string? Message { get; set; }
        public string? JoinUrl { get; set; }
    }
}
