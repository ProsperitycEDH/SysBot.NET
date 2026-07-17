using FluentAssertions;
using PKHeX.Core;
using SysBot.Pokemon;
using SysBot.Pokemon.GenBridge;
using System.Collections.Generic;
using Xunit;

namespace SysBot.Tests;

public class EditReturnTests
{
    private sealed class FixedEncounterGender(byte gender) : IFixedGender
    {
        public byte Gender { get; } = gender;
        public bool IsFixedGender => true;
    }

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

    #region IsRemainingTeamOffer

    [Fact]
    public void IsRemainingTeamOffer_MatchingSpeciesAndForm_Accepts()
    {
        var target = new PK9 { Species = (ushort)Species.Skiddo, Form = 0 };
        var offered = new PK9 { Species = (ushort)Species.Skiddo, Form = 0 };
        offered.RefreshChecksum();

        PokeTradeBotSV.IsRemainingTeamOffer(new[] { target }, offered)
            .Should().BeTrue();
    }

    [Fact]
    public void IsRemainingTeamOffer_PreviousSpecies_Rejects()
    {
        var target = new PK9 { Species = (ushort)Species.Skiddo, Form = 0 };
        var offered = new PK9 { Species = (ushort)Species.Mudbray, Form = 0 };
        offered.RefreshChecksum();

        PokeTradeBotSV.IsRemainingTeamOffer(new[] { target }, offered)
            .Should().BeFalse();
    }

    [Fact]
    public void IsRemainingTeamOffer_FormMismatch_Rejects()
    {
        var target = new PK9 { Species = (ushort)Species.Tauros, Form = 1 };
        var offered = new PK9 { Species = (ushort)Species.Tauros, Form = 2 };
        offered.RefreshChecksum();

        PokeTradeBotSV.IsRemainingTeamOffer(new[] { target }, offered)
            .Should().BeFalse();
    }

    [Fact]
    public void IsRemainingTeamOffer_NullOrEmpty_Rejects()
    {
        var target = new PK9 { Species = (ushort)Species.Skiddo, Form = 0 };
        var empty = new PK9();
        empty.RefreshChecksum();

        PokeTradeBotSV.IsRemainingTeamOffer(new[] { target }, null).Should().BeFalse();
        PokeTradeBotSV.IsRemainingTeamOffer(new[] { target }, empty).Should().BeFalse();
    }

    [Fact]
    public void IsRemainingTeamOffer_InvalidChecksum_Rejects()
    {
        var target = new PK9 { Species = (ushort)Species.Skiddo, Form = 0 };
        var offered = new PK9 { Species = (ushort)Species.Skiddo, Form = 0 };

        PokeTradeBotSV.IsRemainingTeamOffer(new[] { target }, offered)
            .Should().BeFalse();
    }

    #endregion

    #region SelectRandomizedGender

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void SelectRandomizedGender_DualGenderSpecies_UsesCoinFlip(bool chooseFemale, byte expected)
    {
        var pokemon = new PK9 { Species = (ushort)Species.Ralts, Form = 0 };

        PokeTradeBotSV.SelectRandomizedGender(pokemon, null, chooseFemale)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectRandomizedGender_GenderlessSpecies_StaysGenderless(bool chooseFemale)
    {
        var pokemon = new PK9 { Species = (ushort)Species.Magnemite, Form = 0 };

        PokeTradeBotSV.SelectRandomizedGender(pokemon, null, chooseFemale)
            .Should().Be(EntityGender.Genderless);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectRandomizedGender_MaleOnlySpecies_StaysMale(bool chooseFemale)
    {
        var pokemon = new PK9 { Species = (ushort)Species.Tauros, Form = 0 };

        PokeTradeBotSV.SelectRandomizedGender(pokemon, null, chooseFemale)
            .Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectRandomizedGender_FemaleOnlySpecies_StaysFemale(bool chooseFemale)
    {
        var pokemon = new PK9 { Species = (ushort)Species.Happiny, Form = 0 };

        PokeTradeBotSV.SelectRandomizedGender(pokemon, null, chooseFemale)
            .Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectRandomizedGender_FixedEncounter_OverridesCoinFlip(bool chooseFemale)
    {
        var pokemon = new PK9 { Species = (ushort)Species.Ralts, Form = 0 };
        var fixedFemale = new FixedEncounterGender(1);

        PokeTradeBotSV.SelectRandomizedGender(pokemon, fixedFemale, chooseFemale)
            .Should().Be(1);
    }

    #endregion

    #region ShouldKeepTeamTradeBoxOpen

    [Fact]
    public void ShouldKeepTeamTradeBoxOpen_IntermediateEditReturnSuccess_StaysOpen()
    {
        PokeTradeBotSV.ShouldKeepTeamTradeBoxOpen(
            PokeTradeType.EditReturn, remaining: 2, anotherAttemptAllowed: true, PokeTradeResult.Success)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldKeepTeamTradeBoxOpen_NoRemainingTargets_Exits()
    {
        PokeTradeBotSV.ShouldKeepTeamTradeBoxOpen(
            PokeTradeType.EditReturn, remaining: 0, anotherAttemptAllowed: true, PokeTradeResult.Success)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldKeepTeamTradeBoxOpen_NoAttemptRemaining_Exits()
    {
        PokeTradeBotSV.ShouldKeepTeamTradeBoxOpen(
            PokeTradeType.EditReturn, remaining: 2, anotherAttemptAllowed: false, PokeTradeResult.Success)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldKeepTeamTradeBoxOpen_FailedTrade_Exits()
    {
        PokeTradeBotSV.ShouldKeepTeamTradeBoxOpen(
            PokeTradeType.EditReturn, remaining: 2, anotherAttemptAllowed: true, PokeTradeResult.TrainerTooSlow)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldKeepTeamTradeBoxOpen_NonEditReturn_Exits()
    {
        PokeTradeBotSV.ShouldKeepTeamTradeBoxOpen(
            PokeTradeType.Specific, remaining: 2, anotherAttemptAllowed: true, PokeTradeResult.Success)
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
