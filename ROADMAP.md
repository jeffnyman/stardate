# Roadmap

The games are simple next to *Wing Commander*: 8088 programs for a 128 KB IBM PC with a color
graphics adapter and a speaker. Most of the machine came from Confederation; the steps here are
what these games needed beyond it, and what's left.

## Done

1. **The core, from Confederation.** The 386 (real mode is all the games use), the timer, the
   interrupt controller, the keyboard controller, the BIOS and DOS answered in C#, and saved
   states. Expanded memory, the DPMI host, the second interrupt controller, DMA, the game port,
   the mouse driver, the CD and MSCDEX, batch files, and the AdLib, Sound Blaster and MT-32 are
   gone; the memory is the first megabyte.
2. **`.COM` programs.** DOS loads them as well as MZ executables. *The Promethean Prophecy*,
   sold as a self-starting disk, is started by a 2004 launcher, `RUN.COM`, that loads the game
   from an image of its disk and sends its disk access to the image.
3. **The color graphics adapter.** Text in 40 and 80 columns with its sixteen colors, blinking,
   the cursor, and the display's start address; the status port's retrace bits; and the 320 and
   640 by 200 graphics modes, in which *First Contact* draws its pictures (its `.BIN` files).
   Drawn with the VGA's 8 by 16 font, or the CGA's 8 by 8.
4. **The video BIOS.** Modes, the cursor's place and shape, pages, scrolling windows up and
   down, reading and writing characters and attributes, the teletype, strings, pixels, and
   characters drawn in the graphics modes. DOS's console output goes through it to the screen.
5. **The speaker.** Port 61h and the timer's counter 2, as a tone or as bits toggled directly,
   and the BIOS's beep for a bell character.
6. **The games start.** Each plays from its start to the point where the captain gives orders,
   checked by tests that press the keys a player would (`GameTests`).

7. **The player.** Confederation's SDL3 window, sound, keyboard and menu, on the new core, with
   the three games by short name, `--cga-font`, and saved states. Checked headless, by
   screenshots and an off-screen render of the menu; *still to try in a real window*, with the
   keyboard, the sound, and Ctrl+F5 and Ctrl+F9.
8. **The speed.** The games run at 240,000 instructions a second, the IBM PC's 8088 at 4.77 MHz,
   rather than the million of their DOSBox setups: *The Kobayashi Alternative* and *The
   Promethean Prophecy* time their pauses by counting, and at a million their messages went by
   too fast to read. The date and time the BIOS and DOS report run with the machine's time,
   as the tick count does, rather than the host's, so *First Contact*'s credits, timed by the
   clock, keep pace with the rest.

## Next

9. **Each game played through.** A walkthrough of each (in the manuals' folders) played to its
   end, saving and restoring on the way, with whatever the games turn out to need. *The
   Promethean Prophecy* saves to drive B, its launcher's second disk image; that should be
   checked to land in the save folder and come back.
10. **Releases.** Downloads for Windows, Linux and macOS, as Confederation's are.
