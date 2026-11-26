using Aion.Application.Characters;
using Aion.Application.Chat;
using Aion.Host.Chat;
using Aion.Application.Authentication;
using Aion.Application.Gameplay;
using Aion.Host.Authentication;
using Aion.Host.Native;
using Aion.Host.Platform;
using Aion.Host.NcGuard;
using Aion.Host;
using Aion.Host.Database;
using Aion.Host.Debug;
using Aion.Host.Logging;
using Aion.World;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Net;
using Aion.Core;
using Aion.Infrastructure.Postgres;
using Npgsql;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
var clientProtocol = ClientProtocolBootstrap.Load(builder.Configuration);
Console.WriteLine($"[ClientProtocol] {clientProtocol.Kind} version={clientProtocol.VersionId} profile={clientProtocol.ProtocolProfileId}");
// Counters for the staff diagnostics API (docs/DIAGNOSTICS.md); also counts logged errors per category.
var diagnostics = new Aion.Host.Diagnostics.HostDiagnostics();
builder.Services.AddSingleton(diagnostics);
builder.Logging.AddProvider(diagnostics);
builder.Logging.AddServerFileLog(builder.Configuration);
// Multi-world: a host in world role (Aion:Role=world) runs one world's game port only: no login port, account site,
// platform bootstrap, NCGuard or chat port. The primary host starts managed ones (Aion.Host.Worlds.WorldSupervisor);
// remote ones run on other machines against the same database.
var worldProcess = Aion.Host.Worlds.WorldProcessOptions.From(builder.Configuration);
if (worldProcess is not null) builder.Services.AddSingleton(worldProcess);
var debugOptions = builder.Configuration.GetSection(AionConfiguration.Debug).Get<DebugOptions>() ?? new DebugOptions();
if (debugOptions.Enabled)
{
    builder.Services.AddSingleton(_ => OpcodeCatalog.Load());
    builder.Services.AddSingleton(debugOptions);
    builder.Services.AddSingleton<PlaytestDebugSink>();
}
// Bind and advertised addresses (loopback unless configured for a public server).
var network = builder.Configuration.GetSection(AionConfiguration.Network).Get<NetworkOptions>() ?? new NetworkOptions();
network.Validate();
builder.Services.AddSingleton(network);
var progressionSection = builder.Configuration.GetSection(AionConfiguration.Progression).Get<ProgressionOptions>() ?? new ProgressionOptions();
var levelCap = progressionSection.LevelCap is > 0 and <= ProgressionOptions.TableMaxLevel
    ? progressionSection.LevelCap
    : ProgressionOptions.DefaultLevelCap;
builder.Services.AddSingleton(new ProgressionOptions { LevelCap = levelCap });
builder.Services.AddSingleton(_ => Aion.World.GameData.Embedded.WithLevelCap(levelCap));
builder.Services.AddSingleton<IItemStatRoller>(sp => new ItemStatRollerService(sp.GetRequiredService<GameData>(), Random.Shared));
if (builder.Configuration["Aion:Debug:ShopFile"] is { Length: > 0 } shopDebug)
    Aion.World.GameSession.ConfigureShopDebug(shopDebug);
var shopPremium = Aion.World.ShopOptions.Parse(builder.Configuration["Aion:Shop:PremiumMode"]);
Aion.World.GameSession.ConfigureShopOptions(new Aion.World.ShopOptions
{
    PremiumMode = shopPremium,
    PassUnlockPrice = Math.Clamp(builder.Configuration.GetValue("Aion:Shop:PassUnlockPrice", Aion.World.GameSession.PaidPassUnlockQunaCost), 0, 1_000_000),
});
{
    var shop = builder.Configuration.GetSection("Aion:Shop");
    Aion.World.GameSession.ConfigureShopFeatures(new Aion.World.ShopFeatureOptions
    {
        UseMileageCurrency = shop.GetValue("UseMileageCurrency", true),
        MileageEarnPercent = Math.Clamp(shop.GetValue("MileageEarnPercent", 10), 0, 100),
        MailDeliveryEnabled = shop.GetValue("MailDeliveryEnabled", true),
        RefundWindowHours = Math.Clamp(shop.GetValue("RefundWindowHours", 168), 1, 24 * 365),
        GiftingEnabled = shop.GetValue("GiftingEnabled", true),
        PackageBonusesEnabled = shop.GetValue("PackageBonusesEnabled", true),
    });
}
try { Aion.World.ShopPackageCatalog.Embedded.ValidateAgainstOfficialParts(); }
catch (Exception error) { throw new InvalidOperationException("shop_package_tiers.json failed validation against official_shop parts", error); }
// Dungeon waves and boss phases (docs/BOSSES.md): Aion:Bosses (Phases, Engine, Frenzy, FrenzySeconds, DamageScale, Waves, WavePartSeconds).
// Engine (default true) runs bosses on the mechanics engine (docs/BOSS-MECHANICS-ENGINE.md); false keeps the legacy hand-coded fights.
Aion.World.GameSession.ConfigureBosses(builder.Configuration.GetSection("Aion:Bosses").Get<Aion.World.BossOptions>() ?? Aion.World.BossOptions.Default);
Aion.World.GameSession.ConfigureCombatTelegraphs(
    builder.Configuration.GetSection("Aion:Combat").Get<Aion.World.CombatTelegraphOptions>() ?? Aion.World.CombatTelegraphOptions.Default);
