using Aion.Application.Authentication;
using Aion.Application.Characters;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Aion.World;

namespace Aion.Host.Native;

public sealed class QaBotSessionFactory(
    int worldId,
    QaInternalWorldSessions qaSessions,
    IWireCharacterRepository wire,
    IGameplayStore game,
    CharacterPersistenceService persistence,
    IQunaWallet? quna,
    Func<Guid, CancellationToken, Task<string?>>? staffRole)
{
    public QaBotSpawnBinding Bind(QaBotSpawnContext context) =>
        Bind(context.Slot, context.AccountId, context.Turn, context.CharacterLevel);

    public QaBotSpawnBinding Bind(int slotIndex, Guid accountId, WorldTurn turn, int characterLevel)
    {
        var identity = new WorldIdentity(new VerifiedAccount(accountId), worldId);
        var token = qaSessions.Register(identity);
        var inner = new ServiceNativeCharacterStore(token, identity, qaSessions, wire, game, persistence, quna);
        var store = TurnStore.Wrap(inner, turn);
        var options = new GameSessionOptions
        {
            WorldId = worldId,
            CharacterLevel = characterLevel,
            StaffRole = staffRole,
        };
        return new(store, options, inner);
    }
}