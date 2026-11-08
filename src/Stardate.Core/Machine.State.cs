namespace Stardate.Core;

// Saved states: the whole machine, written out and read back.
public sealed partial class Machine
{
    /// <summary>
    /// Saves the whole machine as it is between instructions: the processor, memory, every
    /// device, and DOS's open files and searches. Not saved: the files themselves, in the game's
    /// folder and the save folder, which stay where they are.
    /// </summary>
    public void SaveState(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        MachineState.Save(this, [], "", stream);
    }

    /// <summary>
    /// Puts the machine back as <see cref="SaveState"/> saved it. The machine must be made the
    /// same way, for the same game. The files the program had open are opened again, where they
    /// were; one that's gone since fails the load part way, and leaves the machine unfit to
    /// carry on.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The stream is not a saved state, is one saved by a different version, or names a file
    /// that can't be opened again.
    /// </exception>
    public void LoadState(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // the state's open files take the place of the machine's
        MachineState.Load(this, [], _ => _dos.CloseFiles(), stream);
        MapMemory();
    }
}
