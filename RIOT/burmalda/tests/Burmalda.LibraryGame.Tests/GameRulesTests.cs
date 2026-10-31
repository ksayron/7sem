using System.Numerics;
using Burmalda.LibraryGame;
using Xunit;

namespace Burmalda.LibraryGame.Tests;

public sealed class GameRulesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-29T12:00:00Z");
    private static GameState New() => new() { UserId = Guid.NewGuid() };
    private static GameChange Do(GameState state, string type, string? book = null, int? series = null, string? upgrade = null) =>
        GameRules.Execute(state, new(Guid.NewGuid(), state.Version, type, book, series, upgrade), Now);
    [Fact] public void FullCampaignUnlocksSectionsAndAwardsExactly72Sparks()
    {
        var state = New();
        for (var s = 0; s < 6; s++) for (var v = 1; v <= 12; v++) {
            Assert.True(s < state.OpenSeries);
            var picked = Do(state, "PickBook", LibraryWorld.BookId(s, v));
            Assert.Empty(state.Held); // Rules never mutate the input save.
            var shelved = Do(picked.State, "PlaceBook", series: s);
            Assert.Equal("BookShelved", Assert.Single(shelved.Events).EventType);
            state = shelved.State;
        }
        Assert.True(state.Complete); Assert.Equal(72, state.Sparks); Assert.Equal(144, state.Version); Assert.Empty(state.Held);
    }
    [Theory][InlineData(0,2)][InlineData(23,2)][InlineData(24,4)][InlineData(47,4)][InlineData(48,6)]
    public void SectionThresholds(int count, int expected)
    {
        var state = New(); for (var i = 0; i < count; i++) state.Shelved.Add(LibraryWorld.BookId(i / 12, i % 12 + 1)); Assert.Equal(expected, state.OpenSeries);
    }
    [Fact] public void WrongShelfAndDuplicateBookCannotAwardSparks()
    {
        var picked = Do(New(), "PickBook", "0:1").State;
        Assert.Throws<GameRuleException>(() => Do(picked, "PlaceBook", series: 1));
        Assert.Throws<GameRuleException>(() => Do(picked, "PickBook", "0:1"));
        var placed = Do(picked, "PlaceBook", series: 0).State;
        Assert.Throws<GameRuleException>(() => Do(placed, "PickBook", "0:1")); Assert.Equal(1, placed.Sparks);
    }
    [Theory][InlineData("6:1")][InlineData("-1:1")][InlineData("0:13")][InlineData("0:0")][InlineData("00:1")][InlineData("oops")]
    public void InvalidBookIdsRejected(string id) => Assert.Throws<GameRuleException>(() => Do(New(), "PickBook", id));
    [Fact] public void LockedSectionAndFullHandsRejected()
    {
        Assert.Throws<GameRuleException>(() => Do(New(), "PickBook", "2:1"));
        var state = Do(New(), "PickBook", "0:1").State;
        Assert.Throws<GameRuleException>(() => Do(state, "PickBook", "0:2"));
    }
    [Fact] public void UpgradeCostsAreEnforcedAndCannotBePurchasedTwiceAtMax()
    {
        var state = New(); state.Sparks = 26;
        state = Do(state, "BuyUpgrade", upgrade: "hands").State; Assert.Equal(3, state.Capacity);
        state = Do(state, "BuyUpgrade", upgrade: "hands").State; Assert.Equal(5, state.Capacity);
        state = Do(state, "BuyUpgrade", upgrade: "attraction").State; Assert.Equal(4, state.AttractionLimit); Assert.Equal(0, state.Sparks);
        Assert.Throws<GameRuleException>(() => Do(state, "BuyUpgrade", upgrade: "hands"));
        Assert.Throws<GameRuleException>(() => Do(New(), "BuyUpgrade", upgrade: "hands"));
    }
    [Fact] public void AttractionIsAsynchronousBoundedAndIdempotent()
    {
        var state = New(); state.Capacity = 5;
        for (var v = 1; v <= 6; v++) state.Shelved.Add($"0:{v}");
        state.Held.Add("1:1");
        var change = Do(state, "UseAbility");
        Assert.Single(change.State.Held); var op = change.State.Operations.Single(); Assert.Equal("Pending", op.Status);
        var applied = GameRules.ApplyAttraction(change.State, op.Id);
        Assert.Equal(new[] { "1:1", "1:2", "1:3" }, applied.Held);
        Assert.Equal(2, applied.Operations[0].Moved);
        var duplicate = GameRules.ApplyAttraction(applied, op.Id);
        Assert.Equal(applied.Version, duplicate.Version); Assert.Equal(applied.Held, duplicate.Held);
        Assert.Throws<GameRuleException>(() => Do(applied, "UseAbility")); // Cooldown.
    }
    [Fact] public void LateAttractionRechecksFreeSlotsAndCanFinishWithoutEffect()
    {
        var state = New(); state.Capacity = 3; state.Held.AddRange(["0:1","0:2","0:3"]);
        var op = Guid.NewGuid(); state.Operations.Add(new(op, 0, "Pending"));
        var applied = GameRules.ApplyAttraction(state, op); Assert.Equal("NoEffect", applied.Operations[0].Status); Assert.Equal(3, applied.Held.Count);
    }
    [Fact] public void StaleCommandRejectedAndEventCarriesIdentity()
    {
        var state = New(); state.Version = 8;
        var ex = Assert.Throws<GameRuleException>(() => GameRules.Execute(state, new(Guid.NewGuid(), 7, "PickBook", "0:1"), Now)); Assert.Equal(409, ex.Status);
        var change = Do(state, "PickBook", "0:1"); var e = Assert.Single(change.Events);
        Assert.Equal(state.UserId, e.UserId); Assert.Equal(state.SessionId, e.SessionId); Assert.Equal(Now, e.Timestamp); Assert.Equal(9, e.StateVersion);
    }
    [Fact] public void MovementCollidesWithWallsAndShelvesAndRayHasLimitedReach()
    {
        var pos = new Vector3(9.2f,1.65f,0); Assert.Equal(pos, LibraryWorld.Move(pos,new(1,0,0)));
        Assert.Null(LibraryWorld.RayBox(Vector3.Zero, -Vector3.UnitZ, new(0,0,-10), Vector3.One));
        Assert.NotNull(LibraryWorld.RayBox(Vector3.Zero, -Vector3.UnitZ, new(0,0,-2), Vector3.One));
    }
}
