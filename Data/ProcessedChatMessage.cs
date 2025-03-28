namespace Kanawanagasaki.TwitchHub.Data;

using Models;
using Services;
using TwitchLib.Client.Models;
using System.Globalization;

public class ProcessedChatMessage
{
    public TwitchAuthModel BotAuth { get; private set; }
    public ChatMessage Original { get; private set; }
    public RenderFragments Fragments { get; private set; }

    public List<MessagePart> ParsedMessage { get; private set; } = [];

    public bool IsCommand { get; private set; }
    public string? CommandName { get; private set; }
    public string[] CommandArgs { get; private set; } = [];
    public bool ShouldReply { get; private set; }
    public string? Reply { get; private set; }

    public List<object> CustomContent { get; private set; } = [];

    public string? ImageUrl { get; private set; }

    public string? YoutubeVideoId { get; private set; }

    public TwitchGetUsersResponse? Sender { get; private set; }
    public string? Color { get; private set; }

    public ProcessedChatMessage(TwitchAuthModel botAuth, ChatMessage originalMessage)
    {
        BotAuth = botAuth;
        Original = originalMessage;
        Fragments = RenderFragments.Message | RenderFragments.OriginalMessage;
    }

    public ProcessedChatMessage AsCommand(string commandName, string[] args)
    {
        IsCommand = true;
        CommandName = commandName;
        CommandArgs = args;
        return this;
    }

    public ProcessedChatMessage WithoutMessage()
    {
        Fragments &= ~RenderFragments.Message;
        return this;
    }

    public ProcessedChatMessage WithoutOriginalMessage()
    {
        Fragments &= ~RenderFragments.OriginalMessage;
        return this;
    }

    public ProcessedChatMessage WithCustomContent(object content)
    {
        CustomContent.Add(content);
        Fragments |= RenderFragments.CustomContent;
        return this;
    }

    public ProcessedChatMessage WithReply(string reply)
    {
        Reply = reply;
        ShouldReply = true;
        return this;
    }

    public ProcessedChatMessage WithImage(string url)
    {
        ImageUrl = url;
        Fragments |= RenderFragments.Image;
        return this;
    }

    public ProcessedChatMessage WithYoutubeVideo(string videoId)
    {
        YoutubeVideoId = videoId;
        Fragments |= RenderFragments.YoutubeVideo;
        return this;
    }

    public ProcessedChatMessage WithCode(CodeContent code)
    {
        Fragments |= RenderFragments.Code;
        return WithCustomContent(code);
    }

    public void SetColor(string color)
    {
        Color = color;
    }

    public void SetUser(TwitchGetUsersResponse? user)
    {
        Sender = user;
    }

    public void ParseEmotes(Dictionary<string, ThirdPartyEmote> thirdPartyEmotes)
    {
        List<string> parts = [];
        List<(string code, string url)> emoteUrls = [];
        IOrderedEnumerable<Emote> emotes = Original.EmoteSet.Emotes.OrderBy(x => x.StartIndex);
        StringInfo stringInfo = new(Original.Message);
        int leftIndex = 0;
        foreach (Emote? emote in emotes)
        {
            string leftPart = stringInfo.SubstringByTextElements(leftIndex, emote.StartIndex - leftIndex);
            parts.Add(leftPart);
            emoteUrls.Add((emote.Name, $"https://static-cdn.jtvnw.net/emoticons/v2/{emote.Id}/default/dark/1.0"));
            leftIndex = emote.EndIndex + 1;
        }

        if (leftIndex < stringInfo.LengthInTextElements)
        {
            string leftOverPart = stringInfo.SubstringByTextElements(leftIndex);
            if (0 < leftOverPart.Length)
                parts.Add(leftOverPart);
        }

        for (int i = 0; i < parts.Count; i++)
        {
            string[] split = parts[i].Split(" ");
            for (int j = 0; j < split.Length; j++)
            {
                if (!thirdPartyEmotes.TryGetValue(split[j], out ThirdPartyEmote? emote))
                    continue;

                parts[i] = string.Join(" ", split.Take(j)) + " ";
                parts.Insert(i + 1, string.Join(" ", split.Skip(j + 1)));

                emoteUrls.Insert(i, (emote.code, emote.url));

                i--;
                break;
            }
        }

        {
            ParsedMessage.Clear();
            int i;
            for (i = 0; i < parts.Count; i++)
            {
                ParsedMessage.Add(new(false, parts[i]));
                if (i < emoteUrls.Count)
                    ParsedMessage.Add(new(true, emoteUrls[i].code, emoteUrls[i].url));
            }
            for (; i < emoteUrls.Count; i++)
                ParsedMessage.Add(new(true, emoteUrls[i].code, emoteUrls[i].url));
        }
    }

    [Flags]
    public enum RenderFragments
    {
        None = 0,
        Message = 1,
        OriginalMessage = 2,
        CustomContent = 4,
        Image = 8,
        YoutubeVideo = 16,
        HtmlPreview = 32,
        Code = 64
    }

    public record MessagePart(bool IsEmote, string Content, string? EmoteUrl = null);
}