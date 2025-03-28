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
            _logger.LogInformation("Received translate webhook");

            try
            {
                string callSid = GetStringProperty(request, "call_sid");
                string direction = GetStringProperty(request, "direction");

                if (string.IsNullOrEmpty(callSid))
                {
                    return BadRequest(new { error = "Missing call_sid" });
                }

                if (direction == "inbound")
                {
                    _yardMaster.AddSession(callSid);

                    string from = GetStringProperty(request, "from");
                    string to = GetStringProperty(request, "to");

                    if (string.IsNullOrEmpty(from))
                    {
                        from = GetStringProperty(request, "caller_name");
                    }

                    int sampleRate = GetSampleRate();
                    bool lowerVolume = !string.IsNullOrEmpty(_configuration["LowerVolume"]);
                    string callerIdOverride = _configuration["CallerIdOverride"] ?? from;

                    var target = CreateTarget(to);

                    var response = new JsonArray();

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

                    if (lowerVolume)
                    {
                        configAction["boostAudioSignal"] = _configuration["LowerVolume"];
                    }

                    response.Add(configAction);

                    var dialAction = new JsonObject
                    {
                        ["verb"] = "dial",
                        ["callerId"] = callerIdOverride,
                        ["target"] = target,
                        ["listen"] = new JsonObject
                        {
                            ["url"] = "/audio-stream",
                            ["channel"] = 2,
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

                    var dialDubArray = new JsonArray();
                    dialDubArray.Add(new JsonObject
                    {
                        ["action"] = "addTrack",
                        ["track"] = "a_translated"
                    });
                    dialAction["dub"] = dialDubArray;

                    response.Add(dialAction);

                    response.Add(new JsonObject
                    {
                        ["verb"] = "hangup"
                    });

                    return Ok(response);
                }
                else
                {
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
            _logger.LogInformation("Received translate action webhook");
            return Ok(new JsonArray());
        }

        [HttpPost("status")]
        public IActionResult CallStatus([FromBody] JsonElement request)
        {
            _logger.LogInformation("Call status update received");

            try
            {
                string callStatus = GetStringProperty(request, "call_status");
                string callSid = GetStringProperty(request, "call_sid");

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

            return Ok();
        }

        private string GetStringProperty(JsonElement element, string propertyName, string defaultValue = "")
        {
            if (element.TryGetProperty(propertyName, out JsonElement property) &&
                property.ValueKind == JsonValueKind.String)
            {
                return property.GetString() ?? defaultValue;
            }
            return defaultValue;
        }

        private int GetSampleRate()
        {
            if (!string.IsNullOrEmpty(_configuration["OpenAI:ApiKey"]))
            {
                return 24000;
            }
            return 8000;
        }

        private JsonNode CreateTarget(string to)
        {
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

                _logger.LogWarning("Unrecognized OUTBOUND_OVERRIDE format: {Override}, using default target",
                    outboundOverride);
            }

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