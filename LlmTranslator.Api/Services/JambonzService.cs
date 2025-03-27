using LlmTranslator.Api.Models;
using System.Text;
using System.Text.Json;

namespace LlmTranslator.Api.Services
{
    /// <summary>
    /// Service for interacting with the Jambonz API
    /// </summary>
    public class JambonzService
    {
        private readonly ILogger<JambonzService> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _accountSid;
        private readonly string _apiKey;
        private readonly string _apiBaseUrl;

        public JambonzService(ILogger<JambonzService> logger, IConfiguration configuration, HttpClient? httpClient = null)
        {
            _logger = logger;
            _httpClient = httpClient ?? new HttpClient();

            // Get Jambonz API configuration
            _accountSid = configuration["Jambonz:AccountSid"] ??
                throw new ArgumentNullException("Jambonz:AccountSid is not configured");
            _apiKey = configuration["Jambonz:ApiKey"] ??
                throw new ArgumentNullException("Jambonz:ApiKey is not configured");
            _apiBaseUrl = configuration["Jambonz:ApiBaseUrl"] ?? "https://api.jambonz.cloud";

            // Set default headers
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        }

        /// <summary>
        /// Gets information about a call
        /// </summary>
        /// <param name="callSid">The call SID</param>
        /// <returns>Call information</returns>
        public async Task<CallInfo?> GetCallAsync(string callSid)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_apiBaseUrl}/v1/Accounts/{_accountSid}/Calls/{callSid}");
                response.EnsureSuccessStatusCode();

                return await response.Content.ReadFromJsonAsync<CallInfo>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting call information for {CallSid}", callSid);
                return null;
            }
        }

        /// <summary>
        /// Updates a call with new instructions
        /// </summary>
        /// <param name="callSid">The call SID</param>
        /// <param name="jambonzResponse">The response containing new instructions</param>
        /// <returns>True if successful</returns>
        public async Task<bool> UpdateCallAsync(string callSid, JambonzResponse jambonzResponse)
        {
            try
            {
                var json = JsonSerializer.Serialize(jambonzResponse);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PutAsync(
                    $"{_apiBaseUrl}/v1/Accounts/{_accountSid}/Calls/{callSid}",
                    content);

                response.EnsureSuccessStatusCode();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating call {CallSid}", callSid);
                return false;
            }
        }

        /// <summary>
        /// Makes an outbound call
        /// </summary>
        /// <param name="from">From number</param>
        /// <param name="to">To number or target</param>
        /// <param name="webhook">Webhook URL to handle the call</param>
        /// <returns>Call SID if successful</returns>
        public async Task<string?> MakeCallAsync(string from, string to, string webhook)
        {
            try
            {
                var callRequest = new
                {
                    from,
                    to = new[]
                    {
                        new
                        {
                            type = to.StartsWith("sip:") ? "sip" : "phone",
                            number = to.StartsWith("sip:") ? null : to,
                            sipUri = to.StartsWith("sip:") ? to : null
                        }
                    },
                    webhook_url = webhook,
                    call_status_webhook_url = $"{webhook}-status"
                };

                var json = JsonSerializer.Serialize(callRequest);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(
                    $"{_apiBaseUrl}/v1/Accounts/{_accountSid}/Calls",
                    content);

                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<CallResponse>();
                return result?.CallSid;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error making outbound call from {From} to {To}", from, to);
                return null;
            }
        }

        /// <summary>
        /// Ends a call
        /// </summary>
        /// <param name="callSid">The call SID</param>
        /// <returns>True if successful</returns>
        public async Task<bool> EndCallAsync(string callSid)
        {
            try
            {
                var response = await _httpClient.DeleteAsync(
                    $"{_apiBaseUrl}/v1/Accounts/{_accountSid}/Calls/{callSid}");

                response.EnsureSuccessStatusCode();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ending call {CallSid}", callSid);
                return false;
            }
        }
    }

    public class CallInfo
    {
        public string? CallSid { get; set; }
        public string? ParentCallSid { get; set; }
        public string? From { get; set; }
        public string? To { get; set; }
        public string? Status { get; set; }
        public string? Direction { get; set; }
        // Add other properties as needed
    }

    public class CallResponse
    {
        public string? CallSid { get; set; }
        public string? Status { get; set; }
    }
}