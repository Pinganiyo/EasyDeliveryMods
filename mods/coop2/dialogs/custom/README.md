# Custom co-op dialogs

`coop_lines.csv` holds new NPC lines for two-player games, written in each character's own voice.
Every NPC has 5 lines for each of two situations (120 lines in total):

| situation | when it is used |
|-----------|-----------------|
| `p2`      | Player 2 talks to the NPC (the NPC meets "the other one"). |
| `duo`     | Both players are in the same shop when the NPC is talked to. |

Keys per situation: `greeting`, `sendoff`, `interaction_1` .. `interaction_3`.
Columns: `npc_id` (the game's SDxx id), `name`, `workplace`, `situation`, `key`, `text`.
Gus's interaction lines keep the game's `$price` placeholder (he is the fuel seller).
All text is plain ASCII, no line is longer than 148 characters.

## Voice notes (what each line imitates)

| NPC | Where | How they talk |
|-----|-------|---------------|
| Theo (SD30) | Easy Eats | Earnest new-age positivity, "mate", "Cheers!", accidental nonsense wisdom. |
| Cici (SD66) | Pawn Shop | Paranoid conspiracy theories, counts things, trusts nobody. |
| Fortino (SD49) | Depot | Shy, lovesick, ellipses, hides from Ellie, gifts of dirt. |
| Ellie (SD11) | Bakery/Cafe | Loud, chaotic, wrong facts and wrong math, shouted sendoff. |
| Fit (SD51) | Cafe | Shameless pickup lines and puns, proud of them. |
| Tooey (SD02) | Restaurant | Old man, "whippersnapper", "Jeepers", nostalgic about Seb, teases Foreman. |
| Reed (SD73) | Auto Shop | Forgetful, mistakes you for Seb, repairs that never happen. |
| Fives (SD55) | Bar | Dry, protective twin of Sixo, tired of his schemes. |
| Sixo (SD56) | Bar | Business-pitch addict, sells anything, "Yo!". |
| Ada (SD08) | Flower Shop | Gentle stutter (hyphenated), discovering feelings. |
| Foreman (SD24) | Pizzeria | Gruff Scots ("tae", "ye"), fish nicknames, fishing daydreams. |
| Gus (SD19) | Fuel station | Sour insults, always ends with the price. |

## How the game stores NPC dialog (for the in-game hook)

- NPC lines live in `sNPCInformation.npcData[i].dialog[]` (English), plus the greeting / sendoff rows.
- Shopkeepers are `sTransactionNPC`; their dialog uses `$price`, `$noPay`, `$partial`, `$cantPay`.
- Today a talk press from Player 2 runs the NPC's `ButtonPressed`, which locks Player 1's controller and uses
  Player 1's HUD, so Player 2 needs its own dialog flow before these lines can be shown.