// Open-world field bosses (docs/FIELD-BOSSES.md): Aion:FieldBosses (Enabled, Boss/Named respawn windows, RespawnMinutesByName,
// Announcements, SharedLoot). Official CANDIDATE ranges by default (named 1.5-6 h, bosses 6-12 h).
Aion.World.FieldBosses.Configure(builder.Configuration.GetSection("Aion:FieldBosses").Get<Aion.World.FieldBossOptions>() ?? Aion.World.FieldBossOptions.Default);
Aion.World.AbyssRiftZone.Configure(builder.Configuration.GetSection("Aion:AbyssRiftZone").Get<Aion.World.AbyssRiftZoneOptions>() ?? Aion.World.AbyssRiftZoneOptions.Default);
Aion.World.GameSession.FieldEventSettings = builder.Configuration.GetSection("Aion:FieldEvents").Get<Aion.World.FieldEventOptions>() ?? Aion.World.FieldEventOptions.Default;
// Aion:Stigmas (docs/STIGMAS.md): off by default; Enabled, Slots, ShardRate, MaxLevel, ResetKinah, RequireSlot, ShardUpdateSendsSpent.
Aion.World.GameSession.ConfigureStigmas(builder.Configuration.GetSection("Aion:Stigmas").Get<Aion.World.StigmaOptions>() ?? Aion.World.StigmaOptions.Default);
// Base rates (OURS; from ckrealm/A2Core): Aion:Rates (Profile official|accelerated, Experience, Kinah, Drop, QuestExperience).
// Scheduled dashboard events multiply on top (SharedWorld.ExpRate / DropRate, each clamped 0.1–10).
var serverRates = (builder.Configuration.GetSection(AionConfiguration.Rates).Get<Aion.World.RatesOptions>() ?? new Aion.World.RatesOptions()).Resolve();
builder.Services.AddSingleton(serverRates);
Aion.World.GameSession.ConfigureMembership(builder.Configuration.GetValue("Aion:World:MembershipByDefault", true));
// Aion:Rules (OURS; ported from ckrealm/A2Core): PartyLoot off|freeforall|roundrobin|killer, FallbackLoot, MonsterKinahDrops (all off by default).
Aion.World.GameSession.ConfigureWorldRules(
    (builder.Configuration.GetSection(AionConfiguration.Rules).Get<Aion.World.WorldRules>() ?? Aion.World.WorldRules.Default).Validated());
var connectionString = builder.Configuration.GetConnectionString("Aion");
var authConnectionString = builder.Configuration.GetConnectionString("Auth");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connectionString));
    builder.Services.AddSingleton<PostgresCharacterRepository>();
    builder.Services.AddSingleton<ICharacterRepository>(provider => provider.GetRequiredService<PostgresCharacterRepository>());
    builder.Services.AddSingleton<ICharacterStateRepository>(provider => provider.GetRequiredService<PostgresCharacterRepository>());
    builder.Services.AddSingleton<IWireCharacterRepository>(provider => provider.GetRequiredService<PostgresCharacterRepository>());
    builder.Services.AddSingleton<CharacterService>();
    builder.Services.AddSingleton<CharacterPersistenceService>();
    builder.Services.AddSingleton<IGameplayStore>(sp => new PostgresGameplayStore(
        sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<ProgressionOptions>(), sp.GetService<IItemStatRoller>(),
        sp.GetService<ILogger<PostgresGameplayStore>>()));
    builder.Services.AddHostedService<StarterProfileHealth>();
    builder.Services.AddSingleton<IGameTicketRedeemer, PostgresGameTicketRedeemer>();
    // The account Quna wallets (migration 111, docs/QUNA_WALLET.md); every change is pushed to the account's sessions.
    builder.Services.AddSingleton<PostgresQunaWallet>();
    builder.Services.AddSingleton<IQunaWallet>(provider =>
        provider.GetService<Aion.World.WorldPresence>() is { } presence
            ? new PushingQunaWallet(provider.GetRequiredService<PostgresQunaWallet>(), presence, provider.GetRequiredService<ILogger<PushingQunaWallet>>())
            : provider.GetRequiredService<PostgresQunaWallet>());
    builder.Services.AddSingleton<PostgresMileageWallet>();
    builder.Services.AddSingleton<IMileageWallet>(provider =>
        provider.GetService<Aion.World.WorldPresence>() is { } presence
            ? new PushingMileageWallet(provider.GetRequiredService<PostgresMileageWallet>(), presence, provider.GetRequiredService<ILogger<PushingMileageWallet>>())
            : provider.GetRequiredService<PostgresMileageWallet>());
    builder.Services.AddSingleton<IWorldRegistry, PostgresWorldRegistry>();
    // Custom NPCs (migration 122, docs/CUSTOM_NPCS.md): edited on the dashboard, placed by every world on this database.
    builder.Services.AddSingleton<PostgresCustomNpcs>();
    builder.Services.AddSingleton<ICustomNpcAdmin>(provider => provider.GetRequiredService<PostgresCustomNpcs>());
    builder.Services.AddSingleton<ICustomNpcCatalog>(provider => provider.GetRequiredService<PostgresCustomNpcs>());
    // Field boss respawn timers (migration 126, docs/FIELD-BOSSES.md): kept across a restart, scoped to this host's world.
    builder.Services.AddSingleton<IFieldBossStore>(provider => new PostgresFieldBosses(
        provider.GetRequiredService<NpgsqlDataSource>(),
        provider.GetService<NativeOptions>()?.WorldId ?? Aion.World.DevelopmentDefaults.WorldId));
}
var accountsReady = !string.IsNullOrWhiteSpace(connectionString) && !string.IsNullOrWhiteSpace(authConnectionString);
if (!accountsReady)
    builder.Services.AddSingleton<IPlatformSecondaryAuth, Aion.Host.Platform.NullPlatformSecondaryAuth>();
