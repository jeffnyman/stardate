# Tools

`tools/` holds what was built to play and study the games, rather than to run them for a player.

```
tools/Stardate.Driver   stardate-driver: plays a game headless, from a script, and looks inside it as it plays
tools/routes            stfc.route, stka.route and stpp.route: each game played from a new game to its winning ending
```

The driver was written to check the walkthroughs in `walkthroughs/`. All three walkthroughs were played to the end with it, and the routes here are those plays, kept so they can be run again. It's also a worked example of what the core's split from the player is for ([project layout](../docs/project-layout.md)). The driver uses only `Stardate.Core`'s public API: it loads a game into a `Machine`, runs it a number of instructions at a time, presses keys, reads the text screen and memory, and saves and loads the machine's state. No window, sound or SDL is involved.

## Running it

From the repository:

```
dotnet run --project tools/Stardate.Driver -c Release -- play stka tools/routes/stka.route
dotnet run --project tools/Stardate.Driver -c Release -- play stpp tools/routes/stpp.route
dotnet run --project tools/Stardate.Driver -c Release -- play stfc tools/routes/stfc.route
```

Each route takes some minutes. It prints what the game says as it goes, and ends with exit code 0 if every step went as expected, or 3, with the step that didn't, if one failed.

There are two commands:

- **`play <game> <route>`** plays a route a step at a time. After each step it saves the machine's state in `driver-states/<game>/<step>.state` (or `--states <folder>`). `--from <step>` picks the route up from the state the step before left, and `--to <step>` stops after a step, so one step can be worked on without replaying the game up to it.
- **`run <game> <state|-> <state out|-> "<script>"`** runs one script from a saved state (or, with `-`, a new game), and saves where it ends (or, with `-`, doesn't). `--screen` prints the whole screen after it. This is the command for experiments: try a script against a step's state, look, change it, try again.

The other options are `--games <folder>` (the games are found in the repository's `games/` by default), `--cycles <n>` (instructions a millisecond, 240 by default, the 8088's speed), `--start <time>` (the clock's start) and `--stop-at-pauses` (leave a "press a key" pause waiting, rather than pressing through it).

## Playing a game without seeing it

A person at the keyboard waits until the game is ready before typing, and reads what it says. The driver has to do both by itself, and almost all of its code is about that.

### Readiness

A key pressed while the game is busy is lost, or worse, arrives somewhere unexpected. So before each key the driver waits until the game is *ready*, which it decides in two parts.

The first part is the same for every game: the screen has to stay still for a moment, 0.3 seconds (1.2 in *First Contact*, below). Some things on the screen move all the time, so they don't count. In *The Kobayashi Alternative* that's the twinkling stars (the `·` characters) and the `Time:dd dd dd` clock.

The second part is different for each game, because they read the keyboard differently. Each game's `GameProfile` (in `GameProfile.cs`) gives its own check:

- ***The Kobayashi Alternative*** polls the keyboard in a loop of its own. The driver samples where the processor is, 24 times, 487 instructions apart (an odd number, so the samples don't fall in step with the loop). The game is ready when at least half of the samples fall inside the loop, at `108C:0560`–`05A0`. That address was found with `{where}` (below), run while the game sat at its prompt.
- ***First Contact*** is *The Kobayashi Alternative*'s engine a year earlier, with the same windows, menus and order line, so its profile is Kobayashi's with its own loops: `0E78:06E8`–`0728` at the order prompt, and `23A2:2A40`–`2A80` at the computer's `::Inquiry::` prompt. A reply's "Press any key to continue" box can come up to a second after the reply, while the game polls the keyboard, so the screen must stay still for 1.2 seconds, and no pause may be showing.
- ***The Promethean Prophecy*** reads keys through the BIOS. It's ready when the BIOS's keyboard buffer is empty (the words at `0040:001A` and `0040:001C` are equal) and the command window shows a fresh `//` prompt. A still screen isn't enough here: the game can clear its story window and then work for a while before it writes anything.

When it types a line, the driver also waits for each character to be taken before it sends the next one.

### Pauses

When a game fills its window it stops for a key. *The Kobayashi Alternative* shows "Hit any key to continue", *First Contact* "Press any key to continue", and *The Promethean Prophecy* a line holding only `↓`. *First Contact*'s pictures, in the CGA's graphics mode, are pauses too: the driver reads no text from them, so it knows one by the mode, and by the processor waiting in the BIOS for a key (at `F000:0184`). The driver keeps a copy of the screen and presses Space, then waits for the screen to change before it would press again: a key the pause didn't take goes to the order line, and in *First Contact* the clock stops while a line is being typed. With `--stop-at-pauses`, it leaves the pause waiting instead.

What the game said is worked out from those copies. *The Promethean Prophecy*'s new lines are read from its story window. *The Kobayashi Alternative*'s and *First Contact*'s replies are read from their reply boxes and shown as "Speaker: text", and *First Contact*'s story from its room window too. When the window's title changes, the new place is shown as `[place]`. The text a script looks for is matched against this as well as the screen, so a phrase that wraps inside a narrow reply box is still found.

### Each game's quirks

These are things that went wrong until the driver learned about them, and they're in the profiles now.

In *The Kobayashi Alternative*:

- Esc closes a reply box, but with no box open it moves the cursor along the order line, so the driver presses Esc only when a box is open.
- To clear the order line, the driver presses Backspace once for each character before the cursor. It reads the cursor's position from the BIOS data area, at `0040:0050`.
- An order that fills the whole line ends the game.

In *First Contact*:

- A reply stays until the next order, and no key closes it: Esc moves the cursor, and anything else is typed. So the driver leaves replies alone.
- Typed text is speech, to whoever Kirk is speaking to. What Kirk does himself (examine, take, touch, use, adjust) is chosen from menus, by `{menu}`.
- The game looks ready a moment after an order before it is, so its route waits a second after each (`@pace 1`).

*The Promethean Prophecy* wraps its lines, so a phrase the route looks for can be split across two of them. The driver joins wrapped lines before it searches.

### The same game every time

Both games take their chances from the clock. A route has to play the same way every time, so the driver starts the machine's clock at a fixed time: the 1st of March, 1987, at nine in the morning (`Machine.StartTime`). After that the clock follows the machine's own time, not the real one. The same keys at the same moments then give the same game.

The pace of a route matters too. The game runs on while the player reads, so how long the driver waits after each order changes what happens. `@pace` in a route fixes that wait.

## Scripts

A script is text with commands in braces. Plain text is typed as it is, and anything in braces is a key or a command. Braces nest, so a command can take a script as its argument:

```
{say:Spock, follow me}
{repeat:20:{say:Wait}{if:lies!:{break}}}
```

### Keys

`{enter}`, `{esc}`, `{space}`, `{bksp}`, `{up}` `{down}` `{left}` `{right}`, `{pgup}` `{pgdn}` `{home}` `{end}` and `{f1}`–`{f10}`. `{down*3}` presses a key three times. `{w2}` or `{w1.5}` waits that many seconds.

### Orders

| Command | What it does |
| --- | --- |
| `{say:ORDER}` | Gives an order the way the game takes one: clears the line, types the order, presses Enter, and waits until the game is ready again. |
| `{idle}` | Waits until the game is ready, up to a minute. |
| `{pace:N}` | Waits N seconds after each order from now on. |
| `{wait:TEXT;TEXT}` | Runs, pressing through pauses, until one of the texts appears, up to 150 seconds; `{wait:300:TEXT}` waits up to 300. |
| `{expect:TEXT;TEXT}` | Fails the step unless one of the texts has appeared on the screen during it, including on screens that were pressed through. This is how a route knows it's still on course. |
| `{retry:TEXT>ORDER>N}` | Gives the order again, up to N times (8 by default), until the text appears. |
| `{trade:WHO:ITEM>WANT>N}` | Gives the item to WHO, then gives back whatever comes in return, up to N times (8 by default), until the item it wants comes back. It reads what was given from the reply ("He hands you a brush"). |
| `{menu:KEY:ITEM:ITEM}` | Opens a menu with a key (`f8`, say) and chooses each item by its name, going on through the menu's pages (`F10 ...MORE`) until it's there; a box left open afterwards is closed. `{menu:f7:phaser:curtain rope}` uses the phaser on the curtain rope. Each key waits until the game is ready, as a menu drawn at 8088 speed takes a moment, and a key pressed before it's drawn goes to the page before. |
| `{show}` / `{screen}` | Keeps a copy of the screen, or prints it. |

There are also commands for *The Kobayashi Alternative*'s menus and screens:

| Command | What it does |
| --- | --- |
| `{walk:DIRECTION:TEXT}` | Steps with an arrow key until the text appears. |
| `{goto:X.Y}` | Walks across a planet's surface to a place, reading the location panel. |
| `{take:ITEM}` | Picks up an item, using the F4 menu. |
| `{pack:ITEM}` | Puts an item in the pack, using the F2 inventory. |
| `{hand:ITEM}` | Takes an item into the hands, using the F2 inventory. |
| `{wear:ITEM}` | Wears an item, using the F2 inventory. |

### Control

| Command | What it does |
| --- | --- |
| `{repeat:N:SCRIPT}` | Runs the script up to N times, or until a `{break}` in it. |
| `{if:TEXT;TEXT:SCRIPT}` | Runs the script if one of the texts is on the screen now. |
| `{unless:TEXT;TEXT:SCRIPT}` | Runs the script if none of the texts is on the screen now. |
| `{mark:NAME}` | Remembers that something happened, for the rest of the step. |
| `{ifmark:A,B:SCRIPT}` | Runs the script if every one of the marks is set. |
| `{ifnot:A,B:SCRIPT}` | Runs the script if none of the marks is set. |
| `{break}` | Leaves the nearest `{repeat}`. Outside of one, it ends the script. |

After the game exits, only `{expect}` commands still run, checked against the screens kept along the way.

## Routes

A route is a file of steps, one to a line:

```
name | script
name | script | seconds
```

After each step, the route lets the game run on for a second (or the number of seconds in a third column) before saving the state. `#` starts a comment, and an `@pace N` line sets the pace for the whole route. Each step starts from the state the step before it saved, so a route is also a set of save points. Here are two steps of `stpp.route`:

```
# the Northern Promontory: ... the bowl, where a Defender breaks the communicator at its third call
bowl         | {repeat:40:{if:That was costly:{break}}{say:Wait}{if:communicator whistles:{say:Turn on communicator}{say:Turn off communicator}}}{expect:That was costly}
ride         | {say:Put tarpaulin on bowl}{expect:Enclave of the Sustainers}
```

Where a game's chances decide what happens, a route doesn't count on them. It waits for the event, or keeps trading, and then checks that it got there with `{expect}`.

## Tests

`tests/Stardate.Driver.Tests` reads each of the three routes every time the tests run, to check that its steps parse. Playing the routes to their endings takes several minutes, so that's done only when `STARDATE_ROUTES` is set:

```
STARDATE_ROUTES=1 dotnet test tests/Stardate.Driver.Tests -c Release
```

## Looking inside a game

These commands were used to find out how *The Kobayashi Alternative* works. Addresses and values are in hex. An address given as an offset alone is in the game's data segment, which is `1A20` in *The Kobayashi Alternative*.

| Command | What it does |
| --- | --- |
| `{where}` | Shows where the processor spends its time, from 200 samples. |
| `{peek:OFF}` or `{peek:SEG:OFF}` | Prints a word of memory. |
| `{poke:OFF:VALUE}` | Writes a word of memory once. |
| `{hold:OFF:VALUE}` | Keeps a word at a value as the game plays, until `{letgo}`. |
| `{dump:SEG:FILE}` | Writes a 64 KB segment to a file, to disassemble or to compare. |
| `{breakat:SEG:OFF}` | Runs until the processor reaches the address, then prints the registers and the stack. `{breakat:SEG:OFF:FILE}` also dumps the code there. |
| `{runto:TEXT}` | Runs one instruction at a time until the machine's log mentions the text, such as a file being opened. |
| `{tracebefore:TEXT:N}` | Prints the last N instructions before the text appears on the screen. |
| `{traceinto:SEG:FROM:TO:N}` | Runs one instruction at a time until the processor reaches an address between SEG:FROM and SEG:TO, then prints the last N instructions before it, each with its first bytes: how a program came to run where it shouldn't. |
| `{log}` | Prints the machine's log of DOS calls, such as files opened and seeks. |

These are some things found with them, and how:

- **The Achir bomb.** Comparing `{dump}`s taken a few seconds apart, after beaming down to Achir, showed one word climbing: `DS:0F50`. The bomb goes off when it reaches 500 (`01F4`). The route plays from a new game through to the bomb with the word at 348. A `{hold:0F50:0}` keeps the bomb from ever going off while you explore.
- **The tribes.** Another word, `DS:0F52`, counts the tribes won over, one for each step of the five-tribe chain. `{peek:0F52}` after each tribe shows whether that step worked.
- **The two endings.** `{tracebefore}` on the ending's text led to the game-over routine at `0A15:AAA1`. `{breakat:0A15:AAA1}` then showed its argument on the stack: −8 gives message 7, the good ending, and −12 gives message 11, the nova. The good ending depends on a flag at `DS:00DC`. The only instruction that sets it is an `inc` in the mindmeld code, at offset `10CBA` of `ST.EXE`. That's why the ending has to be played in a fixed order: orbit the star, then the mindmeld, then "Kirk to Renner", then beam down the antimatter.
- **The keyboard loop.** `{where}`, run at the order prompt, gave `108C:0560`–`05A0`. That's the range the driver's readiness check for *The Kobayashi Alternative* uses.
- **The Promethean Prophecy's lost saves.** A saved game restored, then hung. `{traceinto}` showed the game's Forth interpreter jumping through a word in its dictionary that held zero. Comparing `{dump}`s from before and after the restore showed that one 1 KB stretch of the dictionary had come back as zeros. That was the save's last block, and it was zeros in `SAVE.DAT` too. Tracing the save showed it copying the right memory into its disk buffer, with a `rep movsb`, and writing that buffer. But the launcher never closes `SAVE.DAT`, and Stardate's DOS left each write in .NET's file buffer until the file was closed, so the last block never reached the disk before the process ended. DOS writes now go straight to the disk (`A_write_reaches_the_disk_though_the_file_is_never_closed`), and saves restore. (The DOS log now gives each write's source address, `from SEG:OFF`, as reads give their destination.)
- **First Contact's twelve-minute saves.** Each save moved the mission clock on twelve minutes. With the machine's time added to each line of the DOS log, the twelve minutes went by before the save file was even created, while the Utilities box and the prompt for the file's name were up: the driver doesn't know those as ready, and waited out its timeout twice. That timeout was the driver's own bug. Each look at the screen runs the game for the profile's still time (1.2 seconds in *First Contact*) and a tenth more, but counted only 0.15, so "40 seconds" was nearly six minutes. Waits are now measured by the machine's clock, and all three routes still win.
