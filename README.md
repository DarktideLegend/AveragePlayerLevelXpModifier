# AveragePlayerLevelXpModifier

This is a mod that calculates the highest level character from each player's account on the server (does not include admin accounts) and finds the average. The average player level for the entire server is divided by the character's level to determine their xp modifier.

**Example:**

*Formula* `(player level average / character) = xpModifier`

If your character is level 60 and the average level player on the server is 80. 

`(80 / 60) = 1.3` You will earn 1.3x the xp for all xp earned (quests, mob kills etc.. ..)

**Example:**

If a character is level 80 and the average level player on the server is 60.

`(60 / 80 ) = 0.75` You will earn 0.75x the xp for all xp earned (quests, mob kills etc.. ..)


## Threshold

On top of the average player level modifier. An additional modifier will be applied based on fixed level thresholds.

**example threshold array:**
`int[] LevelThresholds { get; set; } = { 80, 100, 125 }; `

**example threshold cap:**
`double LevelCapModifier { get; set; } = 0.15;`

As you reach these level thresholds, the `LevelCapModifier` is applied to your total xpModifier

**Example:** 

If the XP modifier for a character is 0.1 (meaning they are only earning 0.1x xp) and the player's level is 125, the XP modifier will still undergo reduction based on the threshold conditions:

```c#
xpModifier = 0.1 - (0.1 * 0.15)    // at level 80 Decreased by 15%
            = 0.1 - 0.015
            = 0.085

xpModifier = 0.085 - (0.085 * 0.15)    // at level 100  Decreased by 15%
            = 0.085 - 0.01275
            ≈ 0.07225

xpModifier = 0.07225 - (0.07225 * 0.15)    // at level 125 Decreased by 15%
            = 0.07225 - 0.0108375
            ≈ 0.0614125 // <--- this will be the result

```

Your character after applying these thresholds will only earn 0.06x the xp for all quests, mob kills etc.. etc..


## Commands
`/myxp` 

output ->

```
The average player level of the server: 30
You currently earn 0.5x the amount of xp from kills and quests
```

## Settings

```c#
   
  //This is only for new servers, once a player has reached this level, the algorithm will be used instead of this cap
  uint StartingAverageLevelPlayer = 50;
```

```c#
  // the interval to check the PlayerLevelAverage in minutes
  uint PlayerLevelAverageInterval { get; set; } = 60;
```

```c#
  // level threshold where caps are applied, this is an additional threshold to use to increase difficulty of xp earned
  int[] LevelThresholds { get; set; } = { 50, 80, 100, 125, 150, 200, 225 };
```

```c#
  // this is applied to every LevelThreshold 
  double LevelCapModifier { get; set; } = 0.15;
```

  