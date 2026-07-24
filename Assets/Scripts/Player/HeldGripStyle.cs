/// <summary>
/// How the right hand shapes itself around a held socket item. Selects the pose clip on the "Item Hold"
/// animator layer and, for <see cref="Pinch"/>, which socket under hand_r the item is seated on.
/// </summary>
public enum HeldGripStyle
{
    /// <summary>Closed fist — thin items gripped through the finger tunnel (key, flashlight, glowstick).</summary>
    Fist = 0,
    /// <summary>Thumb and index fingertips, for flat items (tickets, cards). Seats on PinchSocket_R.</summary>
    Pinch = 1,
    /// <summary>Open C around a cylinder, thumb opposing the fingers (cans, rolls).</summary>
    Cup = 2,
    /// <summary>Hand draped over a sphere, baseball style (the throwable Ball). Seats on BallSocket_R.</summary>
    Ball = 3,
}
