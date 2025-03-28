using LlmTranslator.Api.Models;
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

    
}