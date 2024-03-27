using ACE.Database;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command.Handlers;
using ACE.Server.Managers;

namespace AveragePlayerLevelXpModifier
{
    [HarmonyPatch]
    public class PatchClass
    {
        #region Settings
        const int RETRIES = 10;

        public static Settings Settings = new();
        static string settingsPath => Path.Combine(Mod.ModPath, "Settings.json");
        private FileInfo settingsInfo = new(settingsPath);

        private JsonSerializerOptions _serializeOptions = new()
        {
            WriteIndented = true,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private void SaveSettings()
        {
            string jsonString = JsonSerializer.Serialize(Settings, _serializeOptions);

            if (!settingsInfo.RetryWrite(jsonString, RETRIES))
            {
                ModManager.Log($"Failed to save settings to {settingsPath}...", ModManager.LogLevel.Warn);
                Mod.State = ModState.Error;
            }
        }

        private void LoadSettings()
        {
            if (!settingsInfo.Exists)
            {
                ModManager.Log($"Creating {settingsInfo}...");
                SaveSettings();
            }
            else
                ModManager.Log($"Loading settings from {settingsPath}...");

            if (!settingsInfo.RetryRead(out string jsonString, RETRIES))
            {
                Mod.State = ModState.Error;
                return;
            }

            try
            {
                Settings = JsonSerializer.Deserialize<Settings>(jsonString, _serializeOptions);
            }
            catch (Exception)
            {
                ModManager.Log($"Failed to deserialize Settings: {settingsPath}", ModManager.LogLevel.Warn);
                Mod.State = ModState.Error;
                return;
            }
        }
        #endregion

        #region Start/Shutdown
        public void Start()
        {
            //Need to decide on async use
            Mod.State = ModState.Loading;
            LoadSettings();

            if (Mod.State == ModState.Error)
            {
                ModManager.DisableModByPath(Mod.ModPath);
                return;
            }

            Mod.State = ModState.Running;
        }

        public void Shutdown()
        {
            //if (Mod.State == ModState.Running)
            // Shut down enabled mod...

            //If the mod is making changes that need to be saved use this and only manually edit settings when the patch is not active.
            //SaveSettings();

            if (Mod.State == ModState.Error)
                ModManager.Log($"Improper shutdown: {Mod.ModPath}", ModManager.LogLevel.Error);
        }
        #endregion

        #region Patches

        private static uint PlayerLevelAverage = Settings.StartingAverageLevelPlayer;

        public static TimeSpan PlayerLevelInterval = TimeSpan.FromMinutes(Settings.PlayerLevelAverageInterval);

        private static DateTime LastCheck = DateTime.MinValue;


        private static void GetPlayerLevelAverage()
        {
            ModManager.Log("[PlayerManager] Getting the PlayerLevelAverage for the server");
            var levels = new List<int>();
            var accountNames = DatabaseManager.Authentication.GetListofAccountsByAccessLevel(AccessLevel.Player);
            var accounts = accountNames.Select(DatabaseManager.Authentication.GetAccountByName);
            var accountIds = accounts.Select(account => account.AccountId);
            var accountPlayers = accountIds.Select(PlayerManager.GetAccountPlayers);

            if (!accountPlayers.Any())
                PlayerLevelAverage = 1;

            foreach (var account in accountPlayers)
            {
                if (account != null)
                {
                    var highestLevelPlayer = account.Values.MaxBy(player => player.Level);
                    if (highestLevelPlayer != null)
                    {
                        ModManager.Log($"[PlayerManager]: Player: {highestLevelPlayer.Name}, Level: {highestLevelPlayer.Level}");
                        levels.Add((int)highestLevelPlayer.Level);
                    }
                }
            }

            if (levels.Count > 0)
            {
                var max = (uint)levels.Max();
                var average = (uint)levels.Average();
                if (max > Settings.StartingAverageLevelPlayer)
                {
                    PlayerLevelAverage = average;
                }
            }

            ModManager.Log($"[PlayerManager] Finished getting the PlayerLevelAverage, the average player level is {PlayerLevelAverage}");
        }

        private static double GetPlayerLevelXpModifier(int level)
        {
            var playerLevelAverage = PlayerLevelAverage;
            return (double)playerLevelAverage / (double)level;
        }

        private static double AddXpCap(double xpModifier, int level)
        {
            var cap = Settings.LevelCapModifier;
            var mod = xpModifier;

            // Define an array of level thresholds
            int[] thresholds = Settings.LevelThresholds;

            // Iterate over the thresholds and apply XP modifier reduction
            foreach (var threshold in thresholds)
            {
                if (level >= threshold)
                {
                    xpModifier -= xpModifier * cap;
                }
            }

            return xpModifier;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), nameof(Player.EarnXP), new Type[] { typeof(long), typeof(XpType), typeof(ShareType) })]
        public static bool PreEarnXP(long amount, XpType xpType, ShareType shareType, ref Player __instance)
        {
            //Console.WriteLine($"{Name}.EarnXP({amount}, {sharable}, {fixedAmount})");

            // apply xp modifiers.  Quest XP is multiplicative with general XP modification
            var questModifier = PropertyManager.GetDouble("quest_xp_modifier").Item;
            var modifier = PropertyManager.GetDouble("xp_modifier").Item;
            if (xpType == XpType.Quest)
                modifier *= questModifier;

            var playerLevelModifier = GetPlayerLevelXpModifier((int)__instance.Level);

            // uncomment for ACRealms 
            //var realmMultiplierAll = __instance.RealmRuleset?.GetProperty(RealmPropertyFloat.ExperienceMultiplierAll) ?? 1;


            // should this be passed upstream to fellowship / allegiance?
            var enchantment = __instance.GetXPAndLuminanceModifier(xpType);

            // uncomment realmMultiplierAll for ACrealms
            var capped = AddXpCap(amount * enchantment * modifier * playerLevelModifier /* *  realmMultiplierAll  */, (int)__instance.Level);

            var m_amount = (long)Math.Round(capped);

            if (m_amount < 0)
            {
                ModManager.Log($"{__instance.Name}.EarnXP({amount}, {shareType})", ModManager.LogLevel.Warn);
                ModManager.Log($"modifier: {modifier}, enchantment: {enchantment}, m_amount: {m_amount}", ModManager.LogLevel.Warn);
                return false;
            }

            __instance.GrantXP(m_amount, xpType, shareType);

            return false;
        }



        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Initialize))]
        public static void PostInitialize()
        {
            GetPlayerLevelAverage();

        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
        public static void PostTick()
        {
            if (LastCheck + PlayerLevelInterval <= DateTime.UtcNow)
            {

                LastCheck = DateTime.UtcNow;
                GetPlayerLevelAverage();
            }
        }

        [CommandHandler("myxp", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0, "Show your xp modifier based on global average", "")]
        public static void HandleMyXp(Session session, params string[] parameters)
        {
            var modifier = GetPlayerLevelXpModifier((int)session.Player.Level);
            CommandHandlerHelper.WriteOutputInfo(session, $"The average player level of the server: {PlayerLevelAverage}", ChatMessageType.Broadcast);
            CommandHandlerHelper.WriteOutputInfo(session, $"You currently earn {(float)modifier}x the amount of xp from kills and quests", ChatMessageType.Broadcast);
        }
        #endregion
    }

}