if (accountsReady)
{
    builder.Services.AddSingleton<PostgresAccountAuthentication>(provider =>
    {
        var auth = new PostgresAccountAuthentication(authConnectionString!);
        var totpKey = builder.Configuration["Aion:Totp:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(totpKey) && builder.Configuration["Aion:BridgeSecret"] is { Length: >= 32 } bridge)
            totpKey = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(bridge)));
        if (!string.IsNullOrWhiteSpace(totpKey)) auth.ConfigureTotp(totpKey);
        return auth;
    });
    builder.Services.AddSingleton<IAccountAuthentication>(provider => provider.GetRequiredService<PostgresAccountAuthentication>());
    builder.Services.AddSingleton<IClientHandoff>(provider => provider.GetRequiredService<PostgresAccountAuthentication>());
    builder.Services.AddSingleton<IAccountManagement>(provider => provider.GetRequiredService<PostgresAccountAuthentication>());
    builder.Services.AddSingleton<ITotpAuthentication>(provider => provider.GetRequiredService<PostgresAccountAuthentication>());
    builder.Services.AddSingleton<IPlatformSecondaryAuth>(provider => provider.GetRequiredService<PostgresAccountAuthentication>());
    builder.Services.AddSingleton<IWorldSessions>(provider => provider.GetRequiredService<PostgresAccountAuthentication>());
    builder.Services.AddSingleton<IStaffSessions>(provider => provider.GetRequiredService<PostgresAccountAuthentication>());
    builder.Services.AddSingleton<IAccountDirectory>(provider => provider.GetRequiredService<PostgresAccountAuthentication>());
    if (worldProcess is null) builder.Services.AddHostedService<AuthenticationCleanup>();
    // Staff tools: maintenance, sanctions, chat log, tickets, events, population, economy (migration 028).
    builder.Services.AddSingleton<IStaffTools, PostgresStaffTools>();
    builder.Services.AddSingleton<Aion.Host.Authentication.ServerControl>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<Aion.Host.Authentication.ServerControl>());
}
// Optional Lime chat API for the dev client (policy ChatServerURL), on its own loopback port.
var chatPort = worldProcess is null ? builder.Configuration.GetValue<int?>("Aion:ChatPort") : null;
builder.Services.AddSingleton(TimeProvider.System);
// --- Chat identity (server/CHAT.md) ---
// Development logins accept the character the client names (explicit switch only). Otherwise
// a chat login needs the account's game ticket and binds to the account's character in the
// world on this host (native game listener with account logins); without those, every chat
// login is rejected.
var chatServerKey = builder.Configuration["Aion:ChatServerKey"] ?? Aion.World.DevelopmentDefaults.WorldIdString;
var chatWorldId = int.TryParse(chatServerKey, out var parsedChatWorld) ? parsedChatWorld : Aion.World.DevelopmentDefaults.WorldId;
static WorldChatDirectory? ChatDirectory(IServiceProvider provider, int world) =>
    provider.GetService<Aion.World.WorldPresence>() is { } presence && provider.GetService<IWireCharacterRepository>() is { } characters
        ? new WorldChatDirectory(presence, characters, world) : null;
builder.Services.AddSingleton(provider => new ChatRelay(provider.GetRequiredService<TimeProvider>(), ChatDirectory(provider, chatWorldId)));
builder.Services.AddSingleton<StompHub>();
if (builder.Configuration.GetValue<bool>("Aion:ChatDevelopmentLogins"))
    builder.Services.AddSingleton<IChatLoginVerifier, DevelopmentChatLoginVerifier>();
else
    builder.Services.AddSingleton<IChatLoginVerifier>(provider =>
        provider.GetService<IClientHandoff>() is { } handoff && ChatDirectory(provider, chatWorldId) is { } directory
            ? new AccountChatLoginVerifier(handoff, directory, chatServerKey, chatWorldId)
            : new RejectingChatLoginVerifier());
