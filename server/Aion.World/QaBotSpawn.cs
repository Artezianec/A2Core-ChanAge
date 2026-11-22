namespace Aion.World;

public sealed record QaBotSpawnContext(int Slot, Guid AccountId, WorldTurn Turn, int CharacterLevel);

public sealed record QaBotSpawnBinding(INativeCharacterStore Store, GameSessionOptions SessionOptions, IAsyncDisposable? Lifetime);