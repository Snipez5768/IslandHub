using PeakPill.Core;

namespace PeakPill.Services;

/// <summary>Holds every service; the island binds against this object.</summary>
public sealed class Hub
{
    public MessageWindow Msg { get; private set; }
    public HotkeyService Hotkeys { get; private set; }
    public ForegroundTracker Foreground { get; private set; }
    public ClipboardService Clipboard { get; private set; }
    public MediaService Media { get; private set; }
    public ShelfService Shelf { get; private set; }
    public LauncherService Launcher { get; private set; }
    public SystemStats Stats { get; private set; }
    public AudioService Audio { get; private set; }
    public RadioService Radios { get; private set; }
    public BrightnessService Brightness { get; private set; }
    public WeatherService Weather { get; private set; }
    public ReminderService Reminders { get; private set; }
    public TodoService Todos { get; private set; }
    public FocusTimerService Timer { get; private set; }
    public SoundService Sounds { get; private set; }
    public GameModeService Game { get; private set; }
    public PrivacyService Privacy { get; private set; }
    public AgentService Agents { get; private set; }
    public NotificationService Notifications { get; private set; }
    public PriceAlertService Prices { get; private set; }
    public SuggestionService Suggestions { get; private set; }
    public DiscordService Discord { get; private set; }
    public ObsService Obs { get; private set; }
    public SteamService Steam { get; private set; }
    public SpotifyService Spotify { get; private set; }
    public MakeRoomService Room { get; private set; }
    public BrowserDownloadService Downloads { get; private set; }
    public AlarmService Alarm { get; private set; }
    public UpdateService Updates { get; private set; }
    public DeviceMoments Devices { get; private set; }
    public LiveFeeds Feeds { get; private set; }
    public ClockModel Clock { get; private set; }
    public AiService Ai { get; private set; }

    public void Init()
    {
        Msg = new MessageWindow();
        Hotkeys = new HotkeyService(Msg);
        Foreground = new ForegroundTracker();
        Foreground.Start();

        Clipboard = new ClipboardService();
        Clipboard.Start(Msg, Foreground);

        Media = new MediaService();
        _ = Media.InitAsync();

        Shelf = new ShelfService();
        Shelf.Load();
        Ai = new AiService();
        Ai.Load();
        Launcher = new LauncherService();
        Launcher.Load();
        // the Steam tile comes once by itself (user wish 2026-11-14); if it is removed later it stays away
        if (!App.Settings.SteamTileAdded && LauncherService.SteamInstalled)
        {
            Launcher.AddSteamLibrary();
            App.Settings.SteamTileAdded = true;
            App.SaveSettings();
        }

        Stats = new SystemStats();
        Stats.Start();
        Audio = new AudioService();
        Audio.Refresh();
        Radios = new RadioService();
        _ = Radios.InitAsync();
        Brightness = new BrightnessService();
        _ = Brightness.RefreshAsync();

        Clock = new ClockModel();
        Clock.Start();
        Weather = new WeatherService();
        Weather.Start();
        Reminders = new ReminderService();
        Reminders.Load();
        Reminders.Start();
        Todos = new TodoService();
        Todos.Load();
        Timer = new FocusTimerService();
        Alarm = new AlarmService();
        Alarm.Start();
        Sounds = new SoundService();

        Game = new GameModeService(Foreground);
        Game.Start();
        Privacy = new PrivacyService();
        Privacy.Start();
        Agents = new AgentService();
        Agents.Start();
        Notifications = new NotificationService();
        Notifications.Start();
        Prices = new PriceAlertService();
        Prices.Load();
        Prices.Start();
        Suggestions = new SuggestionService();
        Suggestions.Start();
        Discord = new DiscordService();
        Discord.Start();
        Obs = new ObsService();
        Obs.Start();
        Steam = new SteamService();
        Steam.Start();
        Spotify = new SpotifyService();
        Spotify.Start();
        Room = new MakeRoomService();
        Room.Start();
        Downloads = new BrowserDownloadService();
        Downloads.Start();
        Updates = new UpdateService(); // started by the island once it listens (see IslandWindow)
        Devices = new DeviceMoments(); // started by the island once it listens
        Feeds = new LiveFeeds();
        Feeds.Start();
    }

    public void Shutdown()
    {
        try { Room?.Stop(); } catch { }
        try { Devices?.Dispose(); } catch { }
        try { Clipboard?.Shutdown(); } catch { }
        try { Hotkeys?.UnregisterAll(); } catch { }
        try { Foreground?.Dispose(); } catch { }
        try { Reminders?.Save(); Todos?.Save(); Shelf?.Save(); } catch { }
    }
}