// --- end chat identity ---
// Native client ports (port of tools/login_server.py and game_server.py) on the network bind address.
// "accounts" logins check launch tickets and store characters; "development" accepts any
// local client with an unsaved character and must be chosen explicitly.
var worldOptions = builder.Configuration.GetSection(AionConfiguration.World).Get<WorldOptions>() ?? new WorldOptions();
builder.Services.AddSingleton(worldOptions);
var askNpcFallback = worldOptions.ResolveMapEventAskNpcProximityFallback(debugOptions);
var headlessClients = builder.Configuration.GetSection(AionConfiguration.HeadlessClients).Get<HeadlessClientOptions>() ?? new HeadlessClientOptions();
builder.Services.AddSingleton(headlessClients);
builder.Services.AddSingleton<HeadlessClientAuth>();
var nativeSection = builder.Configuration.GetSection(AionConfiguration.Native);
var native = (nativeSection.Get<NativeOptions>() ?? new NativeOptions()) with
{
    Network = network,
    MapEventAskNpcProximityFallback = askNpcFallback,
    // Multi-world is opt-in (docs/MULTI_WORLD.md): Aion:Worlds:Enabled on the login host; a world-role process always.
    MultiWorld = worldProcess is not null || builder.Configuration.GetValue<bool>("Aion:Worlds:Enabled"),
    ClientGmPanel = NativeOptions.ResolveClientGmPanel(nativeSection.GetValue<bool?>("ClientGmPanel")),
    EnforceBattlePassWeeklyCap = builder.Configuration.GetValue("Aion:BattlePass:EnforceWeeklyCap", false),
    ClientProtocol = clientProtocol,
};
if (worldProcess is not null)
{
    if (native.GamePort is null || native.Development || !accountsReady)
        throw new InvalidOperationException("World role needs Aion:Native:GamePort, account logins and the Aion and Auth connection strings.");
    native = native with { LoginPort = null };
}
if (native.LoginPort is not null || native.GamePort is not null)
{
    if (!native.Development && !accountsReady)
        throw new InvalidOperationException("Native ports with account logins need the Aion and Auth connection strings; set Aion:Native:Logins=development for local testing.");
    builder.Services.AddSingleton(native);
    // NPCs walk on the extracted landscape where a ground grid exists (extracted data, not in the repository).
    var terrainDir = Aion.World.TerrainStore.Locate(native.TerrainDirectory, AppContext.BaseDirectory);
    var terrainStore = new Aion.World.TerrainStore(terrainDir);
    builder.Services.AddSingleton(terrainStore);
    // NPC paths around obstacles where a navigation mesh exists (extracted data, not in the repository).
    var navDir = Aion.World.NavMeshStore.Locate(native.NavMeshDirectory, AppContext.BaseDirectory);
    var navStore = new Aion.World.NavMeshStore(navDir);
    builder.Services.AddSingleton(navStore);
    Console.WriteLine($"[Geometry] terrainDir={terrainDir ?? "(null)"} navDir={navDir ?? "(null)"} (override via Native:TerrainDirectory / Native:NavMeshDirectory)");
    foreach (var map in native.PreloadMaps)
    {
        // Background warm-up; a failed load surfaces when a session uses the map.
        var geometry = Aion.World.GameData.Embedded.GeometryMap(map);
        _ = terrainStore.LoadAsync(geometry);
        _ = navStore.LoadAsync(geometry);
    }
    // Builder (GM) commands: client console "s <command>" (0x362e) and "//<command>" in chat.
    builder.Services.AddSingleton<Aion.World.WorldPresence>();
    builder.Services.AddSingleton(sp => new ShopQunaBalanceService(
        sp.GetRequiredService<Aion.World.WorldPresence>(),
        sp.GetService<IPlatformSecondaryAuth>(),
        sp.GetService<IQunaWallet>(),
        sp.GetService<IMileageWallet>()));
    builder.Services.AddSingleton<Aion.Host.Authentication.ScheduledRestart>();
    // Multiplayer: one shared world (maps, NPCs, players) for every game connection on this host.
    builder.Services.AddSingleton(provider => new Aion.World.SharedWorld(provider.GetRequiredService<Aion.World.GameData>()));
    // Playerbots (docs/PLAYERBOTS.md): off unless Aion:Bots:Enabled, and only with a game port.
    var botOptions = (builder.Configuration.GetSection("Aion:Bots").Get<Aion.World.BotOptions>() ?? new Aion.World.BotOptions()).Validated();
    if (botOptions.Enabled && native.GamePort is not null)
    {
        builder.Services.AddSingleton(provider => new Aion.World.PlayerBots(provider.GetRequiredService<Aion.World.GameData>(), botOptions,
            provider.GetRequiredService<Aion.World.SharedWorld>(), presence: provider.GetRequiredService<Aion.World.WorldPresence>()));
        builder.Services.AddHostedService<PlayerBotsService>();
    }
    // QA quest bots (docs/QA_BOTS.md): off unless Aion:QaBots:Enabled.
    var qaOptions = (builder.Configuration.GetSection("Aion:QaBots").Get<Aion.World.QaBotOptions>() ?? new Aion.World.QaBotOptions()).Validated();
    if (qaOptions.Enabled && native.GamePort is not null)
    {
        builder.Services.AddSingleton(qaOptions);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            builder.Services.AddSingleton<IQaBotStore, PostgresQaBotStore>();
            builder.Services.AddSingleton<QaInternalWorldSessions>();
            builder.Services.AddSingleton<IQaBotDiagnostics, QaBotDiagnostics>();
            builder.Services.AddSingleton(provider =>
            {
                var nativeOpts = provider.GetRequiredService<NativeOptions>();
                var game = provider.GetRequiredService<IGameplayStore>();
                Func<Guid, CancellationToken, Task<string?>> staffRole = async (account, ct) =>
                {
                    try
                    {
                        return Aion.Core.Characters.CharacterNames.DisplayRole(
                            (await game.AdminProfileAsync(new Aion.Core.Accounts.VerifiedAccount(account), ct)).Roles);
                    }
                    catch (GameplayException) { return null; }
                };
                return new QaBotSessionFactory(
                    nativeOpts.WorldId,
                    provider.GetRequiredService<QaInternalWorldSessions>(),
                    provider.GetRequiredService<IWireCharacterRepository>(),
                    game,
                    provider.GetRequiredService<CharacterPersistenceService>(),
                    provider.GetService<IQunaWallet>(),
                    staffRole,
                    provider.GetService<TerrainStore>(),
                    provider.GetService<NavMeshStore>());
            });
        }
        builder.Services.AddSingleton(provider =>
        {
            var factory = provider.GetService<QaBotSessionFactory>();
            Func<QaBotSpawnContext, QaBotSpawnBinding?>? spawn = factory is null ? null : factory.Bind;
            return new QaBots(
                provider.GetRequiredService<GameData>(),
                qaOptions,
                provider.GetRequiredService<SharedWorld>(),
                provider.GetService<IQaBotStore>(),
                provider.GetService<NativeOptions>()?.WorldId,
                spawn,
                provider.GetService<IQaBotDiagnostics>(),
                provider.GetService<TerrainStore>(),
                provider.GetService<NavMeshStore>());
        });
        builder.Services.AddHostedService<QaBotService>();
    }
    // Market (OURS, docs/MARKET.md): //ah and optional seller/buyer bots; off unless Aion:Market:Enabled.
    var market = (builder.Configuration.GetSection("Aion:Market").Get<Aion.World.MarketOptions>() ?? new Aion.World.MarketOptions()).Validated();
    var marketReferences = market.Enabled
        ? market.PriceFile is { Length: > 0 } priceFile ? Aion.World.MarketReferencePrices.Load(priceFile)
            : market.BundledPrices ? Aion.World.MarketReferencePrices.Embedded()
            : ((Dictionary<int, long> ById, Dictionary<string, long> ByName)?)null
        : null;
    if (market.Enabled && native.GamePort is not null && !string.IsNullOrWhiteSpace(connectionString))
    {
        builder.Services.AddSingleton(market);
        builder.Services.AddSingleton<Aion.Application.Market.IMarketStore, PostgresMarketStore>();
        builder.Services.AddSingleton(provider => new Aion.World.MarketCommands(provider.GetRequiredService<Aion.Application.Market.IMarketStore>(), market,
            Aion.World.MarketPricing.Embedded, provider.GetRequiredService<Aion.World.SharedWorld>()));
        builder.Services.AddSingleton(provider =>
        {
            var world = provider.GetRequiredService<Aion.World.SharedWorld>();
            var log = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Aion.Market");
            return new Aion.World.MarketBot(provider.GetRequiredService<Aion.Application.Market.IMarketStore>(), market, Aion.World.MarketPricing.Embedded,
                mailed: (seller, post) => world.FindOnline(seller)?.Send(Aion.World.GameSession.PostReceived(post)),
                log: message => log.LogInformation("{Message}", message), references: marketReferences);
        });
        builder.Services.AddHostedService<MarketBotService>();
    }
    // The race of the world each account picks on the login port, for the game port's lobby.
    builder.Services.AddSingleton<Aion.World.WorldChoices>();
    builder.Services.AddSingleton(provider =>
    {
        var log = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Aion.Gm");
        return new Aion.World.BuilderCommands(provider.GetRequiredService<Aion.World.WorldPresence>(), provider.GetRequiredService<Aion.World.GmPolicy>(),
            (actor, command, result) => log.LogInformation("GM {Command} by {Actor}: {Result}", command, actor, result),
            provider.GetRequiredService<Aion.World.GameData>());
    });
    builder.Services.AddSingleton<IChatCommandHandler>(provider =>
        new Aion.World.GmCommands(provider.GetRequiredService<Aion.World.WorldPresence>(), provider.GetRequiredService<Aion.World.BuilderCommands>())
        {
            Bots = provider.GetService<Aion.World.PlayerBots>(),
            QaBots = provider.GetService<Aion.World.QaBots>(),
            Market = provider.GetService<Aion.World.MarketCommands>(),
            // "//report <text>" opens a ticket for the staff dashboard (accounts only).
            Report = provider.GetService<IStaffTools>() is { } staffTools ? async (sender, name, text, ct) =>
            {
                if (sender.AccountId is not { } account) return "Reports need a signed-in account.";
                try { return $"Thanks, report #{await staffTools.CreateTicketAsync(account, name, "other", text, ct)} was sent to the staff."; }
                catch (GameplayException error) { return error.Code == "too_many_tickets" ? "You already have 10 open reports. Please wait for the staff." : "That report could not be sent."; }
            } : null,
        });
    // Staff rights for builder commands and the Login_RS admin flag (one check, from account roles).
    builder.Services.AddSingleton(provider =>
    {
        var store = provider.GetService<IGameplayStore>();
        return new Aion.World.GmPolicy
        {
            DevelopmentNames = native.Development ? native.DevelopmentGmNames.ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(),
            StaffRole = async (account, ct) =>
            {
                if (store is null) return null;
                try { return Aion.Core.Characters.CharacterNames.DisplayRole((await store.AdminProfileAsync(new Aion.Core.Accounts.VerifiedAccount(account), ct)).Roles); }
                catch (GameplayException) { return null; }  // "forbidden": not staff
            },
            SearchItems = store is null ? null : async (account, query, ct) =>
                (await store.AdminSearchItemsAsync(new Aion.Core.Accounts.VerifiedAccount(account), query, ct)).Select(r => (r.TemplateId, r.Name)).ToList(),
            // Same audited, idempotent store path as the staff dashboard (applies on the next world entry).
            GrantItem = store is null ? null : async (account, character, item, count, ct) =>
            {
                var staff = new Aion.Core.Accounts.VerifiedAccount(account);
                var build = (await store.AdminProfileAsync(staff, ct)).BuildId ?? throw new GameplayException("no_catalogue");
                await store.AdminAsync(staff, new AdminCommand(Guid.NewGuid(), character, "grant", BuildId: build, TemplateId: item, Quantity: count), ct);
            },
            SetLevel = store is null ? null : (account, character, level, ct) =>
                store.AdminAsync(new Aion.Core.Accounts.VerifiedAccount(account), new AdminCommand(Guid.NewGuid(), character, "level", Level: level), ct),
            SetAscension = store is null ? null : (account, character, grade, ct) =>
                store.AdminAsync(new Aion.Core.Accounts.VerifiedAccount(account), new AdminCommand(Guid.NewGuid(), character, "ascension", Ascension: grade), ct),
            AcquireWings = store is null ? null : (account, character, wings, ct) =>
                store.AdminAsync(new Aion.Core.Accounts.VerifiedAccount(account), new AdminCommand(Guid.NewGuid(), character, "wings", Wings: wings), ct),
            ChangeClass = store is null ? null : async (account, character, name, ct) =>
            {
                var staff = new Aion.Core.Accounts.VerifiedAccount(account);
                var classes = (await store.AdminCharacterAsync(staff, character, ct))?.Classes ?? throw new GameplayException("character_unavailable");
                var available = classes.Select(c => c.Name).ToList();
                var match = classes.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (match is null) return (false, null, available);
                await store.AdminAsync(staff, new AdminCommand(Guid.NewGuid(), character, "class", CreationId: match.CreationId), ct);
                return (true, match.Name, available);
            },
        };
    });
    builder.Services.AddSingleton<NativePortReadiness>();
    builder.Services.AddSingleton(provider => new PublicPlayerCount(provider.GetService<WorldPresence>(), provider.GetService<QaBots>(), provider.GetRequiredService<TimeProvider>()));
    if (native.LoginPort is not null) builder.Services.AddHostedService<NativeLoginListener>();
    if (native.LoginPort is not null && !native.Development && worldProcess is null && !string.IsNullOrWhiteSpace(connectionString))
    {
        // Multi-world (primary host, Aion:Worlds:Enabled): the managed child worlds. The registry list also names the
        // single-world rows, so a dashboard rename reaches the client without multi-world.
        if (native.MultiWorld)
        {
            builder.Services.AddSingleton<Aion.Host.Worlds.WorldSupervisor>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<Aion.Host.Worlds.WorldSupervisor>());
        }
        builder.Services.AddSingleton(provider => new Aion.Host.Worlds.LoginWorldList(provider.GetRequiredService<IWorldRegistry>(), native,
            provider.GetService<Aion.Host.Worlds.WorldSupervisor>(), provider.GetRequiredService<ILogger<Aion.Host.Worlds.LoginWorldList>>()));
    }
    if (native.GamePort is not null) builder.Services.AddHostedService<NativeGameListener>();
    if (native.GamePort is not null)
    {
        builder.Services.AddSingleton<WorldLoadReporter>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<WorldLoadReporter>());
        builder.Services.AddHostedService<FieldBossNoticeService>();
    }
    // Dashboard grants reach players in the world at once (needs the game database).
    if (native.GamePort is not null && !string.IsNullOrWhiteSpace(connectionString))
    {
        builder.Services.AddHostedService<InventoryNotifications>();
        // Database-wide sweep: the primary host runs it, not every world process.
        if (worldProcess is null) builder.Services.AddHostedService<PostExpirySweep>();
    }
}
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Chat WebSockets are long-lived; they would hold a host permit for their whole lifetime.
    // Chat messages are limited per character by ChatRelay instead.
    // Page bundles and wardrobe files (hundreds of icons at once over HTTP/2) are plain file reads.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        chatPort is { } chat && context.Connection.LocalPort == chat
            ? RateLimitPartition.GetNoLimiter("chat")
            : context.Request.Path.StartsWithSegments("/wardrobe") || context.Request.Path.StartsWithSegments("/assets")
            ? RateLimitPartition.GetNoLimiter("files")
            : RateLimitPartition.GetConcurrencyLimiter("host", _ => new ConcurrencyLimiterOptions { PermitLimit = 16, QueueLimit = 0 }));
    options.AddPolicy("account-entry", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Staff dashboard reads and commands; role checks happen per request in the store.
    options.AddPolicy("admin", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("totp-verify", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
// Optional platform bootstrap for the dev client (GamePlatform.ini ConfigurationServerUrl).
var platform = worldProcess is null ? builder.Configuration.GetSection(AionConfiguration.Platform).Get<PlatformOptions>() ?? new PlatformOptions() : new PlatformOptions();
var ncGuard = (worldProcess is null ? builder.Configuration.GetSection(AionConfiguration.NcGuard).Get<NcGuardOptions>() ?? new NcGuardOptions() : new NcGuardOptions())
    with { BindAddress = network.BindAddress };
// A Steam-patched client ignores ConfigurationServerUrl and talks to api.g.nc.com over HTTPS; when we
// MITM that host, the code-less policy must re-address api_gate/login onto it (not plain HTTP :8087).
if (ncGuard.Port is not null && platform.ConfigApiHost is null)
    platform = platform with { ConfigApiHost = "api.g.nc.com" };
var platformResponses = platform.Port is not { } bootstrapPort ? null : PlatformResponses.Load(platform, network, bootstrapPort, native.LoginPort, chatPort);
// Launchers that sign in on this host and start clients without a launch code in the bootstrap URL.
if (accountsReady) builder.Services.AddSingleton<LaunchPairing>();
if (platform.Port is { } crlBootstrapPort && ncGuard.CrlDistributionUrl is null)
    ncGuard = ncGuard with { CrlDistributionUrl = $"http://{network.PublicAddress}:{crlBootstrapPort}/pki/ca-root.crl" };
if (ncGuard.Port is not null && ncGuard.NccrMode is null && platform.NccrMode is not null)
    ncGuard = ncGuard with { NccrMode = platform.NccrMode };
if (ncGuard.Port is not null)
{
    builder.Services.AddSingleton(ncGuard);
    builder.Services.AddSingleton<NcGuardSink>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<NcGuardSink>());
    builder.Services.AddOptions<KestrelServerOptions>().Configure<NcGuardSink>((options, sink) => sink.Configure(options));
}
// Account site and API (sign-in, launches, staff dashboard). A public host serves it over HTTPS
// (Aion:Https PEM certificate) unless a TLS proxy in front of it is declared (Aion:Https:Proxy).
var port = builder.Configuration.GetValue<int?>("Aion:LocalPort") ?? 8780;
var https = builder.Configuration.GetSection(AionConfiguration.Https).Get<HttpsOptions>() ?? new HttpsOptions();
// A world process's HTTP port is loopback only (status and stop for the primary host), without TLS.
if (worldProcess is not null) https = new HttpsOptions { Proxy = true };
if (!network.LoopbackOnly && https.CertificateFile is null && !https.Proxy)
    throw new InvalidOperationException("A public host needs Aion:Https:CertificateFile and KeyFile for the account port (or Aion:Https:Proxy=true behind a TLS proxy).");
var accountCertificate = https.CertificateFile is { } certificateFile
    ? System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(certificateFile, https.KeyFile) : null;
// Windows TLS (Schannel) cannot use the ephemeral key of a PEM-loaded certificate; reload it as PKCS#12.
if (accountCertificate is not null && OperatingSystem.IsWindows())
    accountCertificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
        accountCertificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null);
// Optional fallback certificate for connections without a host name (SNI), e.g. older launchers that
// pinned a self-signed certificate for the bare IP address. Clients that name the host get the main one.
var legacyCertificate = https.LegacyCertificateFile is { } legacyFile && accountCertificate is not null
    ? System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(legacyFile, https.LegacyKeyFile) : null;
if (legacyCertificate is not null && OperatingSystem.IsWindows())
    legacyCertificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
        legacyCertificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 16384;
    // Behind a proxy the account port stays on loopback; the proxy is the public endpoint.
    options.Listen(https.Proxy ? IPAddress.Loopback : network.Bind, port, listener =>
    {
        if (accountCertificate is not null && legacyCertificate is not null)
            listener.UseHttps(tls => tls.ServerCertificateSelector = (connection, hostName) =>
                string.IsNullOrEmpty(hostName) || IPAddress.TryParse(hostName, out _) ? legacyCertificate : accountCertificate);
        else if (accountCertificate is not null) listener.UseHttps(accountCertificate);
    });
    // The client's own bootstrap and chat requests are plain HTTP (observed policy URLs).
    foreach (var httpPort in new[] { chatPort, platform.Port }.OfType<int>().Where(p => p != port).Distinct())
        options.Listen(network.Bind, httpPort);
});
builder.Services.AddSingleton<WardrobeAccess>();
var app = builder.Build();
if (worldProcess is null)
    PostgresMigrationRunner.ApplyIfEnabled(app.Configuration, app.Logger);
