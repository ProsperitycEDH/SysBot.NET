using FluentAssertions;
using PKHeX.Core;
using SysBot.Pokemon;
using SysBot.Pokemon.GenBridge;
using System.Collections.Generic;
using Xunit;

namespace SysBot.Tests;

public class EditReturnTests
{
    #region SplitTeamPaste

    [Fact]
    public void SplitTeamPaste_SingleSet_ReturnsOneChunk()
    {
        var input = "Pikachu\nAbility: Static\n- Thunder Shock";
        var chunks = GenBridge<PK9>.SplitTeamPaste(input);
        chunks.Should().HaveCount(1);
        chunks[0].Should().Contain("Pikachu");
    }

    [Fact]
    public void SplitTeamPaste_TwoSetsSplitByBlankLine_ReturnsTwoChunks()
    {
        var input = "Pikachu\nAbility: Static\n\nCharizard\nAbility: Blaze";
        var chunks = GenBridge<PK9>.SplitTeamPaste(input);
        chunks.Should().HaveCount(2);
        chunks[0].Should().Contain("Pikachu");
        chunks[1].Should().Contain("Charizard");
    }

    [Fact]
    public void SplitTeamPaste_CrlfInput_HandlesCorrectly()
    {
        var input = "Pikachu\r\nAbility: Static\r\n\r\nCharizard\r\nAbility: Blaze";
        var chunks = GenBridge<PK9>.SplitTeamPaste(input);
        chunks.Should().HaveCount(2);
        chunks[0].Should().Contain("Pikachu");
        chunks[1].Should().Contain("Charizard");
    }

    [Fact]
    public void SplitTeamPaste_MultipleConsecutiveBlankLines_ReturnsCorrectChunks()
    {
        var input = "Pikachu\n\n\n\nCharizard";
        var chunks = GenBridge<PK9>.SplitTeamPaste(input);
        chunks.Should().HaveCount(2);
    }

    [Fact]
    public void SplitTeamPaste_WhitespaceOnlySeparatorLines_ReturnsCorrectChunks()
    {
        var input = "Pikachu\n   \n\t\nCharizard";
        var chunks = GenBridge<PK9>.SplitTeamPaste(input);
        chunks.Should().HaveCount(2);
    }

    [Fact]
    public void SplitTeamPaste_LeadingTrailingBlankLines_ReturnsCorrectChunks()
    {
        var input = "\n\nPikachu\n\nCharizard\n\n";
        var chunks = GenBridge<PK9>.SplitTeamPaste(input);
        chunks.Should().HaveCount(2);
        chunks[0].Should().Contain("Pikachu");
        chunks[1].Should().Contain("Charizard");
    }

    #endregion

    #region ShouldContinueEditReturnSession

    [Fact]
    public void ShouldContinueEditReturnSession_SuccessContinues()
    {
        PokeTradeBotSV.ShouldContinueEditReturnSession(remaining: 3, attempts: 1, cap: 8, PokeTradeResult.Success)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(PokeTradeResult.IllegalTrade)]
    [InlineData(PokeTradeResult.TrainerRequestBad)]
    [InlineData(PokeTradeResult.TrainerTooSlow)]
    public void ShouldContinueEditReturnSession_ContinuableResults(PokeTradeResult result)
    {
        PokeTradeBotSV.ShouldContinueEditReturnSession(remaining: 2, attempts: 1, cap: 6, result)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldContinueEditReturnSession_NoRemainingStops()
    {
        PokeTradeBotSV.ShouldContinueEditReturnSession(remaining: 0, attempts: 1, cap: 8, PokeTradeResult.Success)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldContinueEditReturnSession_AttemptsAtCapStops()
    {
        PokeTradeBotSV.ShouldContinueEditReturnSession(remaining: 2, attempts: 6, cap: 6, PokeTradeResult.Success)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldContinueEditReturnSession_AttemptsBelowCapContinues()
    {
        PokeTradeBotSV.ShouldContinueEditReturnSession(remaining: 2, attempts: 5, cap: 6, PokeTradeResult.Success)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldContinueEditReturnSession_NoTrainerFoundStops()
    {
        PokeTradeBotSV.ShouldContinueEditReturnSession(remaining: 2, attempts: 1, cap: 8, PokeTradeResult.NoTrainerFound)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldContinueEditReturnSession_RecoverStartStops()
    {
        PokeTradeBotSV.ShouldContinueEditReturnSession(remaining: 2, attempts: 1, cap: 8, PokeTradeResult.RecoverStart)
            .Should().BeFalse();
    }

    #endregion

    #region FindTeamTargetIndex

    [Fact]
    public void FindTeamTargetIndex_MatchReturnsIndex()
    {
        var targets = new List<PK9>
        {
            new PK9 { Species = (ushort)Species.Pikachu, Form = 0 },
            new PK9 { Species = (ushort)Species.Charizard, Form = 0 },
        };
        PokeTradeBotSV.FindTeamTargetIndex(targets, (ushort)Species.Pikachu, 0)
            .Should().Be(0);
        PokeTradeBotSV.FindTeamTargetIndex(targets, (ushort)Species.Charizard, 0)
            .Should().Be(1);
    }

    [Fact]
    public void FindTeamTargetIndex_NoMatchReturnsMinusOne()
    {
        var targets = new List<PK9>
        {
            new PK9 { Species = (ushort)Species.Pikachu, Form = 0 },
        };
        PokeTradeBotSV.FindTeamTargetIndex(targets, (ushort)Species.Charizard, 0)
            .Should().Be(-1);
    }

    [Fact]
    public void FindTeamTargetIndex_FormMismatchReturnsMinusOne()
    {
        var targets = new List<PK9>
        {
            new PK9 { Species = (ushort)Species.Pikachu, Form = 0 },
        };
        PokeTradeBotSV.FindTeamTargetIndex(targets, (ushort)Species.Pikachu, 1)
            .Should().Be(-1);
    }

    [Fact]
    public void FindTeamTargetIndex_FirstOfDuplicatesReturnsFirst()
    {
        var targets = new List<PK9>
        {
            new PK9 { Species = (ushort)Species.Pikachu, Form = 0 },
            new PK9 { Species = (ushort)Species.Pikachu, Form = 0 },
        };
        PokeTradeBotSV.FindTeamTargetIndex(targets, (ushort)Species.Pikachu, 0)
            .Should().Be(0);
    }

    #endregion
}
