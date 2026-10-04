namespace KSAStructures;

/// <summary>
/// What the player sets, for the whole session. Fields rather than properties, because the panel
/// binds them by <c>ref</c> and the bridge sets them by name.
/// </summary>
public sealed class Settings
{
    /// <summary>Whether a kitten on foot is seen through its own eyes. V toggles it.</summary>
    public bool FirstPerson;

    /// <summary>Whether the office garage's ceiling lamps are lit.</summary>
    public bool OfficeLamps = true;

    /// <summary>Log at debug level. Off by default: a bug report wants it, a session does not.</summary>
    public bool VerboseLog;
}