app.Logger.LogInformation("Bosses: {Bosses}; field bosses: {FieldBosses}; combat: {CombatTelegraphs}",
    Aion.World.GameSession.BossRules, Aion.World.FieldBosses.Options, Aion.World.GameSession.CombatTelegraphRules);
app.Logger.LogInformation("Boss encounters: {Count} authored (tools/data/boss-mechanics), {Errors} rejected.",
    Aion.World.BossEncounters.Embedded.Count, Aion.World.BossEncounters.Embedded.Errors.Count);
foreach (var error in Aion.World.BossEncounters.Embedded.Errors)
    app.Logger.LogError("Boss encounter {File} rejected: {Problem}", error.File, error.Message);
if (native.LoginPort is not null || native.GamePort is not null)
    app.Logger.LogInformation("World rates: {Rates} (scheduled events multiply on top, each 0.1–10).", serverRates);
if (app.Services.GetService<Aion.World.SharedWorld>() is { } sharedWorld)
{
    Aion.World.CustomNpcWorld.Configure(app.Services.GetService<ICustomNpcCatalog>(), sharedWorld,
        builder.Configuration.GetValue<bool?>("Aion:CustomNpcs:AssumePatchedClients") ?? false,
        message => app.Logger.LogInformation("{Message}", message));
    Aion.World.FieldBossWorld.Configure(app.Services.GetService<IFieldBossStore>(), sharedWorld,
        message => app.Logger.LogInformation("{Message}", message));
}
if (worldProcess is not null)
{
    app.Logger.LogInformation("World process for world {World} ({Name}) on game port {Port}", native.WorldId, worldProcess.Name, native.GamePort);
    Aion.Host.Worlds.WorldEndpoints.MapWorldProcessEndpoints(app, worldProcess, native.WorldId);
    app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
    app.Run();
    return;
}
if (debugOptions.Enabled && app.Services.GetService<PlaytestDebugSink>() is { } playtestDebug)
{
    PlaytestDebug.Sink = playtestDebug;
    app.Logger.LogInformation("Playtest debug log: {Path}", playtestDebug.LogPath);
    app.Lifetime.ApplicationStopping.Register(() => playtestDebug.DisposeAsync().AsTask().GetAwaiter().GetResult());
}
if (app.Services.GetService<ServerFileLoggerProvider>() is { } serverLog)
{
    app.Logger.LogInformation("Server log file: {Path}", serverLog.Path);
    app.Lifetime.ApplicationStopping.Register(serverLog.Dispose);
}
// Chat mutes and the chat log go through the staff tools when both exist.
if (app.Services.GetService<Aion.Host.Authentication.ServerControl>() is { } serverControl && app.Services.GetService<ChatRelay>() is { } chatRelay)
    chatRelay.Moderation = serverControl;
