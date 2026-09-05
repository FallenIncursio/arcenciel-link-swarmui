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

    public static void ApplyWorkerState(bool enable, string? linkKey)
    {
        lock (SyncRoot)
        {
            ArcEnCielLinkConfig candidate = ArcEnCielLinkConfig.Load();

            candidate.ValidateWorkerChange(enable, linkKey);

            if (linkKey is not null)
            {
                candidate.LinkKey = linkKey;
            }

            candidate.Enabled = enable;
            candidate.Save();
            Config = candidate;

            Worker.UpdateConfig(Config);
            Worker.SetWorkerEnabled(enable);
        }
    }

    public static void ApplyConfig(Action<ArcEnCielLinkConfig> update)
    {
        lock (SyncRoot)
        {
            ArcEnCielLinkConfig candidate = ArcEnCielLinkConfig.Load();
            candidate.Enabled = Worker.IsWorkerRunning;
            update(candidate);
            candidate.ValidateWorkerChange(candidate.Enabled, candidate.LinkKey);
            string? managedUrl = Environment.GetEnvironmentVariable("ARCENCIEL_LINK_URL");
            if (managedUrl is not null && candidate.BaseUrl != managedUrl.Trim().TrimEnd('/'))
                throw new ArgumentException("ARCENCIEL_LINK_URL is managed by the runtime environment; update it and restart");
            candidate.Save();
            Config = candidate;

            Worker.UpdateConfig(Config);
            Worker.SetWorkerEnabled(candidate.Enabled);
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
