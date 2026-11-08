# Project layout

Stardate's repository is laid out the same way as Confederation's, its *Wing Commander*
sibling, and Retrograde's, its C64 one. This note explains the layout and why each part is
there. These choices aren't specific to emulators and are worth copying into any project like
this.

## Two projects: a core and a player

```
src/Stardate.Core     the emulated machine: a library, with no user interface
src/Stardate.Player   the program you run: a window, sound, input, and the per-game settings
tests/Stardate.Core.Tests
tests/Stardate.Player.Tests
```

The **core** is the PC: CPU, memory, devices, the DOS layer. It takes input as method calls and
produces output as a framebuffer and audio samples. It never opens a window, reads a gamepad or
plays a sound.

The **player** wraps the core in a real program, using SDL3 for the window, audio and input as
Retrograde does.

The player has tests of its own, for what it decides rather than what it shows: its settings, the
games' short names, and the command line run end to end with a stand-in program in place of a
game ([profiles and the menu](profiles-and-the-menu.md#checking-it)).

Splitting the two makes the core *testable without a screen*. A test can load a game, run it for a
number of emulated frames, feed it keystrokes, and compare the framebuffer against a known image,
all in a CI job with no display. Most of an emulator's bugs show up this way. The split also lets
other programs use the core: a debugger, a tracer, a tool that documents the engine.

## The core's public surface is tracked

`Stardate.Core` uses the Microsoft public API analyzer. Every public type and member is listed
in `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`, and the build fails if the code and the
lists disagree. A change to the library's surface then shows up as a change to those files in a
diff, where it's easy to review. Every public member must also carry an XML documentation comment,
or the build fails.

## Strict builds

`Directory.Build.props` applies to every project:

- **Warnings are errors.** A new warning fails the build, so warnings never pile up.
- **Nullable reference types** are on.
- **Package versions are central**, in `Directory.Packages.props`, so all projects use the same
  versions.
- **The core is optimized even in Debug builds.** An emulator's CPU loop runs millions of times a
  second, and an unoptimized build can be too slow to reach the parts of a game you want to debug.

## Tests that need the games

*The Kobayashi Alternative*, *The Promethean Prophecy* and *First Contact* are in the
repository, in `games/stka`, `games/stpp` and `games/stfc`, by permission of their copyright
holders ([NOTICE.md](../NOTICE.md)). The player's build copies `games/` beside the player, and
the tests find it above their binaries (or in `STARDATE_GAMES`), so the tests of the games run
everywhere, CI included. `.gitattributes` marks the folder binary, so git never touches a byte
of it.

The processor's single-step tests need the 80386 suite, which is large and stays out of the
repository: they look for it in `STARDATE_TESTDATA`, or a `_testdata` folder above the test
binary, and pass without checking when it isn't there.

## Continuous integration

`.github/workflows/build.yml` builds, tests and packs on Windows, Linux and macOS on every push to
`main` and every pull request, the same as Retrograde. macOS is advisory, because GitHub's hosted
Macs are sometimes unavailable.

## Releases

Stardate is for people who want to see the original games, not only for developers, so a
release is something to download and run, with no .NET to install.
`.github/workflows/publish.yml` builds one for each of Windows, Linux, and macOS on Apple silicon
and on Intel. A tag such as `v0.1.0` makes a GitHub release of them, with
`.github/release-notes.md` as its notes; a version with a hyphen, such as `v0.1.0-alpha`, is a
prerelease. Run by hand, the workflow only builds the downloads, as the run's artifacts, to try
one before tagging.

Each download is a folder:

- **`stardate`** (`stardate.exe` on Windows), the player as a single program, with the
  .NET runtime and SDL inside it, compressed: about 40 MB. It's not trimmed. Trimming drops what
  the compiler can't see used, and save states reach every field of the machine by reflection,
  which it can't see.
- **`games/`**, the included games, with their notice, which the build copies beside the player
  and the player plays from.
- **`README.md`**, **`NOTICE.md`** and **`LICENSE`**.

The programs aren't signed, so Windows and macOS ask before running them the first time; the
release notes say how to let them. (Not yet tried for Stardate: these workflows came over from
Confederation renamed, and haven't run.)