if (app.Services.GetService<ChatRelay>() is { } chatRelayBlocks && app.Services.GetService<IGameplayStore>() is { } gameplayStore)
    chatRelayBlocks.WhisperBlocked = (from, to) => gameplayStore.AnyBlockBetweenAsync(from, to, default).GetAwaiter().GetResult();
// Behind the declared TLS proxy (loopback), client addresses for rate limits and launch pairing
// come from X-Forwarded-For; only loopback proxies are trusted (the defaults).
if (https.Proxy)
    app.UseForwardedHeaders(new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
    { ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto });
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    context.Response.StatusCode = 500;
    await context.Response.WriteAsJsonAsync(new { error = "The request could not be completed." });
}));
if (ncGuard.Port is { } ncGuardPort)
{
    var sink = app.Services.GetRequiredService<NcGuardSink>();
    // Config/login SNI hosts (mobileappconfig.g.nc.com, ...) on the TLS port are served by the platform
    // bootstrap handler below (same app/key, app/policy, api_gate and login routes as the bootstrap port),
    // not the fixture sink: a Steam-patched client reaches them directly and would otherwise get a 404.
    // Only divert when the bootstrap handler is actually wired (platform.Port set).
    var platformBootstrapOn = platform.Port is not null;
    app.Use(async (context, next) =>
    {
        if (context.Connection.LocalPort == ncGuardPort
            && !(platformBootstrapOn && PlatformBootstrapEndpoints.IsConfigHostRequest(context, ncGuardPort)))
            await sink.HandleAsync(context);
        else await next();
    });
}
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob: data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/internal")) context.Response.Headers.CacheControl = "no-store";
    await next();
});
// The chat port is plain HTTP on the public address and has no rate limiter: it answers the chat's own routes only, never
// the account site, sign-in or staff API (those stay on the account port, behind TLS and the limiter). Adapted from Addiboii/A2Core 10f8635.
if (chatPort is { } chatOnly && chatOnly != port && chatOnly != platform.Port)
{
    string[] chatRoutes = ["/gameLogin", "/gameClient", "/stomp", "/internal/online", "/internal/world", "/internal/announce"];
    app.Use(async (context, next) =>
    {
        if (context.Connection.LocalPort == chatOnly && !chatRoutes.Any(r => context.Request.Path.StartsWithSegments(r)))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await next();
    });
}
if (platform.Port is { } platformPort)
{
    var accountTls = https.Proxy || https.CertificateFile is not null;
    var accountPageBase = accountTls
        ? $"https://{network.PublicAddress}" + (https.Proxy ? "" : $":{port}")
        : $"http://{network.PublicAddress}:{port}";
    app.UsePlatformBootstrap(platformPort, platformResponses!, platform.RequestLog, network, accountPageBase, ncGuard.Port, platform.NccrMode);
}
app.UseRateLimiter();
if (accountsReady) app.UseStaffSessions();
if (debugOptions.Enabled) app.UsePlaytestHttpLogging();
app.UseWebSockets();
app.MapSynapseStub();
// The staff dashboard can live at a private address (Aion:Web:StaffPath, e.g. "/k3x9..."); then
// /admin.html is not found. Obscurity only: every staff API call still needs a staff sign-in.
if (builder.Configuration["Aion:Web:StaffPath"] is { Length: > 1 } staffPath && staffPath.StartsWith('/') && staffPath != "/admin.html")
{
    var dashboardFile = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "admin.html");
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        if (path.Equals("/admin.html", StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = 404; return; }
        if (path.Equals(staffPath, StringComparison.Ordinal) && HttpMethods.IsGet(context.Request.Method) && File.Exists(dashboardFile))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.SendFileAsync(dashboardFile);
            return;
        }
        await next();
    });
}
app.UseDefaultFiles();
app.UseStaticFiles();
// Optional wardrobe export (meshes, textures, icons, vendored three.js) for the staff dashboard's
// preview and icons. Extracted game assets: a public host serves them to signed-in staff only
// (WardrobeAccess cookie); a loopback development host serves them openly unless StaffOnly is set.
var wardrobePath = builder.Configuration["Aion:WardrobePath"];
var wardrobeStaffOnly = !network.LoopbackOnly || builder.Configuration.GetValue<bool>("Aion:Wardrobe:StaffOnly");
if (!string.IsNullOrWhiteSpace(wardrobePath) && Directory.Exists(wardrobePath) && (!wardrobeStaffOnly || accountsReady))
{
    if (wardrobeStaffOnly) WardrobeAccess.Map(app, secure: accountCertificate is not null || https.Proxy);
    var wardrobeRoot = Path.GetFullPath(wardrobePath);
    var wardrobeTypes = new FileExtensionContentTypeProvider(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    { [".glb"] = "model/gltf-binary", [".webp"] = "image/webp", [".json"] = "application/json", [".js"] = "text/javascript" });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(wardrobeRoot), RequestPath = "/wardrobe/files", ContentTypeProvider = wardrobeTypes,
        // Exported files do not change between exports; staff browsers keep them for a day.
        OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = "private, max-age=86400",
    });
    app.MapGet("/wardrobe/data", () => Results.File(Path.Combine(wardrobeRoot, "wardrobe.json"), "application/json"));
}
// Player downloads (the launcher, launcher/AionLauncher) from Aion:Web:Downloads, for signed-in accounts.
var downloads = accountsReady && builder.Configuration["Aion:Web:Downloads"] is { Length: > 0 } downloadPath && Directory.Exists(downloadPath)
    ? new Downloads(Path.GetFullPath(downloadPath), TimeProvider.System) : null;
