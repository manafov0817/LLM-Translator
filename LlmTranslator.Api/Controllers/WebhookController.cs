using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmTranslator.Api.Utils;

namespace LlmTranslator.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class WebhookController : ControllerBase
    {
        private readonly ILogger<WebhookController> _logger;
        private readonly YardMaster _yardMaster;
        private readonly IConfiguration _configuration;

        public WebhookController(
            ILogger<WebhookController> logger,
            YardMaster yardMaster,
            IConfiguration configuration)
        {
            _logger = logger;
            _yardMaster = yardMaster;
            _configuration = configuration;
        }

        [HttpPost("translate")]
        public IActionResult TranslateCall([FromBody] JsonElement request)
        {
            _logger.LogInformation("Received translate webhook: {Request}", JsonSerializer.Serialize(request));

            try
            {
                // Extract call information using our helper method for safety
                string callSid = GetStringProperty(request, "call_sid");
                string direction = GetStringProperty(request, "direction");

                // Log the entire request for debugging
                _logger.LogDebug("Request fields: call_sid={CallSid}, direction={Direction}", callSid, direction);

                if (string.IsNullOrEmpty(callSid))
                {
                    _logger.LogWarning("Missing call_sid in request");
                    return BadRequest(new { error = "Missing call_sid" });
                }

                // Check if this is an inbound call
                if (direction == "inbound")
                {
                    // Create a new call session
                    _yardMaster.AddSession(callSid);

                    // Get the from and to numbers
                    string from = GetStringProperty(request, "from");
                    string to = GetStringProperty(request, "to");

                    // If from/to are empty, try alternate properties
                    if (string.IsNullOrEmpty(from))
                    {
                        from = GetStringProperty(request, "caller_name");
                    }

                    _logger.LogInformation("Processing inbound call: from={From}, to={To}", from, to);

                    // Get configuration values with defaults
                    int sampleRate = GetSampleRate();
                    bool lowerVolume = !string.IsNullOrEmpty(_configuration["LowerVolume"]);
                    string callerIdOverride = _configuration["CallerIdOverride"] ?? from;

                    _logger.LogDebug("Using settings: sampleRate={SampleRate}, callerIdOverride={CallerId}",
                        sampleRate, callerIdOverride);

                    // Set up target from TO address
                    var target = CreateTarget(to);
                    _logger.LogDebug("Created target: {Target}", JsonSerializer.Serialize(target));

                    // Build the response
                    _logger.LogDebug("Building response JSON");
                    var response = new JsonArray();

                    // Add config action with WebSocket setup for caller (A leg)
                    var configAction = new JsonObject
                    {
                        ["verb"] = "config",
                        ["listen"] = new JsonObject
                        {
                            ["enable"] = true,
                            ["url"] = "/audio-stream",
                            ["mixType"] = "mono",
                            ["sampleRate"] = sampleRate,
                            ["bidirectionalAudio"] = new JsonObject
                            {
                                ["enabled"] = true,
                                ["streaming"] = true,
                                ["sampleRate"] = sampleRate
                            }
                        }
                    };

                    // Add optional volume adjustment if configured
                    if (lowerVolume)
                    {
                        configAction["boostAudioSignal"] = _configuration["LowerVolume"];
                    }

                    

                    // Add the config action to the response
                    response.Add(configAction);

                    // Add dial action for called party (B leg)
                    var dialAction = new JsonObject
                    {
                        ["verb"] = "dial",
                        ["callerId"] = "+17692481301",
                        ["target"] = target,
                        ["listen"] = new JsonObject
                        {
                            ["url"] = "/audio-stream",
                            ["channel"] = 2,  // Stream only called party audio
                            ["mixType"] = "mono",
                            ["sampleRate"] = sampleRate,
                            ["bidirectionalAudio"] = new JsonObject
                            {
                                ["enabled"] = true,
                                ["streaming"] = true,
                                ["sampleRate"] = sampleRate
                            }
                        }
                    };

                    // Add dub track for the called party (B leg)
                    var dialDubArray = new JsonArray();
                    dialDubArray.Add(new JsonObject
                    {
                        ["action"] = "addTrack",
                        ["track"] = "a_translated"
                    });
                    dialAction["dub"] = dialDubArray;

                    // Add the dial action to the response
                    response.Add(dialAction);

                    // Add hangup verb to match the Node.js implementation
                    response.Add(new JsonObject
                    {
                        ["verb"] = "hangup"
                    });

                    // Log the complete response for debugging
                    _logger.LogDebug("Returning response: {Response}", JsonSerializer.Serialize(response));

                    return Ok(response);
                }
                else
                {
                    _logger.LogInformation("Received non-inbound direction: {Direction}", direction);
                    // Handle other direction types if needed
                    return Ok(new JsonArray());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing translate webhook");
                return StatusCode(500, new
                {
                    error = ex.Message
                });
            }
        }

        [HttpPost("translate-action")]
        public IActionResult TranslateAction([FromBody] JsonElement request)
        {
            _logger.LogInformation("Received translate action webhook: {Request}", JsonSerializer.Serialize(request));

            // Handle any actions after the call is established
            // For now, we just return an empty response
            return Ok(new JsonArray());
        }

        [HttpPost("status")]
        public IActionResult CallStatus([FromBody] JsonElement request)
        {
            _logger.LogInformation("Call status update received: {Request}", JsonSerializer.Serialize(request));

            try
            {
                // Use our helper method to safely extract properties
                string callStatus = GetStringProperty(request, "call_status");
                string callSid = GetStringProperty(request, "call_sid");

                _logger.LogDebug("Status update: call_status={CallStatus}, call_sid={CallSid}", callStatus, callSid);

                // Handle call completion to clean up resources
                if (callStatus == "completed" && !string.IsNullOrEmpty(callSid))
                {
                    if (_yardMaster.HasSession(callSid))
                    {
                        _yardMaster.Close(callSid);
                        _logger.LogInformation("Closed session for completed call: {CallSid}", callSid);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling call status webhook");
            }

            // No response is needed for status webhooks
            return Ok();
        }

        /// <summary>
        /// Helper method to safely extract a string property from JsonElement
        /// </summary>
        private string GetStringProperty(JsonElement element, string propertyName, string defaultValue = "")
        {
            if (element.TryGetProperty(propertyName, out JsonElement property) &&
                property.ValueKind == JsonValueKind.String)
            {
                return property.GetString() ?? defaultValue;
            }
            return defaultValue;
        }

        /// <summary>
        /// Get the appropriate sample rate based on which translation service is configured
        /// </summary>
        private int GetSampleRate()
        {
            // Use 24000 for OpenAI or 8000 for Ultravox, matching the Node.js implementation
            if (!string.IsNullOrEmpty(_configuration["OpenAI:ApiKey"]))
            {
                return 24000;
            }
            return 8000;
        }

        /// <summary>
        /// Creates a target for outbound dialing
        /// </summary>
        private JsonNode CreateTarget(string to)
        {
            // Check configuration for override setting
            var outboundOverride = _configuration["OutboundOverride"];

            if (!string.IsNullOrEmpty(outboundOverride))
            {
                if (outboundOverride.StartsWith("phone:"))
                {
                    string phone = outboundOverride.Substring(6);
                    _logger.LogInformation("Using phone override: {Phone}", phone);

                    var targetArray = new JsonArray();
                    targetArray.Add(new JsonObject
                    {
                        ["type"] = "phone",
                        ["number"] = phone
                    });

                    return targetArray;
                }
                else if (outboundOverride.StartsWith("user:"))
                {
                    string user = outboundOverride.Substring(5);
                    _logger.LogInformation("Using user override: {User}", user);

                    var targetArray = new JsonArray();
                    targetArray.Add(new JsonObject
                    {
                        ["type"] = "user",
                        ["name"] = user
                    });

                    return targetArray;
                }
                else if (outboundOverride.StartsWith("sip:"))
                {
                    _logger.LogInformation("Using sip override: {Sip}", outboundOverride);

                    var targetArray = new JsonArray();
                    targetArray.Add(new JsonObject
                    {
                        ["type"] = "sip",
                        ["sipUri"] = outboundOverride
                    });

                    return targetArray;
                }

                _logger.LogWarning("Unrecognized OUTBOUND_OVERRIDE format: {Override}, using default target: {To}",
                    outboundOverride, to);
            }

            // Default to the provided 'to' number
            var defaultTargetArray = new JsonArray();
            defaultTargetArray.Add(new JsonObject
            {
                ["type"] = "phone",
                ["number"] = to
            });

            return defaultTargetArray;
        }
    }
}