namespace ArcEnCiel.Link.Swarm;

internal static class ArcEnCielLinkRuntime
{
    private static readonly object SyncRoot = new();
    private static bool _initialized;

    public static ArcEnCielLinkConfig Config { get; private set; } = new();
    public static ArcEnCielLinkWorker Worker { get; private set; } = new(new ArcEnCielLinkConfig());

    public static void Initialize()
    {
        lock (SyncRoot)
        {
            if (_initialized)
            {
                return;
            }

            Config = ArcEnCielLinkConfig.Load();
            Worker = new ArcEnCielLinkWorker(Config);
            Worker.Start();
            Worker.SetWorkerEnabled(Config.Enabled);
            _initialized = true;
        }
    }

    public static void Shutdown()
    {
        lock (SyncRoot)
        {
            if (!_initialized)
            {
                return;
            }

            Worker.Dispose();
            _initialized = false;
        }
    }

    public static void ApplyWorkerState(bool enable, string? linkKey, string? apiKey)
    {
        lock (SyncRoot)
        {
            Config = ArcEnCielLinkConfig.Load();

            if (linkKey is not null)
            {
                Config.LinkKey = linkKey;
            }

            if (apiKey is not null)
            {
                Config.ApiKey = apiKey;
            }

            Config.Enabled = enable;
            Config.Save();

            Worker.UpdateConfig(Config);
            Worker.SetWorkerEnabled(enable);
        }
    }

    public static void ApplyConfig(Action<ArcEnCielLinkConfig> update)
    {
        lock (SyncRoot)
        {
            Config = ArcEnCielLinkConfig.Load();
            update(Config);
            Config.Save();

            Worker.UpdateConfig(Config);
            Worker.SetWorkerEnabled(Config.Enabled);
        }
    }

    public static void SaveConfig(ArcEnCielLinkConfig config)
    {
        lock (SyncRoot)
        {
            Config = config;
            Config.Save();
            Worker.UpdateConfig(Config);
        }
    }
}
