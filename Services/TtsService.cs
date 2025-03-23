using Kanawanagasaki.TwitchHub.Models;

namespace Kanawanagasaki.TwitchHub.Services;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Data;
using System.Text;
using NAudio.Wave;

public class TtsService(IConfiguration _conf, ILogger<TtsService> _logger, IServiceScopeFactory _serviceScopeFactory) : BackgroundService
{
    private List<(string Id, string Username, string Text)> _textToRead = [];
    private AzureTtsVoiceInfo[]? _cachedVoices;

    private (string Id, string Username, string Text)? _currentItem;

    private bool _isEnabled = true;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_conf["Azure:Speech:Key1"]))
        {
            _logger.LogWarning("Azure:Speech:Key1 was empty");
            return;
        }

        AzureTtsVoiceInfo[] voices = await GetVoices();
        if (voices.Length == 0)
        {
            _logger.LogWarning("Azure voices array was empty");
            return;
        }

        using HttpClient http = new();
        http.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", _conf["Azure:Speech:Key1"]);
        http.DefaultRequestHeaders.Add("X-Microsoft-OutputFormat", "riff-24khz-16bit-mono-pcm");
        http.DefaultRequestHeaders.Add("User-Agent", _conf["Azure:Speech:AppName"]);

        while (!ct.IsCancellationRequested)
        {
            if (0 < _textToRead.Count && _isEnabled)
            {
                try
                {
                    lock (_textToRead)
                    {
                        _currentItem = _textToRead[0];
                        _textToRead.RemoveAt(0);
                    }

                    using IServiceScope scope = _serviceScopeFactory.CreateScope();
                    await using SQLiteContext db = scope.ServiceProvider.GetRequiredService<SQLiteContext>();
                    ViewerVoice? voice = await db.ViewerVoices.FirstOrDefaultAsync(v => v.Username == _currentItem.Value.Username);
                    if (voice is null)
                    {
                        AzureTtsVoiceInfo? voiceInfo = voices.Where(v => v.Locale == "en-US").OrderBy(x => Guid.NewGuid()).FirstOrDefault();
                        if (voiceInfo is null)
                            voiceInfo = voices.First();

                        voice = new()
                        {
                            Uuid = Guid.NewGuid(),
                            Username = _currentItem.Value.Username,
                            VoiceName = voiceInfo.ShortName!,
                            Pitch = 0,
                            Rate = 1
                        };
                        db.ViewerVoices.Add(voice);
                        await db.SaveChangesAsync();
                    }

                    string text = _currentItem.Value.Text.Replace("\"", "&quot;")
                                                      .Replace("&", "&amp;")
                                                      .Replace("'", "&apos;")
                                                      .Replace("<", "&lt;")
                                                      .Replace(">", "&gt;");

                    (string word, int pitch, double rate, string style)[] tuples = text.Split(' ').Select(x => (word: x, pitch: voice.Pitch, rate: voice.Rate, style: string.Empty)).ToArray();
                    for (int i = 0; i < tuples.Length; i++)
                    {
                        (string? word, int pitch, double rate, string? style) = tuples[i];

                        if (Uri.TryCreate(word, UriKind.Absolute, out Uri? uri))
                            tuples[i] = ("<break time=\"200ms\" />" + uri.Host + " " + (uri.AbsolutePath.StartsWith("/") ? uri.AbsolutePath.Substring(1) : uri.AbsolutePath) + "<break time=\"200ms\" />", pitch, 2, string.Empty);
                        else if (voice is { Pitch: 0, Rate: 1 } && word.Any(char.IsLetter) && word.All(x => char.IsUpper(x) || !char.IsLetter(x)))
                            tuples[i] = (word, pitch, rate, "shouting");
                    }

                    int lastPitch = voice.Pitch;
                    double lastRate = voice.Rate;
                    string lastStyle = string.Empty;
                    List<string> line = [];
                    List<string> elements = [];

                    foreach ((string? word, int pitch, double rate, string? style) in tuples)
                    {
                        if (pitch != lastPitch || rate != lastRate || style != lastStyle)
                        {
                            if (0 < line.Count)
                            {
                                if (lastStyle != string.Empty)
                                    elements.Add($"""<mstts:express-as style="{lastStyle}">{string.Join(" ", line)}</mstts:express-as>""");
                                else
                                    elements.Add($"""<prosody volume="100" pitch="{(lastPitch < 0 ? "" : "+")}{lastPitch}Hz" rate="{lastRate.ToString().Replace(",", ".")}">{string.Join(" ", line)}</prosody>""");
                            }

                            lastPitch = pitch;
                            lastRate = rate;
                            lastStyle = style;
                            line.Clear();
                        }

                        line.Add(word);
                    }

                    if (0 < line.Count)
                    {
                        if (lastStyle != string.Empty)
                            elements.Add($"""<mstts:express-as style="{lastStyle}">{string.Join(" ", line)}</mstts:express-as>""");
                        else
                            elements.Add($"""<prosody volume="100" pitch="{(lastPitch < 0 ? "" : "+")}{lastPitch}Hz" rate="{lastRate.ToString().Replace(",", ".")}">{string.Join(" ", line)}</prosody>""");
                    }

                    string ssml = $"""
                        <speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="en-US" xmlns:mstts="https://www.w3.org/2001/mstts">
                            <voice name="{voice.VoiceName}">
                                {string.Join("\n", elements)}
                            </voice>
                        </speak>
                        """;

                    StringContent content = new(ssml, Encoding.UTF8, "application/ssml+xml");
                    HttpResponseMessage response = await http.PostAsync(_conf["Azure:Speech:Endpoints:Tts"], content);
                    if (response.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        await using Stream audioStream = await response.Content.ReadAsStreamAsync(ct);
                        await using RawSourceWaveStream audioProvider = new(audioStream, new(24000, 16, 1));
                        using WaveOutEvent waveOut = new();
                        waveOut.Init(audioProvider);
                        waveOut.Play();
                        while (waveOut.PlaybackState == PlaybackState.Playing)
                        {
                            if (_currentItem is null)
                                waveOut.Stop();
                            await Task.Delay(200, ct);
                        }
                    }
                    else
                    {
                        string responseStr = await response.Content.ReadAsStringAsync(ct);
                        _logger.LogWarning($"Failed to fetch audio file for tts: {(int)response.StatusCode} {response.StatusCode}\n{responseStr}");
                    }
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e.Message);
                }
                finally
                {
                    _currentItem = null;
                }
            }
            else await Task.Delay(1000, ct);
        }
    }

    public void AddTextToRead(string messageId, string username, string text)
    {
        if (!_isEnabled)
            return;

        if (text.StartsWith('!'))
            return;

        lock (_textToRead)
            _textToRead.Add((messageId, username, text.Replace("<", "").Replace(">", "")));
    }

    public void DeleteById(string messageId)
    {
        if (_currentItem is not null && _currentItem.Value.Id == messageId)
            _currentItem = null;

        lock (_textToRead)
        {
            int index = _textToRead.FindIndex(x => x.Id == messageId);
            if (0 <= index)
                _textToRead.RemoveAt(index);
        }
    }

    public void DeleteByUsername(string username)
    {
        if (_currentItem is not null && _currentItem.Value.Username == username)
            _currentItem = null;

        lock (_textToRead)
        {
            int index = -1;
            do
            {
                index = _textToRead.FindIndex(x => x.Username == username);
                if (0 <= index)
                    _textToRead.RemoveAt(index);
            }
            while (0 <= index);
        }
    }

    public async Task<AzureTtsVoiceInfo[]> GetVoices()
    {
        if (_cachedVoices is not null)
            return _cachedVoices;

        if (string.IsNullOrWhiteSpace(_conf["Azure:Speech:Key1"]))
            return [];

        using HttpClient http = new();
        http.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", _conf["Azure:Speech:Key1"]);
        HttpResponseMessage response = await http.GetAsync(_conf["Azure:Speech:Endpoints:VoicesList"]);
        if (response.StatusCode == System.Net.HttpStatusCode.OK)
        {
            string json = await response.Content.ReadAsStringAsync();
            _cachedVoices = JsonConvert.DeserializeObject<AzureTtsVoiceInfo[]>(json) ?? [];
            return _cachedVoices;
        }
        else return [];
    }

    public void Enable()
    {
        _isEnabled = true;
        _logger.LogInformation("Tts enabled");
    }

    public void Disable()
    {
        _isEnabled = false;
        _textToRead.Clear();
        _logger.LogInformation("Tts disabled");
    }
}
