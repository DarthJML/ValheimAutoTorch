# AutoTorch

Never run around your base refuelling torches again.

Every torch, sconce, brazier, jack-o-turnip, campfire and hearth keeps itself fuelled from chests nearby. Put resin (or wood, or whatever that fire burns) in a chest, and AutoTorch tops the fires up for you.

## How it works
- Every 2 minutes it checks each fire. If a fire is not full, it takes exactly the missing fuel from a chest within reach (32 m by default, which is 16 floor tiles).
- It always leaves at least 1 of the fuel item in each chest, so sorting mods (like Robotic Inbox: Cybercrate Edition) keep sending that item to the same chest.
- Only one player ever feeds a given fire, so there is no double feeding in multiplayer.
- No new pieces and no new items. You can add it to or remove it from an existing world at any time.

## Settings (BepInEx/config/DarthJML.AutoTorch.cfg)
| Setting | Default | What it does |
|---|---|---|
| Enabled | true | Turn the mod on or off |
| Radius | 32 | How far (in meters) from each fire it looks for chests |
| CheckSeconds | 120 | Seconds between checks |
| RefuelOnlyAtNight | false | Only top fires up at night. They still burn during the day; most fires can't be turned off. |
| IncludeWoodFires | true | Also feed campfires, hearths, bonfires and hot tubs |
| KeepInChest | 1 | Always leave this many of the fuel item in each chest |
| NeverUseTheseItems | (empty) | Fuel items it should never take, separated by commas |
| AlwaysLitNoFuel | false | Cheat: keeps every fire full without using any fuel |
| Debug | false | Log every refuel |

## Multiplayer
Install it on every player. The player who owns a fire (usually the nearest one) feeds it from chests they can open. It adds no pieces or network messages, so it should not need the server, but that has only been tested solo so far.

## Pairs well with
Robotic Inbox: Cybercrate Edition, also by DarthJML. Its crate sorts your resin into a chest, and AutoTorch burns it from there.
