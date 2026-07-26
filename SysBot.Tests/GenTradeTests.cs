using FluentAssertions;
using PKHeX.Core;
using SysBot.Pokemon;
using SysBot.Pokemon.GenBridge;
using System.Collections.Generic;
using Xunit;

namespace SysBot.Tests;

/// <summary>
/// From-scratch gen: the host-trainer registration that gives generated Pokémon their origin, and
/// the provenance gate that decides which origins are acceptable to fabricate.
/// </summary>
[Collection("Generation")]
public class GenTradeTests
{
    private const string HostOT = "Ricky";
    private const ushort HostTID16 = 51234;

    private static ITrainerInfo GetHostSAV()
    {
        AutoLegalityWrapper.EnsureInitialized(new Pokemon.LegalitySettings());
        AutoLegalityWrapper.RegisterHostTrainer(new SimpleTrainerInfo(GameVersion.SV)
        {
            OT = HostOT,
            TID16 = HostTID16,
            SID16 = 4321,
            Gender = 0,
            Language = 2,
            Generation = 9,
        });
        return AutoLegalityWrapper.GetTrainerInfo<PK9>();
    }

    private static PK9 Generate(string set)
    {
        var sav = GetHostSAV();
        var pk = sav.GetLegal(AutoLegalityWrapper.GetTemplate(new ShowdownSet(set)), out _);
        return (PK9)(EntityConverter.ConvertToType(pk, typeof(PK9), out _) ?? pk);
    }

    private const string Garchomp = """
        Garchomp @ Life Orb
        Ability: Rough Skin
        Level: 50
        Tera Type: Steel
        EVs: 252 Atk / 4 Def / 252 Spe
        Jolly Nature
        - Earthquake
        - Dragon Claw
        - Swords Dance
        - Protect
        """;

    private const string Dragapult = """
        Dragapult @ Choice Specs
        Ability: Infiltrator
        Level: 50
        Tera Type: Dragon
        EVs: 252 SpA / 4 SpD / 252 Spe
        Timid Nature
        - Draco Meteor
        - Shadow Ball
        - Flamethrower
        - U-turn
        """;

    #region RegisterHostTrainer

    [Fact]
    public void RegisterHostTrainer_GeneratedMonCarriesHostTrainer()
    {
        var pokemon = Generate(Garchomp);

        pokemon.OriginalTrainerName.Should().Be(HostOT);
        pokemon.TID16.Should().Be(HostTID16);
    }

    [Fact]
    public void RegisterHostTrainer_GeneratedMonIsLegal()
    {
        var pokemon = Generate(Garchomp);

        new LegalityAnalysis(pokemon).Valid.Should().BeTrue();
    }

    [Fact]
    public void RegisterHostTrainer_SetsRegisteredFlag()
    {
        _ = GetHostSAV();

        AutoLegalityWrapper.HostTrainerRegistered.Should().BeTrue();
    }

    #endregion

    #region CheckGenProvenance

    [Fact]
    public void CheckGenProvenance_OrdinaryCompetitiveMon_Accepted()
    {
        var pkms = new List<PKM> { Generate(Garchomp) };

        GenBridge<PK9>.CheckGenProvenance(pkms).Should().BeNull();
    }

    [Fact]
    public void CheckGenProvenance_MultipleOrdinaryMons_Accepted()
    {
        var pkms = new List<PKM> { Generate(Garchomp), Generate(Dragapult) };

        GenBridge<PK9>.CheckGenProvenance(pkms).Should().BeNull();
    }

    [Fact]
    public void CheckGenProvenance_UntradeableForm_Rejected()
    {
        // A fused Calyrex can't cross a trade at all, so it can never be delivered whatever its
        // legality says.
        var pkms = new List<PKM> { new PK9 { Species = (ushort)Species.Calyrex, Form = 1 } };

        var rejection = GenBridge<PK9>.CheckGenProvenance(pkms);

        rejection.Should().NotBeNull();
        rejection.Should().Contain("Calyrex");
    }

    #endregion

    #region ShouldKeepTeamTradeBoxOpen

    [Fact]
    public void ShouldKeepTeamTradeBoxOpen_IntermediateGenSuccess_StaysOpen()
    {
        PokeTradeBotSV.ShouldKeepTeamTradeBoxOpen(
            PokeTradeType.Gen, remaining: 2, anotherAttemptAllowed: true, PokeTradeResult.Success)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldKeepTeamTradeBoxOpen_FinalGenTrade_Exits()
    {
        PokeTradeBotSV.ShouldKeepTeamTradeBoxOpen(
            PokeTradeType.Gen, remaining: 0, anotherAttemptAllowed: true, PokeTradeResult.Success)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldKeepTeamTradeBoxOpen_FailedGenTrade_Exits()
    {
        PokeTradeBotSV.ShouldKeepTeamTradeBoxOpen(
            PokeTradeType.Gen, remaining: 2, anotherAttemptAllowed: true, PokeTradeResult.IllegalTrade)
            .Should().BeFalse();
    }

    #endregion
}
