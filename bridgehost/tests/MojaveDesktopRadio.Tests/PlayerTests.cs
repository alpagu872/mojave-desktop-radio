using MojaveDesktopRadio.Config;
using MojaveDesktopRadio.Media;
using Xunit;

namespace MojaveDesktopRadio.Tests;

public class PlayerTests
{
    [Fact]
    public void Default_player_is_Spotify()
    {
        var p = new BridgeSettings().ResolvePlayer();
        Assert.Equal("spotify", p.Key);
        Assert.Equal(["Spotify"], p.SessionMatch);
        Assert.True(p.Tested);
    }

    [Fact]
    public void Edited_lists_from_old_settings_become_custom()
    {
        // settings.json from 0.2.1 or older: no "player", lists edited by hand
        var s = new BridgeSettings { SessionMatch = ["foobar2000"], AudioProcessNames = ["foobar2000"] };
        var p = s.ResolvePlayer();
        Assert.Equal("custom", p.Key);
        Assert.Equal("foobar2000", p.DisplayName);
        Assert.Equal(["foobar2000"], p.ProcessNames);
    }

    [Theory]
    [InlineData("foobar2000", "foobar2000")]
    [InlineData("WACUP", "wacup")]
    [InlineData(" musicbee ", "musicbee")]
    [InlineData("nonsense", "spotify")]
    public void Player_keys_resolve_with_Spotify_fallback(string key, string expected)
    {
        var s = new BridgeSettings { SessionMatch = ["foobar2000"] };
        Assert.Equal(expected, s.ResolvePlayer(key).Key);
    }

    [Fact]
    public void Custom_player_uses_the_settings_lists()
    {
        var s = new BridgeSettings
        {
            Player = "custom", CustomPlayerName = "My Player",
            SessionMatch = ["  ", "MyPlayer.exe"], AudioProcessNames = ["myplayer"],
        };
        var p = s.ResolvePlayer();
        Assert.Equal("My Player", p.DisplayName);
        Assert.Equal(["MyPlayer.exe"], p.SessionMatch);
    }

    [Fact]
    public void Built_in_player_ignores_the_custom_lists()
    {
        var s = new BridgeSettings { Player = "spotify", SessionMatch = ["foobar2000"], AudioProcessNames = ["foobar2000"] };
        Assert.Equal(["Spotify"], s.ResolvePlayer().ProcessNames);
    }

    [Fact]
    public void Mcm_order_covers_every_player()
    {
        // MCM "Desktop Player" option index = position in McmOrder (the plugin uses the same order)
        Assert.Equal(PlayerProfiles.BuiltIn.Select(p => p.Key).Append(PlayerProfiles.CustomKey), PlayerProfiles.McmOrder);
        Assert.Equal("spotify", PlayerProfiles.McmOrder[0]);
    }
}
