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
                string callSid = GetStringProperty(request, "call_sid");
                string direction = GetStringProperty(request, "direction");

                _logger.LogDebug("Request fields: call_sid={CallSid}, direction={Direction}", callSid, direction);

                if (string.IsNullOrEmpty(callSid))
                {
                    _logger.LogWarning("Missing call_sid in request");
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

                    _logger.LogInformation("Processing inbound call: from={From}, to={To}", from, to);

                    int sampleRate = GetSampleRate();
                    bool lowerVolume = !string.IsNullOrEmpty(_configuration["LowerVolume"]);
                    string callerIdOverride = _configuration["CallerIdOverride"] ?? from;

                    _logger.LogDebug("Using settings: sampleRate={SampleRate}, callerIdOverride={CallerId}",
                        sampleRate, callerIdOverride);

                    var target = CreateTarget(to);
                    _logger.LogDebug("Created target: {Target}", JsonSerializer.Serialize(target));

                    _logger.LogDebug("Building response JSON");
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
                        ["callerId"] = "+17692481301",
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

                    _logger.LogDebug("Returning response: {Response}", JsonSerializer.Serialize(response));

                    return Ok(response);
                }
                else
                {
                    _logger.LogInformation("Received non-inbound direction: {Direction}", direction);
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

            return Ok(new JsonArray());
        }

        [HttpPost("status")]
        public IActionResult CallStatus([FromBody] JsonElement request)
        {
            _logger.LogInformation("Call status update received: {Request}", JsonSerializer.Serialize(request));

            try
            {
                string callStatus = GetStringProperty(request, "call_status");
                string callSid = GetStringProperty(request, "call_sid");

                _logger.LogDebug("Status update: call_status={CallStatus}, call_sid={CallSid}", callStatus, callSid);

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

                _logger.LogWarning("Unrecognized OUTBOUND_OVERRIDE format: {Override}, using default target: {To}",
                    outboundOverride, to);
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