using System.Text;
using NexusPipeline.Core.Configuration;

namespace NexusPipeline.Platform.Storage.Configuration;

internal sealed class ConfigurationUtf8Offsets
{
    internal string Text { get; }
    private readonly int[] _offsets;

    internal ConfigurationUtf8Offsets(ReadOnlySpan<byte> bytes, int offset)
    {
        Text = new UTF8Encoding(false, true).GetString(bytes[offset..]);
        _offsets = new int[Text.Length + 1];
        var total = offset;
        for (var index = 0; index < Text.Length; index++)
        {
            _offsets[index] = total;
            var width = char.IsHighSurrogate(Text[index]) ? 2 : 1;
            total += Encoding.UTF8.GetByteCount(Text.AsSpan(index, width));
            if (width == 2) _offsets[++index] = -1;
        }
        _offsets[^1] = total;
    }

    internal int Byte(long character)
    {
        if (character < 0 || character >= _offsets.Length || _offsets[(int)character] < 0)
            throw new ConfigurationException("configuration_span_invalid");
        return _offsets[(int)character];
    }

    internal int TrimBlockEnd(long start, long end)
    {
        var result = checked((int)end);
        while (result > start && Text[result - 1] is ' ' or '\t' or '\r' or '\n') result--;
        return Byte(result);
    }

    internal int FlowEnd(long character, char closing)
    {
        // YamlDotNet's flow end event points at the closing delimiter, excluding that character.
        if (character < 0 || character >= Text.Length || Text[(int)character] != closing)
            throw new ConfigurationException("configuration_span_invalid");
        return Byte(character + 1);
    }
}
