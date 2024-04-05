using ACE.Database;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command.Handlers;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

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

        private static readonly object Lock = new object();

        private static uint? _average;

        private static uint PlayerLevelAverage
        {
            get
            {
                var starter = Settings.StartingAverageLevelPlayer;
                if (_average == null || _average < starter)
                {
                    return starter;
                }
                else
                    return (uint)_average;
            }

            set
            {
                _average = value;
            }
        }

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

        private static Dictionary<uint, AccountLevelInfo> AccountIdToMaxLevel = new Dictionary<uint, AccountLevelInfo>();

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
                        continue;

                    var players = PlayerManager.GetAccountPlayers(account.AccountId);

                    if (players == null)
                        continue;

                    if (players.Count <= 0)
                        continue;

                    var highestLevelPlayer = players.Values.MaxBy(player => player.Level);

                    if (highestLevelPlayer != null)
                    {
                        var message = $"[PlayerManager]: Player: {highestLevelPlayer.Name}, Level: {highestLevelPlayer.Level}";

                        var info = new AccountLevelInfo(highestLevelPlayer.Name, (int)highestLevelPlayer.Level);
                        AccountIdToMaxLevel[highestLevelPlayer.Account.AccountId] = info;

                        ModManager.Log(message);
                        messages.Add($"{message}");

                        levels.Add((int)highestLevelPlayer.Level);
                    }
                }

                if (levels.Count > 0)
                {
                    var average = (uint)levels.Average();

                    PlayerLevelAverage = average;
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
            if (level <= 5) return 1;
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
            var realmMultiplierAll = __instance.RealmRuleset?.GetProperty(RealmPropertyFloat.ExperienceMultiplierAll) ?? 1;


            // should this be passed upstream to fellowship / allegiance?
            var enchantment = __instance.GetXPAndLuminanceModifier(xpType);

            // uncomment realmMultiplierAll for ACrealms
            var capped = AddXpCap(amount * enchantment * modifier * playerLevelModifier * realmMultiplierAll , (int)__instance.Level);

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

        [HarmonyPrefix]
        [HarmonyPatch(typeof(WorldManager), nameof(WorldManager.Open), new Type[] { typeof(Player) })]
        public static bool PreOpen(Player player)
        {
            GetPlayerLevelAverage();
            //Return false to override
            //return false;

            //Return true to execute original
            return true;
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

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), "CheckForLevelup")]
        public static bool PreCheckForLevelup(ref Player __instance)
        {
            var xpTable = DatManager.PortalDat.XpTable;

            var maxLevel = Player.GetMaxLevel();

            if (__instance.Level >= maxLevel) return false;

            var startingLevel = __instance.Level;
            bool creditEarned = false;

            // increases until the correct level is found
            while ((ulong)(__instance.TotalExperience ?? 0) >= xpTable.CharacterLevelXPList[(__instance.Level ?? 0) + 1])
            {
                __instance.Level++;

                // increase the skill credits if the chart allows this level to grant a credit
                if (xpTable.CharacterLevelSkillCreditList[__instance.Level ?? 0] > 0)
                {
                    __instance.AvailableSkillCredits += (int)xpTable.CharacterLevelSkillCreditList[__instance.Level ?? 0];
                    __instance.TotalSkillCredits += (int)xpTable.CharacterLevelSkillCreditList[__instance.Level ?? 0];
                    creditEarned = true;
                }

                // break if we reach max
                if (__instance.Level == maxLevel)
                {
                    __instance.PlayParticleEffect(PlayScript.WeddingBliss, __instance.Guid);
                    break;
                }
            }

            if (__instance.Level > startingLevel)
            {
                var message = (__instance.Level == maxLevel) ? $"You have reached the maximum level of {__instance.Level}!" : $"You are now level {__instance.Level}!";

                message += (__instance.AvailableSkillCredits > 0) ? $"\nYou have {__instance.AvailableExperience:#,###0} experience points and {__instance.AvailableSkillCredits} skill credits available to raise skills and attributes." : $"\nYou have {__instance.AvailableExperience:#,###0} experience points available to raise skills and attributes.";

                var levelUp = new GameMessagePrivateUpdatePropertyInt(__instance, PropertyInt.Level, __instance.Level ?? 1);
                var currentCredits = new GameMessagePrivateUpdatePropertyInt(__instance, PropertyInt.AvailableSkillCredits, __instance.AvailableSkillCredits ?? 0);

                if (__instance.Level != maxLevel && !creditEarned)
                {
                    var nextLevelWithCredits = 0;

                    for (int i = (__instance.Level ?? 0) + 1; i <= maxLevel; i++)
                    {
                        if (xpTable.CharacterLevelSkillCreditList[i] > 0)
                        {
                            nextLevelWithCredits = i;
                            break;
                        }
                    }
                    message += $"\nYou will earn another skill credit at level {nextLevelWithCredits}.";
                }

                if (__instance.Fellowship != null)
                    __instance.Fellowship.OnFellowLevelUp(__instance);

                if (__instance.AllegianceNode != null)
                    __instance.AllegianceNode.OnLevelUp();

                __instance.Session.Network.EnqueueSend(levelUp);

                __instance.SetMaxVitals();

                // play level up effect
                __instance.PlayParticleEffect(PlayScript.LevelUp, __instance.Guid);

                __instance.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Advancement), currentCredits);

                HandlePlayerLevelUp(__instance);
            }

            return false;

        }

        public class AccountLevelInfo
        {
            public string Name { get; set; }
            public int HighestLevel { get; set; }

            public AccountLevelInfo(string name, int highestLevel)
            {
                Name = name;
                HighestLevel = highestLevel;
            }
        }



        private static void HandlePlayerLevelUp(Player player)
        {
            if (!AccountIdToMaxLevel.TryGetValue(player.Account.AccountId, out AccountLevelInfo account) || player.Level > account.HighestLevel)
            {
                AccountIdToMaxLevel[player.Account.AccountId] = new AccountLevelInfo(player.Name, (int)player.Level);
            }


            var levels = AccountIdToMaxLevel.Values.Select(info => info.HighestLevel);
            PlayerLevelAverage = (uint)levels.Average();

            Debugger.Break();
        }

        #endregion
    }

}
