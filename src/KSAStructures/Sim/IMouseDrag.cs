namespace KSAStructures;

/// <summary>A view the player turns by dragging with the right button, the way KSA's own camera turns.</summary>
public interface IMouseDrag
{
    void Press();
    void Release();

    /// <summary>Where the cursor is now, in screen pixels.</summary>
    void Move(double x, double y);

    /// <summary>Wheel notches; positive is in.</summary>
    void Scroll(double notches);

    /// <summary>True while the button is down and the cursor has moved far enough to be a drag.</summary>
    bool Dragging { get; }
}
