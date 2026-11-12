// REPO DirectHost — own in-game Photon server for R.E.P.O. (the lobby host runs the server inside the game).
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using RepoDirectHost.Game;

namespace RepoDirectHost
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "kot.repo.directhost";
        public const string Name = "REPO DirectHost";
        public const string Version = "1.0.1";
        internal static ManualLogSource Log;

        internal static ConfigEntry<string> HostAddress, PublicAddress, Route;
        internal static ConfigEntry<int> MasterPort, GamePort;
        internal static ConfigEntry<bool> Verbose, BotSupport;

        private static readonly string[] Conflicting = { "local.repoversteam.transport", "Self_Hosted_Photon" };
        private float _poll;

        private void Awake()
        {
            Log = Logger;
            HostAddress = Config.Bind("Network", "HostAddress", "", "CLIENT: host address (ip or ip:port). Empty = take it from the Steam lobby (published by the host automatically).");
            PublicAddress = Config.Bind("Network", "PublicAddress", "", "HOST: address to publish for friends (your public IP). Empty = detect automatically (STUN).");
            MasterPort = Config.Bind("Network", "MasterPort", 7777, "HOST: UDP port of the master server (forward it on the router).");
            GamePort = Config.Bind("Network", "GamePort", 7778, "HOST: UDP port of the game server (forward it on the router).");
            Route = Config.Bind("Network", "Route", "Auto", new ConfigDescription("CLIENT: how to reach the host. Auto = best first: public IP / LAN -> Steam P2P -> virtual LAN (Radmin, ZeroTier, Hamachi) -> Steam relay. Direct = never use Steam. Steam = always the Steam tunnel (for testing).", new AcceptableValueList<string>("Auto", "Direct", "Steam")));
            Verbose = Config.Bind("Debug", "Verbose", false, "Log every connection.");
            BotSupport = Config.Bind("Debug", "BotSupport", false, "HOST, testing only: write scene view ids to BepInEx/directhost-views.txt for the DirectHost test bot.");

            foreach (var g in Conflicting)
                if (Chainloader.PluginInfos.ContainsKey(g))
                {
                    Log.LogError($"{Name} is disabled: '{Chainloader.PluginInfos[g].Metadata.Name}' is also installed — both replace Photon networking. Disable one of them in r2modman.");
                    return;
                }

            GameHooks.Apply(new Harmony(Guid));
            Log.LogInfo($"{Name} {Version} loaded");
        }

        private void Update()
        {
            GameHooks.Frame();
            _poll -= UnityEngine.Time.unscaledDeltaTime;
            if (_poll > 0) return;
            _poll = 1f;
            GameHooks.Poll();
            if (BotSupport.Value) GameHooks.DumpViews();
        }

        private void OnApplicationQuit() => GameHooks.Shutdown();
    }
}
