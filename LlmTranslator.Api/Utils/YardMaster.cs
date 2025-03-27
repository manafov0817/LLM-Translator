using System.Collections.Concurrent;
using System.Net.WebSockets;
using LlmTranslator.Api.Models;
using LlmTranslator.Api.Services;

namespace LlmTranslator.Api.Utils
{
    /// <summary>
    /// Manages all call sessions and their associated WebSocket connections
    /// Similar to the yardmaster.js in the Node.js implementation
    /// </summary>
    public class YardMaster
    {
        private readonly ILogger<YardMaster> _logger;
        private readonly ConcurrentDictionary<string, CallSession> _sessions;
        private readonly IServiceProvider _serviceProvider;

        public YardMaster(ILogger<YardMaster> logger, IServiceProvider serviceProvider)
        {
            _logger = logger;
            _sessions = new ConcurrentDictionary<string, CallSession>();
            _serviceProvider = serviceProvider;
        }

        public void AddSession(string callSid)
        {
            var translationService = _serviceProvider.GetRequiredService<ITranslationService>();
            var callSession = new CallSession(callSid, _logger, translationService);

            if (_sessions.TryAdd(callSid, callSession))
            {
                _logger.LogInformation("YardMaster: added session for call_sid {CallSid}, there are {Count} sessions",
                    callSid, _sessions.Count);
            }
            else
            {
                _logger.LogWarning("YardMaster: session already exists for call_sid {CallSid}", callSid);
            }
        }

        public bool HasSession(string callSid)
        {
            return _sessions.ContainsKey(callSid);
        }

        public void AddJambonzWebSocket(WebSocket webSocket, string callSid, string? parentCallSid = null)
        {
            var targetCallSid = parentCallSid ?? callSid;

            if (_sessions.TryGetValue(targetCallSid, out var session))
            {
                session.AddWebSocket(webSocket, callSid);
                _logger.LogInformation("YardMaster: added WebSocket for call_sid {CallSid} to session {ParentCallSid}",
                    callSid, targetCallSid);
            }
            else
            {
                _logger.LogWarning("YardMaster: no session found for call_sid {CallSid}", targetCallSid);

                // Close the WebSocket if no session is found
                CloseWebSocketAsync(webSocket, "No session found").Wait();
            }
        }

        public void Close(string callSid)
        {
            if (_sessions.TryRemove(callSid, out var session))
            {
                try
                {
                    session.Close();
                    _logger.LogInformation("YardMaster: removed session for call_sid {CallSid}, there are {Count} sessions",
                        callSid, _sessions.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error closing session for call_sid {CallSid}", callSid);
                }
            }
        }

        private async Task CloseWebSocketAsync(WebSocket webSocket, string reason)
        {
            try
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        reason,
                        CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing WebSocket");
            }
        }
    }
}