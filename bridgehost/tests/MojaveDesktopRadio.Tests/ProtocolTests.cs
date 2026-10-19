using System.Text.Json;
using MojaveDesktopRadio.Fx;
using MojaveDesktopRadio.Ipc;
using Xunit;

namespace MojaveDesktopRadio.Tests;

public class ProtocolTests
{
    [Fact]
    public void Built_messages_carry_version_and_type()
    {
        var json = Protocol.Build("state", w => w.WriteString("title", "Ağır Roman – İstanbul"));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("state", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("Ağır Roman – İstanbul", doc.RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain('\n', json);
    }

    [Fact]
    public void Parses_flat_messages_with_lenient_types()
    {
        Assert.True(InboundMessage.TryParse("{\"v\":1,\"type\":\"config\",\"fxEnabled\":1,\"volume\":\"0.5\",\"preset\":\"Damaged\"}", out var m, out _));
        Assert.Equal("config", m!.Type);
        Assert.Equal(1, m.Version);
        Assert.True(m.GetBool("fxEnabled"));
        Assert.Equal(0.5, m.GetDouble("volume"));
        Assert.True(FxSettings.TryParsePreset(m.GetString("preset"), out var p));
        Assert.Equal(RadioPreset.Damaged, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"v\":1}")]
    public void Rejects_invalid_messages(string line)
    {
        Assert.False(InboundMessage.TryParse(line, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Error_messages_include_id_and_code()
    {
        using var doc = JsonDocument.Parse(Protocol.Error(5, "player_not_running", "x"));
        Assert.Equal("error", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(5, doc.RootElement.GetProperty("id").GetInt64());
        Assert.Equal("player_not_running", doc.RootElement.GetProperty("code").GetString());
    }
}