static string SiteName(IConfiguration configuration) =>
    configuration["Aion:Web:SiteName"]?.Trim() is { Length: > 0 } name ? name[..Math.Min(name.Length, 40)] : "A2Core";
var launcherEndpoints = accountsReady && platform.Port is not null ? LauncherEndpoints.Embedded() : null;
downloads?.Map(app, launcherEndpoints is null ? null
    : request => LauncherEndpoints.LauncherJson(request, app.Configuration, SiteName(app.Configuration), network.PublicAddress, platform.Port!.Value));
launcherEndpoints?.Map(app, () => SiteName(app.Configuration), network.PublicAddress, platform.Port!.Value);
var registrationOpen = builder.Configuration.GetValue<bool?>("Aion:Accounts:Registration") ?? true;
app.MapGet("/api/status", async (IServiceProvider services, CancellationToken ct) =>
{
    var gameReady = services.GetService<NativePortReadiness>() is { } readiness && await readiness.GameReadyAsync(ct);
    var online = 0;
    var onlineBots = 0;
    if (services.GetService<PublicPlayerCount>() is { } players)
    {
        var snap = await players.SnapshotAsync(ct);
        online = snap.Online;
        onlineBots = snap.OnlineBots;
    }
    return Results.Ok(new { accountsConfigured = accountsReady, registrationOpen = accountsReady && registrationOpen,
        launcherAvailable = downloads?.LauncherAvailable ?? false, gameReady, online, onlineBots });
});
if (accountsReady)
{
    app.MapAccountEndpoints(registrationOpen);
    app.MapStaffSessionEndpoints();
    app.MapTotpEndpoints();
    app.MapGameplayEndpoints();
    Aion.Host.Authentication.StaffAccountEndpoints.MapStaffAccountEndpoints(app);
    Aion.Host.Authentication.StaffShopEndpoints.MapStaffShopEndpoints(app);
    Aion.Host.Authentication.StaffEndpoints.MapStaffEndpoints(app);
    Aion.Host.Authentication.StaffToolsEndpoints.MapStaffToolsEndpoints(app);
    Aion.Host.Authentication.StaffToolsEndpoints.MapTicketEndpoints(app);
    if (app.Services.GetService<IQaBotStore>() is not null)
        Aion.Host.Authentication.QaBotEndpoints.MapQaBotEndpoints(app);
    Aion.Host.Worlds.WorldEndpoints.MapWorldEndpoints(app);
    var bridgeSecret = builder.Configuration["Aion:BridgeSecret"];
    if (bridgeSecret is { Length: >= 64 })
    {
        app.MapClientHandoff(bridgeSecret);
        app.MapWorldPersistence(bridgeSecret);
    }
}

if (chatPort is { } chatEndpoints) app.MapLimeChat(chatEndpoints, network.PublicAddress);

app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
// Do not advertise gameplay readiness before authentication/storage are wired.
app.MapGet("/health/ready", () => Results.Json(
    new { status = "not_ready", reason = "Original-client game-session handoff is not connected." },
    statusCode: StatusCodes.Status503ServiceUnavailable));

app.Run();

/// <summary>Aion:Https: PEM certificate and key for the account port, or Proxy when a TLS proxy terminates it.</summary>
sealed record HttpsOptions
{
    public string? CertificateFile { get; init; }
    public string? KeyFile { get; init; }
    public string? LegacyCertificateFile { get; init; }
    public string? LegacyKeyFile { get; init; }
    public bool Proxy { get; init; }
}
