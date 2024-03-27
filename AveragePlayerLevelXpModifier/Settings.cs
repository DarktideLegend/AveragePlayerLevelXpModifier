namespace AveragePlayerLevelXpModifier
{
    public class Settings
    {
        // this value is for new servers
        // new servers may have not have any players to start, use this value as an initial baseline
        // if a player's level is below this value, they will have an increased xp modifier, if above, a lower xp modifier
        // once the average player level is above this value, this value will be replaced with the PlayerLevelAverage calculated by this mod
        public uint StartingAverageLevelPlayer = 50;

        // the interval to check the PlayerLevelAverage in minutes
        public uint PlayerLevelAverageInterval = 60;

        // this is applied to every LevelThreshold 
        public double LevelCapModifier { get; set; } = 0.15;

        // level threshold where caps are applied
        public int[] LevelThresholds { get; set; } = { 50, 80, 100, 125, 150, 200, 225 };
    }
}