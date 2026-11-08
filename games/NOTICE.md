# The included games

The files in this folder are the original IBM PC releases of Simon & Schuster's *Star Trek*
interactive fiction:

| Folder | Game | Started by |
|---|---|---|
| `stka` | *Star Trek: The Kobayashi Alternative* (1985) | `ST.EXE` |
| `stpp` | *Star Trek: The Promethean Prophecy* (1986) | `RUN.COM` |
| `stfc` | *Star Trek: First Contact* (1987) | `ST.EXE` |

They are copyrighted works of their owners, and are included in Stardate by permission of their
copyright holders. They are not covered by Stardate's license, the GNU General Public License,
and may not be sold or charged for. See [NOTICE.md](../NOTICE.md).

## What else is here

- **`stka/START`** is the game's own: a saved game it starts from.
- **`stka/STDOC.TXT`** is a condensed set of instructions, with hints, from a later release.
- **`stpp`** was sold as a disk that started itself, without DOS. What's here is the disk as an
  image, `STPP.DAT`, with a small DOS program, `RUN.COM`, that loads the game from it and sends
  its disk reads and writes to the image; saved games go to a second, blank image, `SAVE.DAT`.
  `RUN.COM` was written in 2004 by Mok, whose notes are `README.txt` and `NOTICE.txt`. As they
  say, a game is saved and restored on drive B, which is `SAVE.DAT`; drive A is the game's own
  disk, and saving there would write over it. (Stardate never changes these files: what a game
  writes goes to a copy in its save folder.)
