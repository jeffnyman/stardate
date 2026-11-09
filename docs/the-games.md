# The games

Three *Star Trek* games for the IBM PC, published by Simon & Schuster's Computer Software
Division. Each was first run in Confederation's machine, unchanged, to see how far it got and what
it asked for; this note records what they turned out to need.

## What they ask of the machine

Very little, next to *Wing Commander*. They're 8088 programs for a PC with 128 KB, DOS 2, a
color graphics adapter and a speaker. Run in Confederation's machine, *The Kobayashi Alternative*
and *First Contact* set text mode, read their data and drew their first screens with no service
the machine lacked: everything they called (DOS's files and memory, the BIOS's keyboard and video
mode) was there. *The Promethean Prophecy* stopped at once, because its launcher is a `.COM`
program, which Confederation never had to load.

What they needed that Confederation didn't have:

- **Text.** All three play in 80-column color text, written straight into the display's memory
  at B800:0000 or through the BIOS. Confederation's VGA drew text modes black, and its video BIOS
  answered only what *Wing Commander*'s mode changes needed.
- **CGA graphics.** *First Contact*'s pictures (its `.BIN` files: the title, the bridge, the
  planets) are drawn in the CGA's 320 by 200 four-color mode, with the cyan, magenta and white
  palette. It changes to that mode for a picture and back to text after.
- **`.COM` programs**, for *The Promethean Prophecy*.
- **The speaker**, for the beeps and the sounds of a program that toggles it itself.

## The Kobayashi Alternative (1985)

`ST.EXE`, built with Amber Systems' tools, and nine overlay and data files, `ST.1` to `ST.9`,
which it reads in pieces: it opens itself as a file and reads its overlay from its own end.
`START` is a saved game in the game's own format (it begins `st2.0-B`, as the game's saves do);
`start` is among the program's strings, so it's likely where a new game begins, though that's
yet to be checked. The game opens on Starfleet's orders, a pause, and then the bridge, in windows drawn
with the PC's box characters over a starfield, with Kirk's command line and the Enterprise's
status at the foot of the screen.

## The Promethean Prophecy (1986)

Written in MVP-FORTH by TRANS Fiction Systems, and sold as a disk that started itself, without
DOS. What's here is that disk as an image, `STPP.DAT`, with a launcher written in 2004, `RUN.COM`,
that loads the game from the image into memory, takes over INT 11h and INT 20h for the game's
own use, and sends the game's disk reads and writes to the image: drive A to `STPP.DAT`, drive B
to `SAVE.DAT`, a blank image for saved games. Its notes say it needs memory from 0FC0:0000 to
3000:0000 free, because the game uses fixed addresses there; Stardate's DOS starts its arena at
0A00h, and the launcher loads at 0A05h, below that, so it's free. The launcher uses the 386's
32-bit registers (`66h` prefixes) and `PUSHA`, which is one reason the processor stays a 386.

The game runs in mode 2, the CGA's "black and white" 80-column text. That mode only turned off
the color signal for a composite monitor or television; on IBM's own color display, which took
the colors directly, it was in color like mode 3, and the game uses color in it (its window
titles are blue and red). Stardate draws it in color.

## First Contact (1987)

`ST.EXE`, built with Microsoft C 1986 and Amber Systems' tools, with its text and tables in
`ST.*` files and twenty CGA pictures in `.BIN` files. It keeps a log of the mission in `ST.LOG`,
which it deletes and writes afresh; the copy that came with the game files, from a 2019 session,
was left out of the repository. It opens with a pause, then its title picture in graphics, then
its credits, each up for a second or two by the clock (it reads DOS's time, and works the
seconds out with the C library's long arithmetic) over an animated starfield, and then the bridge,
laid out as *The Kobayashi Alternative*'s is.

## The speed

The games were written for the IBM PC's 8088 at 4.77 MHz, and the machine runs at its pace,
240,000 instructions a second, rather than the million of the DOSBox setups the games came
with. *The Kobayashi Alternative* times its pauses by counting: each of its credits stays up for
so many passes of an empty loop, `for (j = 0; j < 20000; j++)`, four instructions a pass, about
80 of the 8088's clock cycles. That's a third of a second a pass on the 8088 and a twelfth at a
million instructions a second, at which the credits went by too fast to read. *The Promethean
Prophecy*'s FORTH counts too. *First Contact* goes by the clock, which is the machine's: the
date and time the BIOS and DOS report are the host's when the machine started and the machine's
time since, as the tick count is, so a game timed by the clock and one that counts keep the
same pace at any speed. `--cycles` sets another speed.

## The display

The adapter is IBM's Color Graphics Adapter, as the games expect, rather than a VGA. Its BIOS
leaves alone the functions only later cards have (the EGA's and VGA's palette registers, fonts
and display information), as a CGA's BIOS did, so a program asking for them learns it's on a
CGA.

Its memory is the machine's own, at B8000h: the processor writes it at full speed as plain
memory, and the adapter reads it only when it draws. The BIOS keeps what a program asks for in
the data area at 0040h, as a real one does (the mode, the columns, the cursor's place on each
page, the page's start), and sets the adapter's registers to match, so a program that moves the
cursor or the display's start through the registers directly shows the same as one that asks the
BIOS.

The text is drawn in the PC's own character set, from IBM's ROM fonts as DOSBox has them: the
VGA's 8 by 16 by default, the look these games had on the PCs they were mostly played on, or the
CGA's own 8 by 8. Text blinks every 16 frames when the mode control register says attribute bit 7
blinks, and the cursor every 8. The picture is always 640 pixels wide, and 400 lines with the
VGA's font (a graphics mode's lines drawn twice, as the VGA scanned them) or 200 with the CGA's,
so its size never changes under the player when a game changes mode.
