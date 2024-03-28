using ACE.Database;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command.Handlers;
using ACE.Server.Managers;

namespace XpModifier
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

        private static DateTime LastCheck = DateTime.UtcNow;


        public class PlayerLevelAverageException : Exception
        {
            // Constructor that accepts a message
            public PlayerLevelAverageException(string message) : base(message)
            {
            }

            // Constructor that accepts a message and an inner exception
            public PlayerLevelAverageException(string message, Exception innerException) : base(message, innerException)
            {
            }
        }

        private static List<string> GetPlayerLevelAverage()
        {
            var messages = new List<string>();

            var intro = "[PlayerManager] Getting the PlayerLevelAverage for the server";
            ModManager.Log(intro);
            messages.Add(intro);
            try
            {
                var accountNames = DatabaseManager.Authentication.GetListofAccountsByAccessLevel(AccessLevel.Player);

                if (!accountNames.Any())
                {
                    throw new PlayerLevelAverageException("There are no accounts currently created on the server.");
                }

                var levels = new List<int>();

                foreach (var accountName in accountNames)
                {
                    var account = DatabaseManager.Authentication.GetAccountByName(accountName);

                    if (account == null)
                    {
                        continue;
                    }

                    var players = PlayerManager.GetAccountPlayers(account.AccountId);

                    if (players.Count <= 0)
                        continue;

                    var highestLevelPlayer = players.Values.MaxBy(player => player.Level);

                    if (highestLevelPlayer != null)
                    {
                        var message = $"[PlayerManager]: Player: {highestLevelPlayer.Name}, Level: {highestLevelPlayer.Level}";

                        ModManager.Log(message);
                        messages.Add($"{message}");

                        levels.Add((int)highestLevelPlayer.Level);
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

                var outro = $"[PlayerManager] Finished getting the PlayerLevelAverage, the average player level is {PlayerLevelAverage}";
                ModManager.Log(outro);
                messages.Add(outro);
                return messages;
            }
            catch (Exception ex)
            {
                var error = $"[PlayerManager] Error occurred while getting PlayerLevelAverage: {ex.Message}";
                ModManager.Log(error, ModManager.LogLevel.Error);
                messages.Add(error);
                return messages;
            }
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
        [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.Tick))]
        public static void PostTick()
        {
            if (LastCheck + PlayerLevelInterval <= DateTime.UtcNow)
            {
                LastCheck = DateTime.UtcNow;
                GetPlayerLevelAverage();
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WorldManager), nameof(WorldManager.Open), new Type[] { typeof(Player) })]
        public static void PostOpen(Player player)
        {
            GetPlayerLevelAverage();
            LastCheck = DateTime.UtcNow;
        }


        [CommandHandler("myxp", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0, "Show your xp modifier based on global average", "")]
        public static void HandleMyXp(Session session, params string[] parameters)
        {
            var modifier = GetPlayerLevelXpModifier((int)session.Player.Level);
            CommandHandlerHelper.WriteOutputInfo(session, $"The average player level of the server: {PlayerLevelAverage}", ChatMessageType.Broadcast);
            CommandHandlerHelper.WriteOutputInfo(session, $"You currently earn {modifier.ToString("0.00")}x the amount of xp from kills and quests", ChatMessageType.Broadcast);
        }

        [CommandHandler("player-level-xp", AccessLevel.Developer, CommandHandlerFlag.None, 0, "Show your xp modifier based on global average", "")]
        public static void HandleGetPlayerLevelXp(Session session, params string[] parameters)
        {
            var messages = GetPlayerLevelAverage();
            foreach(var message in messages)
            {
                session.Player.SendMessage(message, ChatMessageType.System);
            }
        }
        #endregion
    }

}
