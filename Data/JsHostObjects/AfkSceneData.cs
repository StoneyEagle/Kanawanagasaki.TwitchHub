using System.Collections.ObjectModel;
using Newtonsoft.Json;

namespace Kanawanagasaki.TwitchHub.Data.JsHostObjects;

public class AfkSceneData
{
    public string bg { get; set; } = "#1e1e1e";

    public ArrayJs<SymbolData> symbols { get; private set; } = new(Array.Empty<SymbolData>());

    internal void SetContent(string content)
    {
        if (!string.IsNullOrWhiteSpace(content))
        {
            symbols = new(content.Select((ch, i)
                =>
                {
                    SymbolData l = new(ch);
                    l.x = (-(content.Length * 24) / 2 + 12) + i * 24;
                    return l;
                }).ToArray());
        }
        else symbols = new(Array.Empty<SymbolData>());
    }

    public override string ToString()
        => @$"{{ ""bg"":""{bg}"", ""symbols"":SymbolJs[{symbols.Count}] }}";
}

public class SymbolData
{
    public readonly char symbol;

    public float x;
    public float y = 0;

    public float size = 36;

    public string color = "#ffffff";
    public string shadow = "none";

    public SymbolData(char ch)
    {
        symbol = ch;
    }

    public override string ToString()
        => JsonConvert.SerializeObject(this);
}

public class ArrayJs<T> : ReadOnlyCollection<T>
{
    public ArrayJs(IList<T> list) : base(list) { }
    public int length => Count;

    public override string ToString()
        => $"{typeof(T).Name}[{Count}]";
}
