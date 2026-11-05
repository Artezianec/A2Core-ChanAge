using System.Globalization;

namespace Aion.Launcher;

public enum LauncherLanguage { En, Ru }

/// <summary>Launcher UI strings (EN/RU). Default follows Windows UI culture unless overridden in settings.</summary>
public static class LauncherLoc
{
    public static LauncherLanguage Language { get; private set; }

    public static void Apply(string? savedLanguage)
    {
        Language = savedLanguage switch
        {
            "ru" or "RU" => LauncherLanguage.Ru,
            "en" or "EN" => LauncherLanguage.En,
            _ => DefaultFromSystem(),
        };
    }

    public static LauncherLanguage DefaultFromSystem() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase)
            ? LauncherLanguage.Ru
            : LauncherLanguage.En;

    public static string StorageValue => Language == LauncherLanguage.Ru ? "ru" : "en";

    public static void SetLanguage(LauncherLanguage language) => Language = language;

    static string T(string en, string ru) => Language == LauncherLanguage.Ru ? ru : en;

    public static string[] StepLabels =>
    [
        T("Sign in", "Вход"),
        T("Get the game", "Загрузка игры"),
        T("Install", "Установка"),
        T("Play", "Игра"),
    ];

    // ---------- Dialogs ----------
    public static string DialogStopAndClose => T("Stop the current task and close?", "Остановить задачу и закрыть?");
    public static string DialogDeleteGameCopy(string copy) => T(
        $"Also delete the private game copy ({copy})?\n\nYes deletes it (it can be set up again later). No keeps the files.",
        $"Удалить локальную копию игры ({copy})?\n\n«Да» — удалить (позже можно установить снова). «Нет» — оставить файлы.");
    public static string DialogVerifyRepairDownload(string problem) => T(
        $"{problem}\n\nDownload the corrected files from Steam with your account?",
        $"{problem}\n\nСкачать исправленные файлы из Steam с вашего аккаунта?");
    public static string DialogRemoveLauncher(string name) => T(
        $"Remove {name} from this PC?\n\nThis removes the program and its shortcuts, the firewall rules and the settings. Your Steam copy of the game is not touched.",
        $"Удалить {name} с этого компьютера?\n\nБудут удалены программа и ярлыки, правила брандмауэра и настройки. Копия игры в Steam не затрагивается.");
    public static string InvalidLauncherJson(string detail) => T("launcher.json is invalid: ", "Неверный launcher.json: ") + detail;

    // ---------- Sign in / TOTP ----------
    public static string TitleSignIn => T("Sign in", "Вход");
    public static string IntroSignIn => T("Use the account you were given for this server.", "Используйте аккаунт, выданный для этого сервера.");
    public static string LabelUsername => T("Username", "Имя пользователя");
    public static string LabelPassword => T("Password", "Пароль");
    public static string KeepSignedIn => T("Keep me signed in on this PC", "Запомнить вход на этом компьютере");
    public static string ButtonSignIn => T("Sign in", "Войти");
    public static string LinkAccountHelp => T("Forgot your password or need help? Open the account page", "Забыли пароль или нужна помощь? Откройте страницу аккаунта");
    public static string EnterUsernamePassword => T("Enter your username and password.", "Введите имя пользователя и пароль.");
    public static string StatusSigningIn => T("Signing in…", "Вход…");
    public static string SessionEndedSignInAgain => T("Your saved sign-in has ended. Sign in again.", "Сохранённый вход недействителен. Войдите снова.");
    public static string SignInToInstallUpdate => T("Sign in to install the update.", "Войдите, чтобы установить обновление.");

    public static string TotpTitle => T("Two-step verification", "Двухэтапный вход");
    public static string TotpIntro => T(
        "This account uses two-step verification. Enter the code from your authenticator app or a recovery code.",
        "На аккаунте включена двухэтапная проверка. Введите код из приложения-аутентификатора или код восстановления.");
    public static string TotpCodeLabel => T("Authenticator code", "Код из приложения");
    public static string TotpRecoveryLabel => T("Recovery code (optional)", "Код восстановления (необязательно)");
    public static string TotpContinue => T("Continue", "Продолжить");
    public static string TotpBack => T("Back", "Назад");
    public static string TotpMissing => T("Enter an authenticator code or a recovery code.", "Введите код из приложения или код восстановления.");
    public static string TotpWrong => T("Wrong code. Try again.", "Неверный код. Попробуйте снова.");

    // ---------- Updates ----------
    public static string TitleCheckingUpdates => T("Checking for updates", "Проверка обновлений");
    public static string StatusCheckingNewerVersion => T("Checking for a newer version…", "Проверка новой версии…");
    public static string TitleUpdateAvailable(string name) => T($"A new version of {name} is available", $"Доступна новая версия {name}");
    public static string UpdateIntroSignedIn(string versionSuffix) => T(
        $"The server has a newer version{versionSuffix}. Install it to get the latest fixes: it is downloaded and started in place of this one, and your settings and game files are kept.",
        $"На сервере новая версия{versionSuffix}. Установите её: загрузка и запуск вместо текущей, настройки и файлы игры сохранятся.");
    public static string UpdateIntroSignInRequired(string versionSuffix) => T(
        $"The server has a newer version{versionSuffix}. Sign in to download and install it; your settings and game files are kept.",
        $"На сервере новая версия{versionSuffix}. Войдите, чтобы скачать и установить; настройки и файлы игры сохранятся.");
    public static string InstalledVersion(string v) => T($"Installed version: {v}", $"Установленная версия: {v}");
    public static string ButtonInstallUpdate => T("Install update", "Установить обновление");
    public static string ButtonSignInToInstall => T("Sign in to install", "Войти для установки");
    public static string ButtonNotNow => T("Not now", "Не сейчас");
    public static string TitleUpdating(string name) => T($"Updating {name}", $"Обновление {name}");
    public static string IntroUpdating => T(
        "The new version is downloaded and checked, then started in place of this one.",
        "Новая версия загружается и проверяется, затем запускается вместо этой.");
    public static string StatusDownloadingNewVersion => T("Downloading the new version…", "Загрузка новой версии…");
    public static string StatusCheckingDownload => T("Checking the download…", "Проверка загрузки…");
    public static string DownloadMismatch => T("The download did not match what the server announced.", "Загрузка не совпала с данными сервера.");
    public static string TitleUpdateNotInstalled => T("The update was not installed", "Обновление не установлено");
    public static string UpdateFailedBody(string reason) => T(
        $"{reason} You can download the new version from the account page instead; this one keeps working in the meantime.",
        $"{reason} Можно скачать новую версию со страницы аккаунта; эта программа пока продолжит работать.");
    public static string ButtonContinueThisVersion => T("Continue with this version", "Продолжить с этой версией");
    public static string LinkOpenAccountPage => T("Open the account page", "Открыть страницу аккаунта");
    public static string StatusStartingNewVersion => T("Starting the new version…", "Запуск новой версии…");

    // ---------- Install offer / uninstall ----------
    public static string TitleInstall(string name) => T($"Install {name}", $"Установить {name}");
    public static string IntroInstall(string name, string folder) => T(
        $"{name} is set up for your Windows account: it goes to {folder}, with a shortcut on the desktop and in the Start menu, and shows in Apps & features for removal. No administrator rights are needed.",
        $"{name} устанавливается для вашей учётной записи Windows: папка {folder}, ярлыки на рабочем столе и в меню «Пуск», удаление через «Приложения и возможности». Права администратора не нужны.");
    public static string AlreadyInstalled => T("A copy is already installed there; this one replaces it.", "Копия уже установлена; эта заменит её.");
    public static string ButtonInstall => T("Install", "Установить");
    public static string LinkRunWithoutInstalling => T("Run without installing", "Запуск без установки");
    public static string StatusInstalling => T("Installing…", "Установка…");
    public static string TitleRemove(string name) => T($"Remove {name}", $"Удалить {name}");
    public static string IntroRemove(string name) => T(
        $"This removes {name} from your Windows account: the program, its shortcuts, the firewall rules and settings. You can also delete the private game copy. Your Steam copy of the game is not touched.",
        $"Удаляет {name} из вашей учётной записи Windows: программу, ярлыки, правила брандмауэра и настройки. Можно также удалить локальную копию игры. Steam-копия не затрагивается.");
    public static string ButtonRemove => T("Remove", "Удалить");
    public static string ButtonCancel => T("Cancel", "Отмена");
    public static string StatusRemovingFirewall => T("Removing the firewall rules (approve the Windows prompt)…", "Удаление правил брандмауэра (подтвердите запрос Windows)…");
    public static string StatusDeletingCopy => T("Deleting the private copy…", "Удаление локальной копии…");
    public static string StatusRemovingProgram => T("Removing the program…", "Удаление программы…");
    public static string StatusRemoved => T("Removed.", "Удалено.");
    public static string TitleSigningIn => T("Signing in", "Вход");
    public static string ContinuingAs(string user) => T($"Continuing as {user}…", $"Продолжение как {user}…");

    // ---------- Game flow ----------
    public static string TitleLookingForGame => T("Looking for the game", "Поиск игры");
    public static string IntroLookingForGame => T("Checking your Steam library for the version this server needs…", "Проверка библиотеки Steam на нужную серверу версию…");
    public static string ButtonTryAgain => T("Try again", "Повторить");
    public static string TitleSteamNeeded => T("Steam is needed", "Нужен Steam");
    public static string IntroSteamNeeded(string name) => T(
        "The game comes from your own Steam account. Install Steam, sign in, then come back here: " + name + " continues by itself.",
        "Игра загружается из вашего Steam. Установите Steam, войдите и вернитесь сюда — " + name + " продолжит сам.");
    public static string ButtonGetSteam => T("Get Steam", "Установить Steam");
    public static string TitleContinueDownload => T("Continue the download", "Продолжить загрузку");
    public static string TitleDownloadGame => T("Download the game", "Загрузить игру");
    public static string IntroDownloadGame(string launcherName) => T(
        "This server runs one specific version of the game (about 88 GB). " + launcherName + " downloads it from Steam with your own Steam account. Your normal Steam copy of the game is not changed.",
        "Сервер использует одну версию игры (около 88 ГБ). " + launcherName + " скачивает её из Steam с вашего аккаунта. Обычная копия в Steam не изменяется.");
    public static string SteamQrHelp(string launcherName) => T(
        "You sign in by scanning a QR code with the Steam app on your phone. " + launcherName + " never sees your Steam password.",
        "Вход — QR-код в мобильном приложении Steam. " + launcherName + " не видит ваш пароль Steam.");
    public static string ButtonContinueDownload => T("Continue download", "Продолжить загрузку");
    public static string ButtonSignInSteamDownload => T("Sign in to Steam and download", "Войти в Steam и загрузить");
    public static string ButtonCheckAgain => T("Check again", "Проверить снова");
    public static string LinkExistingFolder => T("I already have this version in another folder", "У меня уже есть эта версия в другой папке");
    public static string PausedDownload => T("Paused. Press Continue download to carry on.", "Пауза. Нажмите «Продолжить загрузку».");
    public static string TitleInstallingDownloaded => T("Installing", "Установка");
    public static string IntroInstallingDownloaded => T(
        "Downloaded. A2Core Launcher checks the files and adds firewall rules so the game can reach only this server. Windows asks for permission once: choose Yes.",
        "Загрузка завершена. Лаунчер проверяет файлы и добавляет правила брандмауэра только для этого сервера. Windows один раз запросит разрешение — выберите «Да».");
    public static string TitleDownloadingSteam => T("Downloading from Steam", "Загрузка из Steam");
    public static string QrConnecting => T("Connecting to Steam…", "Подключение к Steam…");
    public static string QrInstructions => T(
        "Scan this code to sign in to Steam:\n   1.  Open the Steam app on your phone.\n   2.  Tap the Steam Guard shield, then Scan a QR code.\n   3.  Approve the sign-in.",
        "Отсканируйте код для входа в Steam:\n   1.  Откройте Steam на телефоне.\n   2.  Щит Steam Guard → «Сканировать QR-код».\n   3.  Подтвердите вход.");
    public static string QrWaiting => T("Waiting for you to approve the sign-in in the Steam app…", "Ожидание подтверждения входа в приложении Steam…");
    public static string QrSignedIn => T("Signed in to Steam.", "Вход в Steam выполнен.");
    public static string CloseAnyTimeNote(string launcherName) => T(
        "You can close " + launcherName + " at any time; the download continues where it stopped when you open it again.",
        "Можно закрыть " + launcherName + " — загрузка продолжится при следующем запуске.");
    public static string ButtonPause => T("Pause", "Пауза");
    public static string DownloadProgress(long done, long total, long remaining, string ratePart, string timePart) =>
        T($"{Gb(done)} of {Gb(total)}  ·  {Gb(remaining)} left{ratePart}{timePart}",
          $"{Gb(done)} из {Gb(total)}  ·  осталось {Gb(remaining)}{ratePart}{timePart}");
    public static string RemainingAbout1Min => T("1 minute", "1 минута");
    public static string RemainingMinutes(int n) => T($"{n} minutes", $"{n} мин.");
    public static string RemainingHours(double h) => T($"{h:0.0} hours", $"{h:0.0} ч.");
    static string Gb(long bytes) => $"{bytes / 1e9:0.0} GB";

    // ---------- Play ----------
    public static string TitleReadyToPlay => T("Ready to play", "Готово к игре");
    public static string SignedInAs(string user) => T($"Signed in as {user}.", $"Вы вошли как {user}.");
    public static string LinkSignOut => T("Sign out", "Выйти");
    public static string LabelClientVersion => T("Client version", "Версия клиента");
    public static string VersionNoteSteam(string? folder) => T(
        $"Game files: {folder ?? "Steam copy not found (set steamClientPath in launcher.json)"}. Used as-is: not changed, no patch paks; the server supports 2.0.6.0, so a newer client may not connect.",
        $"Файлы: {folder ?? "копия Steam не найдена (steamClientPath в launcher.json)"}. Без изменений, без патч-паков; сервер на 2.0.6.0 — более новый клиент может не подключиться.");
    public static string VersionNotePrivate(string folder) => T($"Game files: {folder}", $"Файлы игры: {folder}");
    public static string PatchPaksCheck => T("Apply client patch paks (z_A2Core_Patch-Windows_P) before launch", "Применять патч-паки (z_A2Core_Patch-Windows_P) перед запуском");
    public static string ButtonPlay => T("Play", "Играть");
    public static string PlayFirewallNote(string name) => T(
        $"Each time you press Play, Windows asks for permission: {name} points the game's online services at this server while the game runs and removes them afterwards.",
        $"При каждом «Играть» Windows запросит разрешение: {name} направляет онлайн-сервисы игры на этот сервер на время сессии.");
    public static string ButtonVerifyRepair => T("Verify/repair files", "Проверить/починить");
    public static string ButtonRepairFirewall => T("Repair firewall", "Брандмауэр");
    public static string ButtonUninstall => T("Uninstall", "Удалить");
    public static string StatusPlaying => T("Playing. A2Core Launcher waits here until the game closes.", "Игра запущена. Лаунчер ждёт закрытия игры.");
    public static string StatusGameClosed => T("Game closed.", "Игра закрыта.");
    public static string StatusCheckingAllFiles => T("Checking every game file (several minutes)…", "Проверка всех файлов (несколько минут)…");
    public static string AllFilesMatch(int count) => T($"All {count} files match the server's version.", $"Все {count} файлов совпадают с версией сервера.");
    public static string ProblemChooseVerify(string problem) => T($"Problem: {problem}. Choose Verify/repair again when ready.", $"Проблема: {problem}. Запустите проверку снова, когда будете готовы.");
    public static string StatusRestoringFirewall => T("Restoring the firewall rules (approve the Windows prompt)…", "Восстановление правил брандмауэра (подтвердите запрос Windows)…");
    public static string FirewallRestored => T("Firewall rules restored.", "Правила брандмауэра восстановлены.");
    public static string FirewallRepairFailed => T("Repair was cancelled or failed.", "Восстановление отменено или не удалось.");
    public static string ChoiceNotSaved(string msg) => T("The choice was not saved: ", "Выбор не сохранён: ") + msg;

    // ---------- Status / errors (generic) ----------
    public static string StatusCancelled => T("Cancelled.", "Отменено.");
    public static string LinkBack => T("Back", "Назад");
    public static string ButtonBack => T("Back", "Назад");

    // ---------- Account API (AccountClient) ----------
    public static string ErrWrongPassword => T("Wrong username or password.", "Неверное имя пользователя или пароль.");
    public static string ErrTooManyAttempts => T("Too many attempts. Wait a minute and try again.", "Слишком много попыток. Подождите минуту.");
    public static string ErrAccountServerCode(int code) => T($"The account server answered {code}.", $"Сервер аккаунтов ответил {code}.");
    public static string ErrNoSession => T("The account server sent no session.", "Сервер не прислал сессию.");
    public static string ErrNoChallenge => T("The account server did not send a sign-in challenge.", "Сервер не прислал запрос двухэтапного входа.");
    public static string ErrSessionEnded => T("Your session ended. Sign in again.", "Сессия завершена. Войдите снова.");
    public static string ErrNoLaunch => T("The account server sent no launch.", "Сервер не прислал разрешение на запуск.");
    public static string ErrUnexpectedLaunch => T("Unexpected launch authorization.", "Неверное разрешение на запуск.");
    public static string ErrUnexpectedManifest => T("Unexpected launcher manifest.", "Неверный манифест лаунчера.");
    public static string ErrNoLauncherDownload => T("The server has no launcher download.", "На сервере нет файла лаунчера.");
    public static string ErrUnexpectedDownloadLink => T("Unexpected download link.", "Неверная ссылка на загрузку.");
    public static string ErrLauncherDownloadCode(int code) => T($"The launcher download answered {code}.", $"Загрузка лаунчера: код {code}.");
    public static string ErrReachAccount(string detail) => T($"Could not reach the account server ({detail}).", $"Не удалось связаться с сервером аккаунтов ({detail}).");
    public static string ErrAccountTimeout => T("The account server did not answer in time.", "Сервер аккаунтов не ответил вовремя.");

    // ---------- Workflow status / errors ----------
    public static string ErrNotEnoughSpace(string root, string needed, string download) => T(
        $"Not enough free space on {root}. Need at least {needed} GB free ({download} GB to download plus headroom).",
        $"Недостаточно места на {root}. Нужно минимум {needed} ГБ ({download} ГБ загрузки и запас).");
    public static string ErrNoDriveSpace(double gb) => T($"No drive has enough free space for the game ({gb:0} GB plus headroom).", $"Ни на одном диске нет {gb:0} ГБ с запасом.");
    public static string ErrInstallMismatch(string problem) => T($"The installed files do not match the supported build ({problem}).", $"Файлы не совпадают со сборкой ({problem}).");
    public static string ErrNoGameFolder => T("That folder has no game files. Choose the folder that contains the Aion2 and Engine folders.", "В папке нет файлов игры. Выберите папку с Aion2 и Engine.");
    public static string ErrWrongVersion(string problem) => T($"That copy is not the version this server needs ({problem}).", $"Эта копия не той версии ({problem}).");
    public static string ErrStillMismatch(string problem) => T($"Some files still do not match after download ({problem}).", $"После загрузки файлы всё ещё не совпадают ({problem}).");
    public static string ErrFirewallDeclined => T("The Windows permission prompt was declined, so the firewall rules were not added. Press Try again and choose Yes.", "Разрешение Windows отклонено — правила брандмауэра не добавлены. Нажмите «Повторить» и «Да».");
    public static string ErrSteamCopyMissing => T("The Steam copy of AION2 was not found. Set steamClientPath in launcher.json.", "Копия AION2 в Steam не найдена. Укажите steamClientPath в launcher.json.");
    public static string ErrGameNotInstalled => T("The game is not installed.", "Игра не установлена.");
    public static string ErrGameAlreadyRunning => T("The game is already running.", "Игра уже запущена.");
    public static string ErrGameFilesChanged(string problem) => T($"Game files changed ({problem}). Use Check all files, or Uninstall and set up again.", $"Файлы изменились ({problem}). Проверьте все файлы или переустановите.");
    public static string ErrGameFilesChangedUninstall(string problem) => T($"Game files changed ({problem}). Uninstall and set up again.", $"Файлы изменились ({problem}). Удалите и настройте заново.");
    public static string ErrFirewallMissing => T("The firewall rules are missing and setup was cancelled.", "Нет правил брандмауэра, настройка отменена.");
    public static string ErrSteamLaunchChanged => T("Steam's launch entry for the game changed; A2Core Launcher needs an update.", "Точка входа Steam изменилась; нужно обновить лаунчер.");
    public static string ErrGameDidNotStart => T("The game did not start.", "Игра не запустилась.");
    public static string ErrHostsCancelled => T("The game needs the hosts entries for this session; the prompt was cancelled.", "Нужны записи hosts; запрос отменён.");
    public static string ErrHostsNotAdded => T("The hosts entries were not added (see elevated.log in %LOCALAPPDATA%\\A2CorePrivate).", "Записи hosts не добавлены (см. elevated.log в %LOCALAPPDATA%\\A2CorePrivate).");
    public static string ErrRepairFile(string path) => T($"Could not repair: {path} still does not match.", $"Не удалось починить: {path} не совпадает.");

    public static string StatusLinkingSteam => T("Linking the game files from your Steam install (no extra disk space)…", "Связка файлов из Steam (без доп. места)…");
    public static string StatusCopyingSteam => T("Copying the game files from your Steam install (about 88 GB)…", "Копирование из Steam (около 88 ГБ)…");
    public static string StatusCheckingGameFiles => T("Checking the game files…", "Проверка файлов игры…");
    public static string StatusCheckingMatches => T("Checking which files already match…", "Проверка совпадающих файлов…");
    public static string StatusLinkingMatching => T("Linking matching files from your folder (no extra disk space for those files)…", "Связка совпадающих файлов (без доп. места)…");
    public static string StatusCopyingMatching => T("Copying matching files from your folder…", "Копирование совпадающих файлов…");
    public static string StatusDownloadingMissing(int count, double gb) => T($"Downloading {count} missing or mismatched files from Steam ({gb:0.#} GB)…", $"Загрузка {count} файлов из Steam ({gb:0.#} ГБ)…");
    public static string StatusAllowFirewall => T("Allowing the game to reach only this server (approve the Windows prompt)…", "Доступ игры только к этому серверу (подтвердите запрос Windows)…");
    public static string StatusStartingSteam => T("Starting Steam…", "Запуск Steam…");
    public static string StatusGettingLaunchCode => T("Getting a launch code from the server…", "Получение кода запуска с сервера…");
    public static string StatusPatchNotOnSteam => T("Client patch paks are not applied to the Steam copy (its files are never changed).", "Патч-паки не применяются к копии Steam (файлы не меняются).");
    public static string StatusStartingGame => T("Starting the game…", "Запуск игры…");
    public static string StatusDnsInjectFailed => T("DNS inject failed; using hosts file (approve the Windows prompt)…", "DNS inject не сработал; используется hosts (подтвердите запрос)…");
    public static string StatusDnsInjectUnavailable => T("DNS inject unavailable; using hosts file (approve the Windows prompt)…", "DNS inject недоступен; используется hosts (подтвердите запрос)…");
    public static string StatusPointingHosts => T("Pointing the game's online services at this server (approve the Windows prompt)…", "Направление онлайн-сервисов игры на сервер (подтвердите запрос)…");

    public static string TitleUseExistingFiles => T("Use your existing files", "Использовать имеющиеся файлы");
    public static string IntroUseExisting(long present, long total, double still) => T(
        $"{present / 1e9:0.0} GB of {total / 1e9:0.0} GB already match this server's version. About {still / 1e9:0.0} GB still needs to come from Steam with your account.",
        $"{present / 1e9:0.0} ГБ из {total / 1e9:0.0} ГБ уже совпадают. Около {still / 1e9:0.0} ГБ нужно загрузить из Steam.");
    public static string UseFolderInPlace => T("Use this folder in place", "Использовать эту папку");
    public static string CopyThenDownload => T("Copy to private folder, then download", "Скопировать в локальную папку и загрузить");
    public static string LinkBackToDownload => T("Back to download page", "Назад к загрузке");
    public static string TitleInstallFromSource(string desc) => T("Installing", "Установка");
    public static string IntroInstallFromSource(string name, string desc, string target) => T(
        $"Found the server's version in {desc}. {name} puts it in its own folder ({target}) and adds firewall rules so the game can reach only this server.",
        $"Найдена версия в {desc}. {name} поместит игру в {target} и настроит брандмауэр только для этого сервера.");
    public static string WindowsFirewallOnce => T("Windows asks for permission once at the end: choose Yes.", "Windows один раз запросит разрешение — выберите «Да».");
    public static string FolderDialogDescription => T("Choose the folder with this server's game version: the one that contains the Aion2 and Engine folders", "Папка с версией игры для сервера (с Aion2 и Engine)");
    public static string ErrFolderNoGame => T("That folder has no game files. Choose the folder that contains the Aion2 and Engine folders.", "В папке нет игры. Выберите папку с Aion2 и Engine.");

    // ---------- Steam download phases ----------
    public static string SteamAskingVersion => T("Asking Steam for the game version…", "Запрос версии игры у Steam…");
    public static string SteamDownloadFinished => T("Download finished.", "Загрузка завершена.");
    public static string SteamResuming => T("Resuming download…", "Продолжение загрузки…");
    public static string SteamDownloadingMissing => T("Downloading missing files…", "Загрузка недостающих файлов…");
    public static string SteamDownloading => T("Downloading…", "Загрузка…");

    // ---------- Install.exe errors ----------
    public static string ErrUnknownExePath => T("The running program's path is unknown.", "Неизвестен путь к программе.");
    public static string ErrAlreadyRunning(string folder) => T(
        "A2Core Launcher is already running from " + folder + ". Close it and try again.",
        "A2Core Launcher уже запущен из " + folder + ". Закройте и повторите.");
    public static string ErrShortcuts(string msg) => T($"The shortcuts could not be made ({msg}).", $"Не удалось создать ярлыки ({msg}).");

    public static string SpaceRequirementOk(string folder, double still, double free) => T(
        $"Saving to {folder} ({still:0.0} GB to download, {free:0.0} GB free).",
        $"Папка {folder} ({still:0.0} ГБ загрузки, свободно {free:0.0} ГБ).");
    public static string SpaceRequirementFail(double need) => T(
        $"No drive has enough free space for about {need:0.0} GB plus headroom. Free up space, then press Check again.",
        $"Ни на одном диске нет ~{need:0.0} GB с запасом. Освободите место и нажмите «Проверить снова».");

    public static string DialogTitle => T("Launcher", "Лаунчер");
    public static string LangEnglish => "English";
    public static string LangRussian => "Русский";

    public static string SteamSourceInstall => T("your Steam install", "ваша установка Steam");
    public static string SteamSourceInstallSizes => T("your Steam install (sizes match)", "ваша установка Steam (размеры совпадают)");

    public static string IntroExistingFolderChoice => T(
        "Use the folder in place (download only what is missing there), or copy matching files into the launcher's private folder first.",
        "Использовать папку как есть (дозагрузить только недостающее) или сначала скопировать совпадающие файлы в локальную папку лаунчера.");
    public static string IntroAdoptFolderCheck => T(
        "Checking that folder, then adding firewall rules so the game can reach only this server. Windows asks for permission once: choose Yes.",
        "Проверка папки и настройка брандмауэра только для этого сервера. Windows один раз запросит разрешение — выберите «Да».");
    public static string AllFilesMatchShort => T("All files match the server's version.", "Все файлы совпадают с версией сервера.");

    public static string ErrSteamConnect => T("Could not connect to Steam.", "Не удалось подключиться к Steam.");
    public static string ErrSteamDisconnected => T("Steam closed the connection.", "Steam разорвал соединение.");
    public static string ErrSteamSignInNotApproved(string result) => T($"Steam sign-in was not approved ({result}).", $"Вход в Steam не подтверждён ({result}).");
    public static string ErrSteamSignInFailed(string result) => T($"Steam sign-in failed ({result}).", $"Не удалось войти в Steam ({result}).");
    public static string ErrSteamNoProductInfo(uint appId) => T($"Steam has no product info for app {appId}.", $"У Steam нет данных о приложении {appId}.");
    public static string ErrSteamNoDepotList => T("Steam returned no depot list for this app.", "Steam не вернул список депо для этого приложения.");
    public static string ErrSteamDepotNotListed(uint depotId, uint appId) => T($"Depot {depotId} is not listed for app {appId}.", $"Депо {depotId} не указано для приложения {appId}.");
    public static string ErrSteamNoPublicManifest(uint depotId) => T($"Depot {depotId} has no public manifest on Steam.", $"У депо {depotId} нет публичного манифеста в Steam.");
    public static string ErrSteamNoGameInLibrary => T("This Steam account does not have the game. Add it to your Steam library, then try again.", "В этой учётной записи Steam нет игры. Добавьте её в библиотеку и повторите.");
    public static string ErrSteamRefusedFiles(string result) => T($"Steam refused the game files ({result}).", $"Steam отклонил файлы игры ({result}).");
    public static string ErrSteamDownloadNotAllowed => T("Steam did not allow downloading this game version.", "Steam не разрешил загрузку этой версии игры.");
    public static string ErrSteamNoServers => T("Steam offered no download servers.", "Steam не предложил серверы загрузки.");
    public static string ErrSteamManifestDownload => T("Could not download the depot manifest from Steam.", "Не удалось загрузить манифест депо из Steam.");
    public static string ErrSteamFileListDiff(string path) => T($"Steam's file list differs from this server's version ({path}).", $"Список файлов Steam не совпадает с версией сервера ({path}).");

    public static string DownloadRatePart(double mbPerSec) => T($"  ·  {mbPerSec:0.0} MB/s", $"  ·  {mbPerSec:0.0} МБ/с");
    public static string DownloadTimePart(string remaining) => T($"  ·  about {remaining} left", $"  ·  ~{remaining}");
}
